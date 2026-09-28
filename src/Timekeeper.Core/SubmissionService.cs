using System.Text.Json;

namespace Timekeeper.Core;

/// <summary>One reviewed row per request. Every possible side effect has a durable pending journal first.</summary>
public sealed class SubmissionService(TimecardApi api, SessionStore store)
{
    public async Task<SubmissionReceipt> SubmitAsync(ReadSession session, ProposalEnvelope proposal, IReadOnlyList<VerifiedRow> reviewedRows, CancellationToken ct = default, bool warningsAcknowledged = false)
    {
        using var localLock = store.AcquireSubmissionLock();
        if (session.Demo) throw new InvalidOperationException("Demo sessions cannot write to Quickbase.");
        if (!Same(session.Settings, api.Settings)) throw new InvalidOperationException("Account or policy settings changed. Read the day again before submitting.");
        if (reviewedRows.Count == 0) throw new InvalidOperationException("There are no new rows to submit.");
        var validation = Rules.Validate(session, proposal);
        if (!validation.IsValid) throw new InvalidOperationException("The proposal is no longer valid: " + string.Join(" ", validation.Errors));
        if (validation.Warnings.Count > 0 && !warningsAcknowledged)
            throw new InvalidOperationException("Review and acknowledge every proposal warning before writing to Quickbase.");
        if (!SameRows(reviewedRows, validation.Rows)) throw new InvalidOperationException("The reviewed rows changed. Verify and review the proposal again.");
        var history = store.LoadReceipts(session.Settings.ProfileKey);
        CheckHistory(session, reviewedRows, history);
        await CheckFreshAsync(session, reviewedRows, ct);

        // Preserve the exact baseline needed for recovery before writing any receipt or remote row.
        store.SaveSession(session);
        var receipt = new SubmissionReceipt
        {
            SessionId = session.SessionId, ProfileKey = session.Settings.ProfileKey,
            ReviewedWarnings = validation.Warnings.ToList(),
            WarningsAcknowledgedAtUtc = validation.Warnings.Count > 0 ? DateTimeOffset.UtcNow : null,
            Rows = reviewedRows.Select(r => new RowOutcome { Row = r, Status = "not_sent", Message = "Not sent." }).ToList()
        };
        store.SaveReceipt(receipt);
        for (var i = 0; i < receipt.Rows.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                receipt.Message = "Submission stopped before sending the next row.";
                break;
            }
            // Detect edits by another window/tool between individual writes, accounting for our known creates.
            try { await CheckExistingDuringSubmissionAsync(session, receipt, receipt.Rows[i].Row.Date, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or OperationCanceledException)
            {
                receipt.Message = "Fresh Quickbase data could not be confirmed. Remaining rows were not sent; read again after resolving this receipt.";
                break;
            }
            receipt.Rows[i].Status = "pending";
            receipt.Rows[i].Message = "A write is about to be sent. If interrupted, reconcile before another submission.";
            // If this fails, control never reaches the network write.
            store.SaveReceipt(receipt);
            receipt.Rows[i] = await api.CreateRowAsync(receipt.Rows[i].Row, ct);
            try { store.SaveReceipt(receipt); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("A write may have succeeded but its result could not be saved. Stop and reconcile the pending receipt before any further write.");
            }
            if (receipt.Rows[i].Status != "created") break;
        }
        Finish(receipt);
        store.SaveReceipt(receipt);
        return receipt;
    }

    // Existing simple callers may use this only when all Toggl sources are new. The full proposal overload
    // is necessary for already-recorded choices and is what the desktop UI uses.
    public Task<SubmissionReceipt> SubmitAsync(ReadSession session, IReadOnlyList<VerifiedRow> rows, CancellationToken ct = default)
    {
        var proposal = new ProposalEnvelope
        {
            SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId,
            Rows = rows.Where(r => r.Kind == "work").Select(r => new ProposalRow
            {
                Date = r.Date, SourceEntryIds = r.SourceEntryIds, Assignment = r.Assignment, Project = r.Project,
                Task = r.Task, Category = r.Category, Description = r.Description, Hours = r.Hours
            }).ToList()
        };
        return SubmitAsync(session, proposal, rows, ct);
    }

    public async Task<SubmissionReceipt> ReconcileAsync(ReadSession session, SubmissionReceipt receipt, CancellationToken ct = default)
    {
        using var localLock = store.AcquireSubmissionLock();
        if (session.Demo || session.Settings.ProfileKey != api.Settings.ProfileKey || receipt.ProfileKey != session.Settings.ProfileKey || receipt.SessionId != session.SessionId)
            throw new InvalidOperationException("Use the original session and account to reconcile this receipt.");
        // Load authoritative journal rather than accepting a caller's edited status.
        var saved = store.LoadReceipts(receipt.ProfileKey).SingleOrDefault(r => r.SubmissionId == receipt.SubmissionId)
            ?? throw new InvalidOperationException("The original submission receipt is missing.");
        var baseline = store.LoadSession(saved.SessionId) ?? throw new InvalidOperationException("The original session baseline is missing.");
        await api.TestAsync(ct);
        var uncertain = saved.Rows.Where(r => r.Status is "pending" or "unknown").ToList();
        var claimed = store.LoadReceipts(receipt.ProfileKey).SelectMany(r => r.Rows).Where(r => r.RecordId.HasValue).Select(r => r.RecordId!.Value).ToHashSet();
        foreach (var group in uncertain.GroupBy(r => r.Row.Date))
        {
            var existing = await api.ReadExistingAsync(group.Key, ct);
            var original = baseline.Days.Single(d => d.Date == group.Key).Existing.Select(r => r.RecordId).ToHashSet();
            foreach (var outcome in group)
            {
                var matches = existing.Where(r => !original.Contains(r.RecordId) && !claimed.Contains(r.RecordId) && Matches(outcome.Row, r)).ToList();
                var competing = group.Count(other => SameFields(other.Row, outcome.Row));
                if (matches.Count == 1 && competing == 1)
                {
                    outcome.RecordId = matches[0].RecordId;
                    outcome.Status = "created";
                    outcome.Message = "Reconciled: exactly one new matching Quickbase record was found.";
                    claimed.Add(matches[0].RecordId);
                }
                else
                {
                    outcome.Status = "unknown";
                    outcome.Message = matches.Count == 0 ? "No matching new record was found. Outcome remains unknown; do not replay. Inspect Quickbase with your administrator." : "More than one row or record could match. Outcome remains unknown; do not replay. Inspect Quickbase with your administrator.";
                }
            }
        }
        saved.Message = "";
        Finish(saved);
        store.SaveReceipt(saved);
        return saved;
    }

    private static void CheckHistory(ReadSession session, IReadOnlyList<VerifiedRow> rows, List<SubmissionReceipt> history)
    {
        if (history.Any(r => r.SessionId == session.SessionId)) throw new InvalidOperationException("This session already has a submission receipt. Reconcile it if needed, then read the day again. A session cannot be submitted twice.");
        var dates = session.Days.Select(d => d.Date).ToHashSet();
        if (history.SelectMany(r => r.Rows).Any(r => dates.Contains(r.Row.Date) && r.Status is "pending" or "unknown"))
            throw new InvalidOperationException("An earlier write on these dates has an unknown outcome. Reconcile its receipt before submitting.");
        var sourceIds = rows.SelectMany(r => r.SourceEntryIds).ToHashSet();
        if (history.SelectMany(r => r.Rows).Where(r => r.Status is "pending" or "unknown").SelectMany(r => r.Row.SourceEntryIds).Any(sourceIds.Contains))
            throw new InvalidOperationException("A Toggl entry has an unresolved earlier write, even if its date has since changed. Reconcile that receipt before submitting.");
        if (history.SelectMany(r => r.Rows).Where(r => r.Status == "created").SelectMany(r => r.Row.SourceEntryIds).Any(sourceIds.Contains))
            throw new InvalidOperationException("A Toggl entry in this proposal has already been written by Timekeeper. Read again and mark it as already recorded.");
    }

    private async Task CheckFreshAsync(ReadSession session, IReadOnlyList<VerifiedRow> rows, CancellationToken ct)
    {
        await api.TestAsync(ct);
        var reference = await api.ReadReferenceAsync(ct);
        foreach (var row in rows)
        {
            var task = reference.Tasks.SingleOrDefault(t => t.Id == row.Task);
            var category = reference.Categories.SingleOrDefault(c => c.Id == row.Category);
            if (task is null || category is null || task.CategoryId != row.Category || task.Name != row.TaskName || category.Name != row.CategoryName)
                throw Stale("Quickbase task or category");
        }
        foreach (var id in rows.Select(r => r.Project).Distinct())
        {
            var project = await api.ReadProjectAsync(id, ct);
            if (project is null || rows.Any(r => r.Project == id && r.ProjectName != project.Name)) throw Stale("Quickbase project");
        }
        foreach (var id in rows.Where(r => r.Assignment.HasValue).Select(r => r.Assignment!.Value).Distinct())
        {
            var original = session.Reference.Assignments.SingleOrDefault(a => a.Id == id);
            var assignment = await api.ReadAssignmentAsync(id, ct);
            if (original is null || assignment is null || !Same(original, assignment)) throw Stale("Quickbase assignment");
        }
        if (rows.Any(r => r.Kind != "work") && !Same(session.Reference.InternalProject, reference.InternalProject)) throw Stale("Internal project");
        var allEntries = await api.ReadEntriesRangeAsync(session.Days.Min(d => d.Date), session.Days.Max(d => d.Date), ct);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(session.Settings.TimeZoneId);
        foreach (var day in session.Days)
        {
            var entries = allEntries.Where(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Start, timezone).DateTime) == day.Date);
            if (!Same(day.Entries.OrderBy(e => e.Id), entries.OrderBy(e => e.Id))) throw Stale("Toggl entries");
            var existing = await api.ReadExistingAsync(day.Date, ct);
            if (!Same(day.Existing.OrderBy(e => e.RecordId), existing.OrderBy(e => e.RecordId))) throw Stale("Existing Quickbase timecards");
        }
    }

    private async Task CheckExistingDuringSubmissionAsync(ReadSession session, SubmissionReceipt receipt, DateOnly date, CancellationToken ct)
    {
        var expected = session.Days.Single(d => d.Date == date).Existing.ToList();
        expected.AddRange(receipt.Rows.Where(r => r.Status == "created" && r.Row.Date == date).Select(r => new ExistingTimecard(r.RecordId!.Value, r.Row.Date, r.Row.Hours, r.Row.Project, r.Row.Task, r.Row.Category, r.Row.Assignment, r.Row.Description)));
        var actual = await api.ReadExistingAsync(date, ct);
        if (!Same(expected.OrderBy(r => r.RecordId), actual.OrderBy(r => r.RecordId))) throw Stale("Existing Quickbase timecards");
    }
    private static InvalidOperationException Stale(string what) => new($"{what} changed since the export. Read the day again and create a new proposal before writing.");
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options);
    private static bool SameRows(IReadOnlyList<VerifiedRow> left, IReadOnlyList<VerifiedRow> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => Same(pair.First with { RowId = "" }, pair.Second with { RowId = "" }));
    private static bool SameFields(VerifiedRow a, VerifiedRow b) => a.Date == b.Date && a.Hours == b.Hours && a.Project == b.Project && a.Task == b.Task && a.Category == b.Category && a.Assignment == b.Assignment && a.Description == b.Description;
    private static bool Matches(VerifiedRow row, ExistingTimecard record) => row.Date == record.Date && row.Hours == record.Hours && row.Project == record.Project && row.Task == record.Task && row.Category == record.Category && row.Assignment == record.Assignment && row.Description == record.Description;
    private static void Finish(SubmissionReceipt receipt)
    {
        receipt.FinishedAtUtc = DateTimeOffset.UtcNow;
        receipt.Status = receipt.Rows.Any(r => r.Status is "pending" or "unknown") ? "unknown" : receipt.Rows.All(r => r.Status == "created") ? "complete" : "partial";
        if (string.IsNullOrEmpty(receipt.Message)) receipt.Message = receipt.Status switch
        {
            "complete" => "Every row was confirmed created in Quickbase.",
            "unknown" => "At least one row has an unknown outcome. Reconcile before another write on these dates.",
            _ => "Only rows marked created were confirmed. Review the receipt and read again before preparing remaining work."
        };
    }
}
