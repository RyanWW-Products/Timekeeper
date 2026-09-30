using System.Text.Json;

namespace Timekeeper.Core;

/// <summary>One reviewed row per request. Every possible side effect has a durable pending journal first.</summary>
public sealed class SubmissionService(TimecardApi api, SessionStore store)
{
    public async Task<SubmissionReceipt> SubmitAsync(ReadSession session, ProposalEnvelope proposal, IReadOnlyList<VerifiedRow> reviewedRows, CancellationToken ct = default, bool warningsAcknowledged = false, IReadOnlyCollection<DateOnly>? selectedDates = null)
    {
        using var localLock = store.AcquireSubmissionLock();
        if (session.Demo) throw new InvalidOperationException("Demo sessions cannot write to Quickbase.");
        if (!Same(session.Settings, api.Settings)) throw new InvalidOperationException("Account or policy settings changed. Read the day again before submitting.");
        if (reviewedRows.Count == 0) throw new InvalidOperationException("There are no new rows to submit.");
        var scope = ProposalScope.Select(session, proposal, selectedDates);
        var validation = Rules.Validate(scope.Session, scope.Proposal);
        if (!validation.IsValid) throw new InvalidOperationException("The proposal is no longer valid: " + string.Join(" ", validation.Errors));
        if (validation.Warnings.Count > 0 && !warningsAcknowledged)
            throw new InvalidOperationException("Review and acknowledge every proposal warning before writing to Quickbase.");
        if (!SameRows(reviewedRows, validation.Rows)) throw new InvalidOperationException("The reviewed rows changed. Verify and review the proposal again.");
        var history = store.LoadReceipts(session.Settings.ProfileKey);
        var recovered = CheckHistory(scope.Session, reviewedRows, history);
        await CheckFreshAsync(scope.Session, reviewedRows, ct);
        await CheckRecoveredStillAbsentAsync(recovered, ct);

        // Preserve the exact baseline needed for recovery before writing any receipt or remote row.
        store.SaveSession(session);
        var receipt = new SubmissionReceipt
        {
            SessionId = session.SessionId, ProfileKey = session.Settings.ProfileKey,
            SelectedDates = selectedDates?.Order().ToList(),
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
            try
            {
                await CheckExistingDuringSubmissionAsync(scope.Session, receipt, receipt.Rows[i].Row.Date, ct);
                await CheckRecoveredStillAbsentAsync(recovered, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or OperationCanceledException)
            {
                receipt.Message = $"Stopped before row {i + 1} ({receipt.Rows[i].Row.Date:yyyy-MM-dd}): {ex.Message}\nThis row and all remaining rows were not sent. Read again and tell Copilot which entries this receipt confirms were created.";
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
        if (uncertain.Count == 0) return saved;
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

    public async Task<IReadOnlyList<RowOutcome>> FindMissingCreatedEntriesAsync(ReadSession session, SubmissionReceipt receipt, CancellationToken ct = default)
    {
        using var localLock = store.AcquireSubmissionLock();
        var saved = LoadRecoveryReceipt(session, receipt);
        var candidates = saved.Rows.Where(CanRecover).ToList();
        if (candidates.Count == 0) return [];
        await api.TestAsync(ct);
        var found = await api.ReadTimecardRecordIdsAsync(candidates.Select(r => r.RecordId!.Value), ct);
        return candidates.Where(r => !found.Contains(r.RecordId!.Value)).ToList();
    }

    public async Task<SubmissionReceipt> ConfirmDeletedEntriesAsync(ReadSession session, SubmissionReceipt receipt, IReadOnlyCollection<string> rowIds, bool userConfirmed, CancellationToken ct = default)
    {
        using var localLock = store.AcquireSubmissionLock();
        if (!userConfirmed || rowIds.Count == 0 || rowIds.Distinct().Count() != rowIds.Count)
            throw new InvalidOperationException("Select the entries you intentionally deleted and confirm before allowing a rewrite.");
        var saved = LoadRecoveryReceipt(session, receipt);
        var selected = saved.Rows.Where(r => rowIds.Contains(r.Row.RowId)).ToList();
        if (selected.Count != rowIds.Count || selected.Any(r => !CanRecover(r)))
            throw new InvalidOperationException("Only confirmed created work entries from this saved receipt can be recovered. Check the receipt again.");
        await api.TestAsync(ct);
        await CheckRecoveredStillAbsentAsync(selected, ct);
        ct.ThrowIfCancellationRequested();
        var confirmedAt = DateTimeOffset.UtcNow;
        foreach (var row in selected) row.DeletionConfirmedAtUtc = confirmedAt;
        // Preserve the original result, message, IDs and finish time; append only the deletion audit.
        saved.Status = "reopened";
        store.SaveReceipt(saved);
        return saved;
    }

    private SubmissionReceipt LoadRecoveryReceipt(ReadSession session, SubmissionReceipt receipt)
    {
        if (session.Demo || !Same(session.Settings, api.Settings) || receipt.ProfileKey != api.Settings.ProfileKey || receipt.SessionId != session.SessionId)
            throw new InvalidOperationException("Use the original session and account to recover deleted entries.");
        var saved = store.LoadReceipts(api.Settings.ProfileKey).SingleOrDefault(r => r.SubmissionId == receipt.SubmissionId)
            ?? throw new InvalidOperationException("The original submission receipt is missing.");
        var baseline = store.LoadSession(saved.SessionId) ?? throw new InvalidOperationException("The original session baseline is missing.");
        if (baseline.Demo || saved.SessionId != session.SessionId || !Same(baseline.Settings, api.Settings))
            throw new InvalidOperationException("The saved receipt does not match the original account and session.");
        if (saved.Rows.Any(r => r.Status is "pending" or "unknown"))
            throw new InvalidOperationException("This receipt has an unknown write outcome. Check the selected result before recovering deleted entries.");
        return saved;
    }

    private static bool CanRecover(RowOutcome row) => row.Status == "created" && row.RecordId > 0 && row.Row.Kind == "work" && row.Row.SourceEntryIds.Count > 0 && row.DeletionConfirmedAtUtc is null;

    private async Task CheckRecoveredStillAbsentAsync(IReadOnlyList<RowOutcome> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var found = await api.ReadTimecardRecordIdsAsync(rows.Select(r => r.RecordId ?? throw new InvalidDataException("A recovered entry is missing its original record ID.")), ct);
        if (found.Count > 0)
            throw new InvalidOperationException($"Quickbase record #{found.Min()} still exists or has been restored. It cannot be rewritten. Check that record, then read the day again.");
    }

    private static List<RowOutcome> CheckHistory(ReadSession session, IReadOnlyList<VerifiedRow> rows, List<SubmissionReceipt> history)
    {
        if (history.Any(r => r.SessionId == session.SessionId)) throw new InvalidOperationException("This session already has a submission receipt. Reconcile it if needed, then read the day again. A session cannot be submitted twice.");
        var dates = session.Days.Select(d => d.Date).ToHashSet();
        if (history.SelectMany(r => r.Rows).Any(r => dates.Contains(r.Row.Date) && r.Status is "pending" or "unknown"))
            throw new InvalidOperationException("An earlier write on these dates has an unknown outcome. Reconcile its receipt before submitting.");
        var sourceIds = rows.SelectMany(r => r.SourceEntryIds).ToHashSet();
        if (history.SelectMany(r => r.Rows).Where(r => r.Status is "pending" or "unknown").SelectMany(r => r.Row.SourceEntryIds).Any(sourceIds.Contains))
            throw new InvalidOperationException("A Toggl entry has an unresolved earlier write, even if its date has since changed. Reconcile that receipt before submitting.");
        var priorCreates = history.SelectMany(r => r.Rows).Where(r => r.Status == "created" && r.Row.SourceEntryIds.Any(sourceIds.Contains)).ToList();
        var blocked = priorCreates.FirstOrDefault(r => r.DeletionConfirmedAtUtc is null);
        if (blocked is not null)
            throw new InvalidOperationException($"A Toggl entry in this proposal was already written as Quickbase record #{blocked.RecordId} ({blocked.Row.Date:yyyy-MM-dd}). If it still exists, read again and mark it as already recorded. If you intentionally deleted it, open Submission history, select its receipt, and choose Recover deleted entries.");
        foreach (var row in priorCreates)
        {
            if (row.RecordId is not > 0 || row.Row.Kind != "work" || row.DeletionConfirmedAtUtc > DateTimeOffset.UtcNow)
                throw new InvalidDataException("A saved deletion confirmation is invalid. Restore the receipt before writing.");
            if (session.GeneratedAtUtc <= row.DeletionConfirmedAtUtc)
                throw new InvalidOperationException("Read the day again after recovering deleted entries, then request a new Copilot proposal before writing.");
        }
        return priorCreates;
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
            CheckExistingUnchanged(day.Existing, existing, day.Date);
        }
    }

    private async Task CheckExistingDuringSubmissionAsync(ReadSession session, SubmissionReceipt receipt, DateOnly date, CancellationToken ct)
    {
        var expected = session.Days.Single(d => d.Date == date).Existing.ToList();
        expected.AddRange(receipt.Rows.Where(r => r.Status == "created" && r.Row.Date == date).Select(r => new ExistingTimecard(r.RecordId!.Value, r.Row.Date, r.Row.Hours, r.Row.Project, r.Row.Task, r.Row.Category, r.Row.Assignment, r.Row.Description)));
        var actual = await api.ReadExistingAsync(date, ct);
        CheckExistingUnchanged(expected, actual, date);
    }
    private static void CheckExistingUnchanged(IReadOnlyList<ExistingTimecard> expected, IReadOnlyList<ExistingTimecard> actual, DateOnly date)
    {
        // Record equality compares decimal values, so 3, 3.0 and 3.00 hours are equal.
        // JSON text equality incorrectly treats the service's decimal formatting as an edit.
        if (expected.OrderBy(r => r.RecordId).SequenceEqual(actual.OrderBy(r => r.RecordId))) return;
        var actualById = actual.ToDictionary(r => r.RecordId);
        foreach (var before in expected.OrderBy(r => r.RecordId))
        {
            if (!actualById.TryGetValue(before.RecordId, out var after))
                throw Changed($"record #{before.RecordId} is missing from the current read.");
            if (before == after) continue;
            var fields = new List<string>();
            if (before.Date != after.Date) fields.Add("Date");
            if (before.Hours != after.Hours) fields.Add("Hours");
            if (before.Project != after.Project) fields.Add("Project");
            if (before.Task != after.Task) fields.Add("Task");
            if (before.Category != after.Category) fields.Add("Category");
            if (before.Assignment != after.Assignment) fields.Add("Assignment");
            if (before.Description != after.Description) fields.Add("Description");
            // Identify the fields without echoing potentially sensitive descriptions or response bodies.
            throw Changed($"record #{before.RecordId} has changed fields: {string.Join(", ", fields)}.");
        }
        var expectedIds = expected.Select(r => r.RecordId).ToHashSet();
        var added = actual.First(r => !expectedIds.Contains(r.RecordId));
        throw Changed($"record #{added.RecordId} was added outside this submission.");

        InvalidOperationException Changed(string detail) => new($"Quickbase timecards for {date:yyyy-MM-dd} changed: {detail} Read the day again and create a new proposal before writing.");
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
        receipt.Status = receipt.Rows.Any(r => r.Status is "pending" or "unknown") ? "unknown" : receipt.Rows.Any(r => r.DeletionConfirmedAtUtc.HasValue) ? "reopened" : receipt.Rows.All(r => r.Status == "created") ? "complete" : "partial";
        if (string.IsNullOrEmpty(receipt.Message)) receipt.Message = receipt.Status switch
        {
            "complete" => "Every row was confirmed created in Quickbase.",
            "unknown" => "At least one row has an unknown outcome. Reconcile before another write on these dates.",
            "reopened" => "Deletion was confirmed for selected entries. Read the day again to prepare their replacements. The original creation results remain below.",
            _ => "Only rows marked created were confirmed. Review the receipt and read again before preparing remaining work."
        };
    }
}
