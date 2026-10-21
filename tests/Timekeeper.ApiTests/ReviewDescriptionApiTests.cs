using System.Text.Json;
using Timekeeper.Core;

static class ReviewDescriptionApiTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("description submission rejects unreviewed stale and invalid edits before any write", async () =>
        {
            using var f = new Fixture(); var session = await f.Read(); var proposal = f.Proposal(session);
            var baseline = Rules.Validate(session, proposal); var row = baseline.Rows.First(r => r.Kind == "work");
            var edit = new ReviewDescription(ReviewDurations.Key(row), row.Description, "Researched the case chronology.");
            var reviewed = ReviewDescriptions.Apply(session, baseline, [edit]); Require(reviewed.IsValid);
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, reviewed.Rows, warningsAcknowledged: true));
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, reviewed.Rows, descriptionEdits: [edit]));
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, baseline.Rows, warningsAcknowledged: true, descriptionEdits: [edit]));
            foreach (var invalid in new[] { edit with { RowKey = "unknown" }, edit with { OriginalDescription = "Changed source" }, edit with { Description = " " }, edit with { Description = "Bad\0text" } })
                await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, reviewed.Rows, warningsAcknowledged: true, descriptionEdits: [invalid]));
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, reviewed.Rows, warningsAcknowledged: true, descriptionEdits: [edit, edit]));
            Equal(0, f.Server.WriteCount); Equal(0, f.Store.LoadReceipts().Count);
            var source = JsonSerializer.Serialize(session); var originalProposal = JsonSerializer.Serialize(proposal);
            var receipt = await f.Service.SubmitAsync(session, proposal, reviewed.Rows, warningsAcknowledged: true, descriptionEdits: [edit]);
            Equal("complete", receipt.Status); Equal(edit.Description, f.Server.Existing.First().Description);
            var saved = f.Store.LoadReceipts().Single(); var savedRow = saved.Rows.Single(r => r.Row.Kind == "work").Row;
            Equal(edit.Description, savedRow.Description); Require(savedRow.SourceEntryIds.SequenceEqual(row.SourceEntryIds));
            Require(saved.ReviewedWarnings.Any(w => w.Contains("description changed in review")));
            Equal(source, JsonSerializer.Serialize(session)); Equal(source, JsonSerializer.Serialize(f.Store.LoadSession(session.SessionId)));
            Equal(originalProposal, JsonSerializer.Serialize(proposal));
        });
        await check("edited email descriptions combine with local hours and reconcile exact submitted text", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            var baseline = Rules.Validate(session, proposal); var row = baseline.Rows.Single(); var source = JsonSerializer.Serialize(session.EmailWorkbook);
            var durations = new[] { new ReviewDuration(ReviewDurations.Key(row), row.Hours, .5m) };
            var descriptions = new[] { new ReviewDescription(ReviewDurations.Key(row), row.Description, "Followed up on case intake and confirmed next steps.") };
            var reviewed = ReviewDescriptions.Apply(session, ReviewDurations.Apply(session, baseline, durations), descriptions); Require(reviewed.IsValid);
            f.Server.WriteMode = "commit-timeout";
            var receipt = await f.Service.SubmitAsync(session, proposal, reviewed.Rows, warningsAcknowledged: true, durationEdits: durations, descriptionEdits: descriptions);
            Equal("unknown", receipt.Status); Equal(1, f.Server.WriteCount); Equal(descriptions[0].Description, f.Server.Existing.Single().Description);
            var saved = f.Store.LoadReceipts().Single(); Equal(descriptions[0].Description, saved.Rows.Single().Row.Description);
            var reconciled = await f.Service.ReconcileAsync(session, saved);
            Equal("complete", reconciled.Status); Equal("created", reconciled.Rows.Single().Status); Equal<int?>(501, reconciled.Rows.Single().RecordId);
            var actual = reconciled.Rows.Single().Row; Equal(.5m, actual.Hours);
            Require(actual.SourceActivityIds.SequenceEqual(row.SourceActivityIds)); Require(actual.EmailEvidenceKeys.SequenceEqual(row.EmailEvidenceKeys));
            Equal(descriptions[0].Description, actual.Description); Equal(source, JsonSerializer.Serialize(f.Store.LoadSession(session.SessionId)!.EmailWorkbook));
            Equal(row.Description, proposal.Rows.Single().Description); Equal(0, f.Transport.TogglRequests); Equal(1, f.Server.WriteCount);
        });
        await check("manual activity descriptions remain attached to their stable local ID after review", async () =>
        {
            using var f = new Fixture(); var session = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day);
            var entry = new ManualTimecard { Date = Fixture.Day, Hours = .25m, Project = 100, Task = 44, Description = "Original research notes" };
            session = session with { ManualTimecards = [entry] };
            var proposal = new ProposalEnvelope { SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId };
            var baseline = Rules.Validate(session, proposal); var row = baseline.Rows.Single();
            var edits = new[] { new ReviewDescription(ReviewDurations.Key(row), row.Description, "Researched case background for the intake call.") };
            var reviewed = ReviewDescriptions.Apply(session, baseline, edits); Require(reviewed.IsValid);
            var receipt = await f.Service.SubmitAsync(session, proposal, reviewed.Rows, warningsAcknowledged: true, descriptionEdits: edits);
            Equal("complete", receipt.Status); Equal(entry.Id, receipt.Rows.Single().Row.ManualEntryIds.Single()); Equal(edits[0].Description, f.Server.Existing.Single().Description);
            Equal(entry.Description, f.Store.LoadSession(session.SessionId)!.ManualTimecards.Single().Description);
            var fresh = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day);
            fresh = fresh with { ManualTimecards = [entry with { Description = "Another edit" }] };
            var replay = new ProposalEnvelope { SessionId = fresh.SessionId, EmployeeId = fresh.Settings.EmployeeId };
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, replay, Rules.Validate(fresh, replay).Rows, warningsAcknowledged: true));
            Equal(1, f.Server.WriteCount);
        });
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Require(bool condition) { if (!condition) throw new Exception("Expected true"); }
}
