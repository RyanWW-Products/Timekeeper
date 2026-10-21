using System.Text.Json;
using Timekeeper.Core;

static class ManualApiTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("manual baseline and submission need Quickbase only and journal exact local IDs", async () =>
        {
            using var f = new Fixture(); f.Server.WrongToggl = true;
            using var api = new TimecardApi(f.Settings, new("", "test-qb"), new HttpClient(f.Server));
            var service = new SubmissionService(api, f.Store);
            var session = await api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); var manual = Entry();
            session = session with { ManualTimecards = [manual] }; var proposal = Envelope(session); var validation = Rules.Validate(session, proposal); Valid(validation);
            Equal(1, validation.Rows.Count); Equal(.25m, validation.Rows.Single().Hours);
            await Throws<InvalidOperationException>(() => service.SubmitAsync(session, proposal, validation.Rows)); Equal(0, f.Server.WriteCount);
            var receipt = await service.SubmitAsync(session, proposal, validation.Rows, warningsAcknowledged: true);
            Equal("complete", receipt.Status); Equal(manual.Id, receipt.Rows.Single().Row.ManualEntryIds.Single()); Equal(1, f.Server.WriteCount);
            Equal(0, f.Server.CurrentQueries); Equal(0, f.Server.EntryQueries); Require(f.Store.LoadSession(session.SessionId)!.ManualOnly);
        });
        await check("manual IDs block replay across fresh sessions even after editing hours and date", async () =>
        {
            using var f = new Fixture(); var first = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); var manual = Entry();
            first = first with { ManualTimecards = [manual] }; var proposal = Envelope(first);
            await f.Service.SubmitAsync(first, proposal, Rules.Validate(first, proposal).Rows, warningsAcknowledged: true);
            f.Server.FilterExistingDates = true;
            var next = Fixture.Day.AddDays(1); var fresh = await f.Api.ReadManualBaselineAsync(next, next);
            fresh = fresh with { ManualTimecards = [manual with { Date = next, Hours = .5m, Description = "Changed description" }] };
            var nextProposal = Envelope(fresh); var validation = Rules.Validate(fresh, nextProposal); Valid(validation);
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, nextProposal, validation.Rows, warningsAcknowledged: true)); Equal(1, f.Server.WriteCount);
        });
        await check("manual reviewed identity or amount changes cannot reach the create API", async () =>
        {
            using var f = new Fixture(); var session = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); session = session with { ManualTimecards = [Entry()] };
            var proposal = Envelope(session); var rows = Rules.Validate(session, proposal).Rows;
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, [rows[0] with { Hours = .5m }], warningsAcknowledged: true));
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, [rows[0] with { ManualEntryIds = [Guid.NewGuid().ToString("N")] }], warningsAcknowledged: true));
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session with { ManualTimecards = [] }, proposal, rows, warningsAcknowledged: true)); Equal(0, f.Server.WriteCount);
        });
        await check("manual timeout reconciliation and deletion recovery preserve stable IDs without Toggl", async () =>
        {
            using var f = new Fixture(); f.Server.WrongToggl = true;
            var session = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); var manual = Entry(); session = session with { ManualTimecards = [manual] };
            var proposal = Envelope(session); f.Server.WriteMode = "commit-timeout";
            var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true); Equal("unknown", receipt.Status);
            var reconciled = await f.Service.ReconcileAsync(session, receipt); Equal("complete", reconciled.Status); Equal(manual.Id, reconciled.Rows.Single().Row.ManualEntryIds.Single());
            f.Server.Existing.Clear(); var missing = await f.Service.FindMissingCreatedEntriesAsync(session, reconciled); Equal(1, missing.Count);
            var recovered = await f.Service.ConfirmDeletedEntriesAsync(session, reconciled, [missing[0].Row.RowId], true); Equal("reopened", recovered.Status);
            var fresh = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); fresh = fresh with { ManualTimecards = [manual] }; f.Server.WriteMode = "success";
            var replacement = Envelope(fresh); var result = await f.Service.SubmitAsync(fresh, replacement, Rules.Validate(fresh, replacement).Rows, warningsAcknowledged: true);
            Equal("complete", result.Status); Equal(2, f.Server.WriteCount); Equal(0, f.Server.EntryQueries);
        });
        await check("merged manual and imported work retain both replay protections", async () =>
        {
            using var f = new Fixture(); var session = await f.Read(); var proposal = f.Proposal(session);
            var manual = Entry() with { Task = 1, Hours = 7m, Description = proposal.Rows.Single().Description };
            session = session with { ManualTimecards = [manual] }; var validation = Rules.Validate(session, proposal); Valid(validation);
            Require(validation.Rows.Single(r => r.Kind == "work").ManualEntryIds.Contains(manual.Id)); Require(validation.Rows.All(r => r.Kind != "manual"));
            var receipt = await f.Service.SubmitAsync(session, proposal, validation.Rows, warningsAcknowledged: true); Equal(3, f.Server.WriteCount);
            var fresh = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); fresh = fresh with { ManualTimecards = [manual with { Hours = .5m }] };
            var next = Envelope(fresh); await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, next, Rules.Validate(fresh, next).Rows, warningsAcknowledged: true)); Equal(3, f.Server.WriteCount);
        });
        await check("manual carry resolves trusted projects adds baseline dates and preserves session source identity", async () =>
        {
            using var f = new Fixture(); f.Server.FilterExistingDates = true;
            var session = await f.Api.ReadManualBaselineAsync(Fixture.Day, Fixture.Day); var manual = Entry() with { Date = Fixture.Day.AddDays(1), Project = 321 };
            f.Server.ProjectRowsById[321] = [FakeApi.Row((3, 321), (6, "Carried case"))];
            var prepared = await f.Api.PrepareManualEntriesAsync(session, [manual]);
            Equal(session.SessionId, prepared.SessionId); Equal(session.GeneratedAtUtc, prepared.GeneratedAtUtc); Equal(2, prepared.Days.Count);
            Equal(0, session.ManualTimecards.Count); Equal(1, session.Days.Count); Equal("Carried case", prepared.Reference.Projects.Single(p => p.Id == 321).Name);
            Valid(Rules.Validate(prepared, Envelope(prepared))); Equal(0, f.Server.EntryQueries);
            f.Server.ProjectRowsById[321] = []; await Throws<InvalidOperationException>(() => f.Api.PrepareManualEntriesAsync(session, [manual]));
        });
        await check("manual carry rejects unrelated accounts excessive date spans and outside-range Toggl work", async () =>
        {
            using var f = new Fixture(); var session = await f.Read();
            await Throws<InvalidOperationException>(() => f.Api.PrepareManualEntriesAsync(session with { Settings = session.Settings with { Email = "other@example.com" } }, [Entry()]));
            await Throws<InvalidOperationException>(() => f.Api.PrepareManualEntriesAsync(session, [Entry() with { Date = Fixture.Day.AddDays(31) }]));
            f.SetTwoDateEntries(); await Throws<InvalidOperationException>(() => f.Api.PrepareManualEntriesAsync(session, [Entry() with { Date = Fixture.Day.AddDays(1) }])); Equal(0, f.Server.WriteCount);
        });
        await check("manual draft saves reject stale windows and retain the newer saved list", () =>
        {
            using var f = new Fixture(); var secondStore = new SessionStore(f.Store.RootPath); var entry = Entry();
            f.Store.SaveManualDrafts(f.Settings.ProfileKey, [entry], []);
            var firstSnapshot = f.Store.LoadManualDrafts(f.Settings.ProfileKey);
            var secondSnapshot = secondStore.LoadManualDrafts(f.Settings.ProfileKey);
            var edited = entry with { Hours = .5m };
            f.Store.SaveManualDrafts(f.Settings.ProfileKey, [edited], firstSnapshot);
            try { secondStore.SaveManualDrafts(f.Settings.ProfileKey, [], secondSnapshot); throw new Exception("Stale window overwrote newer manual time"); }
            catch (InvalidOperationException error) { Require(error.Message.Contains("Reopen") && error.Message.Contains("not saved")); }
            Equal(edited, secondStore.LoadManualDrafts(f.Settings.ProfileKey).Single());
            secondStore.SaveManualDrafts("other-profile", [entry], []); Equal(entry, secondStore.LoadManualDrafts("other-profile").Single());
            var lockPath = Path.Combine(f.Store.RootPath, "manual-drafts", f.Settings.ProfileKey + ".json.lock");
            using (var busy = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try { secondStore.SaveManualDrafts(f.Settings.ProfileKey, [], [edited]); throw new Exception("Concurrent lock was ignored"); }
                catch (InvalidOperationException) { }
            }
            Equal(edited, f.Store.LoadManualDrafts(f.Settings.ProfileKey).Single());
            secondStore.SaveManualDrafts(f.Settings.ProfileKey, [], [edited]); Equal(0, f.Store.LoadManualDrafts(f.Settings.ProfileKey).Count);
            return Task.CompletedTask;
        });
        await check("manual drafts are atomic per-profile durable and corruption never becomes an empty list", () =>
        {
            using var f = new Fixture(); var entry = Entry(); f.Store.SaveManualDrafts(f.Settings.ProfileKey, [entry]);
            Equal(entry, f.Store.LoadManualDrafts(f.Settings.ProfileKey).Single()); Equal(0, f.Store.LoadManualDrafts("other-profile").Count);
            try { f.Store.SaveManualDrafts(f.Settings.ProfileKey, [entry, entry]); throw new Exception("Invalid duplicates saved"); } catch (InvalidDataException) { }
            Equal(entry, f.Store.LoadManualDrafts(f.Settings.ProfileKey).Single());
            var path = Path.Combine(f.Store.RootPath, "manual-drafts", f.Settings.ProfileKey + ".json");
            foreach (var corrupt in new[] { "", "null", "broken-json", "[null]", JsonSerializer.Serialize(new[] { entry with { Hours = -1 } }, JsonDefaults.Options) })
            {
                File.WriteAllText(path, corrupt);
                try { f.Store.LoadManualDrafts(f.Settings.ProfileKey); throw new Exception("Corrupt manual drafts were ignored"); } catch (InvalidDataException) { }
            }
            f.Store.SaveManualDrafts(f.Settings.ProfileKey, []); Equal(0, f.Store.LoadManualDrafts(f.Settings.ProfileKey).Count);
            return Task.CompletedTask;
        });
    }
    private static ManualTimecard Entry() => new() { Date = Fixture.Day, Hours = .25m, Project = 100, Task = 1, Description = "Manual case administration" };
    private static ProposalEnvelope Envelope(ReadSession session) => new() { SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId };
    private static void Valid(ValidationResult result) { if (!result.IsValid) throw new Exception(string.Join(" | ", result.Errors)); }
    private static void Require(bool value) { if (!value) throw new Exception("Manual assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static async Task Throws<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
