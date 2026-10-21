using System.Text.Json;
using Timekeeper.Core;

static class ManualTimecardTests
{
    public static void Register(Action<string, Action> test)
    {
        test("manual-only uses exact decimal hours with no implicit daily defaults", () =>
        {
            var session = Session(); var manual = Entry(session); session = session with { ManualTimecards = [manual] };
            var result = Check(session); Valid(result); Equal(1, result.Rows.Count); Equal(.25m, result.Rows[0].Hours);
            Equal("manual", result.Rows[0].Kind); Equal(manual.Id, result.Rows[0].ManualEntryIds.Single());
            Require(result.Warnings.Any(w => w.StartsWith("Manual time:")));
            Require(result.Rows[0].SourceEntryIds.Count == 0 && result.Rows[0].SourceActivityIds.Count == 0);
        });
        test("manual validation rejects malformed IDs values collections dates and descriptions", () =>
        {
            var session = Session(); var seed = Entry(session);
            foreach (var entry in new[] { seed with { Id = "invented" }, seed with { Date = default }, seed with { Hours = 0 }, seed with { Hours = -1 }, seed with { Hours = 24.01m }, seed with { Hours = .251m }, seed with { Description = " " }, seed with { Description = new string('x', 4001) }, seed with { Description = "a\0b" }, seed with { Assignment = 0 }, seed with { Project = 0 }, seed with { Task = 0 }, seed with { Date = seed.Date.AddDays(1) } })
                Require(!Check(session with { ManualTimecards = [entry] }).IsValid);
            Require(!Check(session with { ManualTimecards = null! }).IsValid);
            Require(!Check(session with { ManualTimecards = [null!] }).IsValid);
            Require(!Check(session with { ManualTimecards = [seed, seed] }).IsValid);
            Require(!Check(session with { ManualTimecards = [seed with { Hours = 13 }, seed with { Id = Guid.NewGuid().ToString("N"), Hours = 12 }] }).IsValid);
        });
        test("manual time validates project assignment task and category references", () =>
        {
            var session = Session(); var seed = Entry(session);
            foreach (var entry in new[] { seed with { Project = 99999 }, seed with { Task = 99999 }, seed with { Assignment = 501, Project = 101 }, seed with { Assignment = 99999 } })
                Require(!Check(session with { ManualTimecards = [entry] }).IsValid);
            var assigned = Check(session with { ManualTimecards = [seed with { Assignment = 501 }] }); Valid(assigned); Equal("Presentation design", assigned.Rows.Single().AssignmentName);
        });
        test("manual-only rejects imported source data and agent-created manual rows", () =>
        {
            var manual = Session(); var toggl = TogglSession();
            Require(!Rules.Validate(manual with { Days = toggl.Days }, Envelope(manual)).IsValid);
            Require(!Rules.Validate(manual, Envelope(manual) with { Rows = [new() { Date = manual.Days[0].Date, Project = 100, Task = 60, Category = 20, Description = "Invented", Hours = 1 }] }).IsValid);
            Require(!Rules.Validate(manual with { EmailWorkbook = new() }, Envelope(manual)).IsValid);
            var json = JsonSerializer.Serialize(Envelope(manual), JsonDefaults.Options).TrimEnd();
            var injected = json[..^1] + ",\"manual_timecards\": []}";
            try { ProposalExchange.Parse(injected); throw new Exception("Agent manual records accepted"); } catch (FormatException) { }
        });
        test("manual time counts toward Toggl weekday fill without rounding", () =>
        {
            var session = TogglSession(); var manual = Entry(session) with { Hours = .26m };
            session = session with { ManualTimecards = [manual] };
            var result = Rules.Validate(session, DemoData.CreateProposal(session)); Valid(result);
            Equal(.26m, result.Rows.Single(r => r.Kind == "manual").Hours); Equal(.57m, result.Rows.Single(r => r.Kind == "misc_internal").Hours); Equal(8m, result.Rows.Sum(r => r.Hours));
        });
        test("exact manual and imported match includes hours once with stable source IDs", () =>
        {
            var session = TogglSession(); var manual = Entry(session) with { Assignment = 501, Hours = 7m, Description = "  CLIENT   WORK " };
            session = session with { ManualTimecards = [manual] };
            var result = Rules.Validate(session, DemoData.CreateProposal(session)); Valid(result);
            var row = result.Rows.Single(r => r.Kind == "work"); Equal(7m, row.Hours); Equal(manual.Id, row.ManualEntryIds.Single()); Require(row.SourceEntryIds.Count == 1);
            Require(result.Rows.All(r => r.Kind != "manual") && result.Warnings.Any(w => w.Contains("included once"))); Equal(8m, result.Rows.Sum(r => r.Hours));
        });
        test("uncertain imported billing or description keeps the manual row and warns", () =>
        {
            var session = TogglSession() with { Settings = TogglSession().Settings with { AddTimecards = false, FillWeekdays = false } };
            foreach (var manual in new[] { Entry(session) with { Assignment = 501, Hours = 7m, Description = "Client work", BillableOverride = true }, Entry(session) with { Assignment = 501, Hours = 7m, Description = "Additional client work" } })
            {
                var result = Rules.Validate(session with { ManualTimecards = [manual] }, DemoData.CreateProposal(session)); Valid(result);
                Equal(14m, result.Rows.Sum(r => r.Hours)); Require(result.Rows.Any(r => r.Kind == "manual") && result.Warnings.Any(w => w.Contains("Possible overlap")));
            }
        });
        test("unique existing manual match skips only positively compatible billing", () =>
        {
            var session = Session(); var manual = Entry(session) with { BillableOverride = false };
            var card = new ExistingTimecard(800, manual.Date, manual.Hours, manual.Project, manual.Task, 20, null, manual.Description) { Billable = false };
            session = session with { ManualTimecards = [manual], Days = [session.Days[0] with { Existing = [card] }] };
            var result = Check(session); Valid(result); Equal(0, result.Rows.Count); Equal(manual.Id, result.AlreadyRecordedManualEntryIds.Single());
            foreach (var value in new bool?[] { null, true })
            {
                var uncertain = Check(session with { Days = [session.Days[0] with { Existing = [card with { Billable = value }] }] }); Valid(uncertain);
                Equal(1, uncertain.Rows.Count); Equal(0, uncertain.AlreadyRecordedManualEntryIds.Count);
            }
            var ambiguous = Check(session with { Days = [session.Days[0] with { Existing = [card, card with { RecordId = 801 }] }] }); Valid(ambiguous); Equal(1, ambiguous.Rows.Count);
        });
        test("distinct manual duplicates are retained with separate duration-edit identities", () =>
        {
            var session = Session(); var first = Entry(session); var second = first with { Id = Guid.NewGuid().ToString("N") };
            session = session with { ManualTimecards = [first, second] }; var result = Check(session); Valid(result); Equal(2, result.Rows.Count);
            Require(ReviewDurations.Key(result.Rows[0]) != ReviewDurations.Key(result.Rows[1]));
            var edited = ReviewDurations.Apply(session, result, [new(ReviewDurations.Key(result.Rows[1]), .25m, .5m)]); Valid(edited); Equal(.75m, edited.Rows.Sum(r => r.Hours));
        });
        test("two manual entries cannot both claim the same imported or existing timecard", () =>
        {
            var session = TogglSession() with { Settings = TogglSession().Settings with { AddTimecards = false, FillWeekdays = false } };
            var first = Entry(session) with { Assignment = 501, Hours = 7m, Description = "Client work" };
            var second = first with { Id = Guid.NewGuid().ToString("N") };
            var imported = Rules.Validate(session with { ManualTimecards = [first, second] }, DemoData.CreateProposal(session)); Valid(imported);
            Equal(14m, imported.Rows.Sum(r => r.Hours)); Equal(1, imported.Rows.Single(r => r.Kind == "work").ManualEntryIds.Count); Equal(second.Id, imported.Rows.Single(r => r.Kind == "manual").ManualEntryIds.Single());
            session = Session(); first = Entry(session) with { BillableOverride = false }; second = first with { Id = Guid.NewGuid().ToString("N") };
            var card = new ExistingTimecard(800, first.Date, first.Hours, first.Project, first.Task, 20, null, first.Description) { Billable = false };
            var existing = Check(session with { ManualTimecards = [first, second], Days = [session.Days[0] with { Existing = [card] }] }); Valid(existing);
            Equal(1, existing.AlreadyRecordedManualEntryIds.Count); Equal(first.Id, existing.AlreadyRecordedManualEntryIds.Single()); Equal(second.Id, existing.Rows.Single().ManualEntryIds.Single());
        });
        test("manual date scope includes selected entries and cannot hide invalid drafts", () =>
        {
            var session = Session(); var first = Entry(session); var next = first.Date.AddDays(1); var second = first with { Id = Guid.NewGuid().ToString("N"), Date = next };
            session = session with { ManualTimecards = [first, second], Days = [session.Days[0], new() { Date = next }] };
            var scope = ProposalScope.Select(session, Envelope(session), [next]); Equal(second.Id, scope.Session.ManualTimecards.Single().Id); Valid(Check(scope.Session));
            Equal(2, session.ManualTimecards.Count); Equal(2, ProposalScope.ProposedDates(session, Envelope(session)).Count);
            try { ProposalScope.Select(session with { ManualTimecards = [first, second with { Id = first.Id }] }, Envelope(session), [next]); throw new Exception("Repeated ID was hidden"); } catch (InvalidOperationException) { }
        });
        test("legacy sessions and receipts deserialize with empty manual identities", () =>
        {
            var legacy = JsonSerializer.Serialize(Session(), JsonDefaults.Options);
            using var doc = JsonDocument.Parse(legacy);
            var old = doc.RootElement.EnumerateObject().Where(p => p.Name is not ("manual_only" or "manual_timecards")).ToDictionary(p => p.Name, p => p.Value.Clone());
            var restored = JsonSerializer.Deserialize<ReadSession>(JsonSerializer.Serialize(old), JsonDefaults.Options)!;
            Require(!restored.ManualOnly && restored.ManualTimecards.Count == 0);
            var row = JsonSerializer.Deserialize<VerifiedRow>("{\"hours\":1}", JsonDefaults.Options)!; Equal(0, row.ManualEntryIds.Count);
        });
    }
    private static ReadSession Session()
    {
        var demo = DemoData.CreateSession();
        return demo with { ManualOnly = true, Days = [new() { Date = new(2026, 10, 12) }], Reference = demo.Reference with { Projects = [new(100, "Case without assignment"), new(101, "Other case")] } };
    }
    private static ReadSession TogglSession()
    {
        var manual = Session(); var date = manual.Days[0].Date; var start = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.FromHours(-4));
        return manual with { ManualOnly = false, Days = [new() { Date = date, Entries = [new(1001, 1, start, start.AddHours(7), 25200, "Client work", "Case", false, true)] }] };
    }
    private static ManualTimecard Entry(ReadSession session) => new() { Date = session.Days[0].Date, Hours = .25m, Project = 100, Task = 60, Description = "Case administration" };
    private static ProposalEnvelope Envelope(ReadSession session) => new() { SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId };
    private static ValidationResult Check(ReadSession session) => Rules.Validate(session, Envelope(session));
    private static void Valid(ValidationResult result) { if (!result.IsValid) throw new Exception(string.Join(" | ", result.Errors)); }
    private static void Require(bool value) { if (!value) throw new Exception("Manual assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
}
