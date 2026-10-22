using System.Net;
using System.Text.Json;
using Timekeeper.Core;

static class SubmissionResumeTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("saved partial receipts resume after restart without replaying created rows or changing baseline", async () =>
        {
            using var f = new Fixture(); var (session, receipt) = await Partial(f);
            var baseline = JsonSerializer.Serialize(f.Store.LoadSession(session.SessionId));
            var original = JsonSerializer.Serialize(receipt.Rows[0]); var id = receipt.SubmissionId;
            f.Server.BeforeWrite = () =>
            {
                var journal = f.Store.LoadReceipts().Single();
                Equal(1, journal.Rows.Count(r => r.Status == "pending"));
                Equal(f.Server.WriteCount, journal.Rows.Count(r => r.Status == "created"));
            };
            var restartedStore = new SessionStore(f.Store.RootPath);
            var result = await new SubmissionService(f.Api, restartedStore).ResumeAsync(restartedStore.LoadReceipts().Single());
            Equal("complete", result.Status); Equal(id, result.SubmissionId); Equal(3, f.Server.WriteCount);
            Equal(1, f.Server.Existing.Count(r => r.RecordId == 501)); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
            Equal(original, JsonSerializer.Serialize(result.Rows[0])); Equal(1, f.Store.LoadReceipts().Count);
            Equal(baseline, JsonSerializer.Serialize(f.Store.LoadSession(session.SessionId)));
            await Throws(() => f.Service.ResumeAsync(result)); Equal(3, f.Server.WriteCount);
        });
        await check("email continuation preserves edited descriptions hours billing and fill without Toggl", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            var emailEdits = new EmailReviewEdits { AddTimecards = true, FillWeekdays = true, TargetHours = 8m,
                ActivityHours = [new(session.EmailWorkbook!.Activities[0].ActivityId, 10, 1.5m)] };
            var original = Rules.Validate(session, proposal, emailEdits: emailEdits); var work = original.Rows.Single(r => r.Kind == "work");
            var durations = new[] { new ReviewDuration(ReviewDurations.Key(work), work.Hours, 1.5m) };
            var descriptions = new[] { new ReviewDescription(ReviewDurations.Key(work), work.Description, "Held the client consultation.\r\n\r\nReviewed Materials") };
            var reviewed = ReviewDescriptions.Apply(session, ReviewDurations.Apply(session, original, durations), descriptions);
            var rows = reviewed.Rows.Select(r => r with { BillableOverride = r.Kind == "work" }).ToList();
            f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.ServiceUnavailable; };
            var receipt = await f.Service.SubmitAsync(session, proposal, rows, warningsAcknowledged: true, durationEdits: durations, descriptionEdits: descriptions, emailEdits: emailEdits);
            Equal("partial", receipt.Status); Equal(1, f.Server.WriteCount);
            var originalRows = JsonSerializer.Serialize(receipt.Rows.Select(r => r.Row));
            var source = JsonSerializer.Serialize(f.Store.LoadSession(session.SessionId));
            f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK;
            f.Server.Existing[0] = f.Server.Existing[0] with { Description = descriptions[0].Description.Replace("\r\n", "\n") };
            var result = await f.Service.ResumeAsync(receipt);
            Equal("complete", result.Status); Equal(3, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
            Equal(originalRows, JsonSerializer.Serialize(result.Rows.Select(r => r.Row)));
            Equal(source, JsonSerializer.Serialize(f.Store.LoadSession(session.SessionId)));
            Equal(8m, f.Server.Existing.Sum(r => r.Hours)); Equal<bool?>(true, result.Rows[0].ActualBillable);
            Require(result.Rows.Skip(1).All(r => r.ActualBillable == false));
            Require(result.WarningsAcknowledgedAtUtc == receipt.WarningsAcknowledgedAtUtc);
        });
        await check("continuation blocks real changes missing records and outside additions without changing receipt", async () =>
        {
            Action<FakeApi>[] changes = [
                server => server.Existing[0] = server.Existing[0] with { Description = "Changed content" },
                server => server.Existing[0] = server.Existing[0] with { Hours = 1.25m },
                server => server.Existing[0] = server.Existing[0] with { Project = 999 },
                server => server.Existing.Clear(),
                server => server.Existing.Add(new(990, Fixture.Day, .5m, 100, 1, 10, null, "Outside work")),
                server => server.ChangeDescription = true,
                server => server.RowsByTable["bd3bsxtbn"] = [FakeApi.Row((3, 44), (7, "Internal work"), (11, true), (17, 20))],
                server => server.WrongIdentity = true,
                server => server.ExistingReadStatus = HttpStatusCode.Forbidden
            ];
            foreach (var change in changes)
            {
                using var f = new Fixture(); var (_, receipt) = await Partial(f);
                var saved = JsonSerializer.Serialize(f.Store.LoadReceipts().Single()); change(f.Server);
                await Throws(() => f.Service.ResumeAsync(receipt)); Equal(1, f.Server.WriteCount);
                Equal(saved, JsonSerializer.Serialize(f.Store.LoadReceipts().Single()));
            }
        });
        await check("normalization does not hide whitespace text case or HTML description edits", async () =>
        {
            foreach (var changed in new[] { "Work completed ", "work completed", "Work  completed", "<p>Work completed</p>", "Work\ncompleted" })
            {
                using var f = new Fixture(); var (_, receipt) = await Partial(f);
                f.Server.Existing[0] = f.Server.Existing[0] with { Description = changed };
                var ex = await Throws(() => f.Service.ResumeAsync(receipt)); Require(ex.Message.Contains("Description")); Equal(1, f.Server.WriteCount);
            }
        });
        await check("caller changes and stale receipt snapshots cannot change saved continuation rows", async () =>
        {
            using var f = new Fixture(); var (_, receipt) = await Partial(f);
            var forged = receipt with { Rows = receipt.Rows.Select(r => r with { Row = r.Row with { Description = "Unreviewed edit" } }).ToList() };
            await Throws(() => f.Service.ResumeAsync(forged)); Equal(1, f.Server.WriteCount);
            var updated = f.Store.LoadReceipts().Single(); updated.Message = "Receipt changed in another window"; f.Store.SaveReceipt(updated);
            await Throws(() => f.Service.ResumeAsync(receipt)); Equal(1, f.Server.WriteCount);
        });
        await check("unknown failed deleted and billing-unconfirmed results cannot be resumed", async () =>
        {
            foreach (var condition in new[] { "pending", "unknown", "failed", "deleted", "billing", "record-id" })
            {
                using var f = new Fixture(); var (_, receipt) = await Partial(f);
                if (condition == "deleted") receipt.Rows[0].DeletionConfirmedAtUtc = DateTimeOffset.UtcNow;
                else if (condition == "billing") receipt.Rows[0] = receipt.Rows[0] with { Row = receipt.Rows[0].Row with { BillableOverride = true }, ActualBillable = false };
                else if (condition == "record-id") receipt.Rows[1].RecordId = 999;
                else receipt.Rows[1].Status = condition;
                f.Store.SaveReceipt(receipt); Require(!SubmissionService.CanResume(receipt));
                await Throws(() => f.Service.ResumeAsync(receipt)); Equal(1, f.Server.WriteCount);
            }
        });
        await check("a timeout during continuation stays unknown until reconciled and is never replayed", async () =>
        {
            using var f = new Fixture(); var (session, receipt) = await Partial(f);
            f.Server.WriteMode = "commit-timeout";
            var uncertain = await f.Service.ResumeAsync(receipt);
            Equal("unknown", uncertain.Status); Equal(2, f.Server.WriteCount);
            await Throws(() => f.Service.ResumeAsync(uncertain)); Equal(2, f.Server.WriteCount);
            var reconciled = await f.Service.ReconcileAsync(session, uncertain); Equal("partial", reconciled.Status);
            f.Server.WriteMode = "success";
            var result = await f.Service.ResumeAsync(reconciled);
            Equal("complete", result.Status); Equal(3, f.Server.WriteCount); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
        });
        await check("continuation checks again between rows and remains repeatable after a read failure", async () =>
        {
            using var f = new Fixture(); var (_, receipt) = await Partial(f);
            f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 2) f.Server.ExistingReadStatus = HttpStatusCode.TooManyRequests; };
            var stopped = await f.Service.ResumeAsync(receipt); Equal("partial", stopped.Status); Equal(2, f.Server.WriteCount);
            Require(stopped.Message.Contains("row 3")); Require(stopped.Message.Contains("429"));
            f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK;
            var result = await f.Service.ResumeAsync(stopped); Equal("complete", result.Status); Equal(3, f.Server.WriteCount);
        });
        await check("uncertain outcomes in another receipt prevent continuation", async () =>
        {
            using var f = new Fixture(); var (_, receipt) = await Partial(f);
            f.Store.SaveReceipt(new() { ProfileKey = receipt.ProfileKey, SessionId = "other-session", Rows = [new() { Row = receipt.Rows[1].Row, Status = "unknown" }] });
            await Throws(() => f.Service.ResumeAsync(receipt)); Equal(1, f.Server.WriteCount);
        });
        await check("cancelled continuation sends no additional rows", async () =>
        {
            using var f = new Fixture(); var (_, receipt) = await Partial(f);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws(() => f.Service.ResumeAsync(receipt, cancellation.Token)); Equal(1, f.Server.WriteCount);
        });
        await check("continuation keeps the original selected dates and ignores excluded dates", async () =>
        {
            using var f = new Fixture(); var (session, proposal) = await f.TwoDateRead(); var selected = Fixture.Day.AddDays(1);
            var scope = ProposalScope.Select(session, proposal, [selected]);
            f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.ServiceUnavailable; };
            var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(scope.Session, scope.Proposal).Rows, selectedDates: [selected]);
            Equal("partial", receipt.Status);
            f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK; f.Server.ExistingReadDates.Clear();
            f.SetTwoDateEntries(firstDescription: "Changed excluded work");
            f.Server.Existing.Add(new(990, Fixture.Day, 1m, 100, 1, 10, null, "Excluded date"));
            var result = await f.Service.ResumeAsync(receipt);
            Equal("complete", result.Status); Equal(3, f.Server.WriteCount); Require(result.Rows.All(r => r.Row.Date == selected));
            Require(f.Server.ExistingReadDates.All(d => d == selected)); Equal(2, f.Store.LoadSession(session.SessionId)!.Days.Count);
        });
        await check("continuation rejects missing baseline demo baseline and unacknowledged warnings", async () =>
        {
            foreach (var condition in new[] { "missing", "demo", "warnings", "date" })
            {
                using var f = new Fixture(); var (session, receipt) = await Partial(f);
                if (condition == "missing") File.Delete(Path.Combine(f.Store.RootPath, "sessions", session.SessionId + ".json"));
                else if (condition == "demo") f.Store.SaveSession(session with { Demo = true });
                else if (condition == "warnings") { receipt = receipt with { ReviewedWarnings = ["Unconfirmed warning"], WarningsAcknowledgedAtUtc = null }; f.Store.SaveReceipt(receipt); }
                else { receipt = receipt with { SelectedDates = [Fixture.Day.AddDays(1)] }; f.Store.SaveReceipt(receipt); }
                await Throws(() => f.Service.ResumeAsync(receipt)); Equal(1, f.Server.WriteCount);
            }
        });
        await check("continuation checks current billing permissions before remaining overrides", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            var edits = new EmailReviewEdits { AddTimecards = true, FillWeekdays = true, TargetHours = 8m };
            var rows = Rules.Validate(session, proposal, emailEdits: edits).Rows.Select(r => r with { BillableOverride = false }).ToList();
            f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.ServiceUnavailable; };
            var receipt = await f.Service.SubmitAsync(session, proposal, rows, warningsAcknowledged: true, emailEdits: edits);
            Equal("partial", receipt.Status);
            f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK; f.Transport.OverrideVisible = false;
            await Throws(() => f.Service.ResumeAsync(receipt)); Equal(1, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
        });
    }

    private static async Task<(ReadSession, SubmissionReceipt)> Partial(Fixture f)
    {
        var session = await f.Read(); var proposal = f.Proposal(session);
        f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.ServiceUnavailable; };
        var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows);
        Equal("partial", receipt.Status); Equal(1, f.Server.WriteCount); Require(SubmissionService.CanResume(receipt));
        f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK;
        return (session, receipt);
    }
    private static async Task<Exception> Throws(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException or OperationCanceledException) { return ex; }
        throw new Exception("Expected continuation to stop before writing.");
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Require(bool condition) { if (!condition) throw new Exception("Expected true"); }
}
