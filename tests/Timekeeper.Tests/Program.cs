using System.Text.Json;
using Timekeeper.Core;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Equal<T>(T actual, T expected, string context = "")
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{context} Expected {expected}, got {actual}.");
}
void True(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(ValidationResult result, string fragment)
{
    True(!result.IsValid, "Expected validation failure.");
    True(result.Errors.Any(e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase)), $"Expected error containing '{fragment}', got: {string.Join(" | ", result.Errors)}");
}
void Valid(ValidationResult result) => True(result.IsValid, string.Join(" | ", result.Errors));
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
ReadSession Session(int hours = 7, DateOnly? date = null, int startHour = 8)
{
    var seed = DemoData.CreateSession();
    var day = date ?? new DateOnly(2026, 9, 28);
    var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(startHour, 0)), TimeSpan.FromHours(-4));
    return seed with { Days = [new DaySnapshot { Date = day, Entries = [new TimeEntry(1001, 1, start, start.AddHours(hours), hours * 3600, "Client work", "Example project", false, true)] }] };
}
ProposalEnvelope Proposal(ReadSession session) => DemoData.CreateProposal(session);
ValidationResult Check(ReadSession session) => Rules.Validate(session, Proposal(session));
ReadSession WithExisting(ReadSession session, params ExistingTimecard[] cards) => session with { Days = [session.Days[0] with { Existing = cards.ToList() }] };
string Json(ProposalEnvelope proposal) => JsonSerializer.Serialize(proposal, JsonDefaults.Options);

foreach (var (work, total, fill) in new[] { (7, 8m, 0.83m), (8, 8.17m, 0m), (9, 9.17m, 0m) })
    Test($"{work} worked hours preserve work and fill only positive gap", () =>
    {
        var result = Check(Session(work)); Valid(result);
        Equal(result.Rows.Sum(r => r.Hours), total);
        Equal(result.Rows.Where(r => r.Kind == "misc_internal").Sum(r => r.Hours), fill);
        Equal(result.Rows.Single(r => r.Kind == "work").Hours, (decimal)work);
    });

Test("Rounding boundaries", () =>
{
    Equal(Rules.RoundSeconds(0), 0m); Equal(Rules.RoundSeconds(1), 0.08m);
    Equal(Rules.RoundSeconds(300), 0.08m); Equal(Rules.RoundSeconds(301), 0.17m);
    Equal(Rules.RoundSeconds(3600), 1m); Equal(Rules.RoundSeconds(86400), 24m);
    Throws<ArgumentOutOfRangeException>(() => Rules.RoundSeconds(-1));
    Throws<ArgumentOutOfRangeException>(() => Rules.RoundSeconds(long.MaxValue));
});
Test("Each entry rounds then integer seconds aggregate before decimal rounding", () =>
{
    var session = Session(); var start = session.Days[0].Entries[0].Start;
    var entries = Enumerable.Range(0, 3).Select(i => new TimeEntry(1001 + i, 1, start.AddSeconds(i * 301), start.AddSeconds(i * 301 + 1), 1, "Tiny task", "Example", false, true)).ToList();
    session = session with { Days = [session.Days[0] with { Entries = entries }] };
    var row = Proposal(session).Rows[0] with { SourceEntryIds = entries.Select(e => e.Id).ToList() };
    var result = Rules.Validate(session, Proposal(session) with { Rows = [row] }); Valid(result);
    Equal(result.Rows.Single(r => r.Kind == "work").Hours, 0.25m);
});
Test("Split identical mappings merge before final decimal rounding", () =>
{
    var session = Session(); var start = session.Days[0].Entries[0].Start;
    session = session with { Days = [session.Days[0] with { Entries = [new(1001, 1, start, start.AddSeconds(1), 1, "One", "Example", false, true), new(1002, 1, start.AddSeconds(10), start.AddSeconds(11), 1, "Two", "Example", false, true)] }] };
    var proposal = Proposal(session);
    proposal = proposal with { Rows = [proposal.Rows[0], proposal.Rows[0] with { SourceEntryIds = [1002], Description = "Second description" }] };
    var result = Rules.Validate(session, proposal); Valid(result);
    Equal(result.Rows.Single(r => r.Kind == "work").Hours, 0.17m);
});
Test("Mixed internal and client work both count", () =>
{
    var session = Session(); var first = session.Days[0].Entries[0];
    session = session with { Days = [session.Days[0] with { Entries = [first, first with { Id = 1002, Start = first.Stop!.Value, Stop = first.Stop.Value.AddMinutes(30), DurationSeconds = 1800, Billable = false }] }] };
    var proposal = Proposal(session); proposal = proposal with { Rows = [proposal.Rows[0], proposal.Rows[1] with { Assignment = null, Project = 900, Task = 44, Category = 11 }] };
    var result = Rules.Validate(session, proposal); Valid(result); Equal(result.Rows.Single(r => r.Kind == "misc_internal").Hours, 0.33m);
});
Test("Existing Timecards counts once", () =>
{
    var session = Session(); session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 0.17m, 900, 44, 11, null, "Timecards"));
    var result = Check(session); Valid(result);
    Equal(result.Rows.Count(r => r.Kind == "timecards"), 0); Equal(result.Rows.Single(r => r.Kind == "misc_internal").Hours, 0.83m);
});
Test("Already mapped actual Timecards prevents automatic duplicate", () =>
{
    var session = Session(1); var proposal = Proposal(session);
    proposal = proposal with { Rows = [proposal.Rows[0] with { Assignment = null, Project = 900, Task = 44, Category = 11, Description = "Timecards" }] };
    var result = Rules.Validate(session, proposal); Valid(result); Equal(result.Rows.Count(r => r.Kind == "timecards"), 0);
});
Test("Zero sources never infer a full workday", () =>
{
    var session = Session(); session = session with { Days = [session.Days[0] with { Entries = [] }] };
    var result = Check(session); Valid(result); Equal(result.Rows.Count, 0);
});
Test("Weekend logs actual work only", () =>
{
    var result = Check(Session(7, new DateOnly(2026, 10, 3))); Valid(result); Equal(result.Rows.Count, 1); Equal(result.Rows[0].Hours, 7m);
});
Test("Configurable target and addition", () =>
{
    var session = Session(7); session = session with { Settings = session.Settings with { TargetHours = 7.5m, TimecardsHours = 0.25m } };
    var result = Check(session); Valid(result); Equal(result.Rows.Single(r => r.Kind == "misc_internal").Hours, 0.25m); Equal(result.Rows.Sum(r => r.Hours), 7.5m);
});
Test("Automatic rows can be disabled", () =>
{
    var session = Session(7); session = session with { Settings = session.Settings with { AddTimecards = false, FillWeekdays = false } };
    var result = Check(session); Valid(result); Equal(result.Rows.Count, 1);
});
Test("No time-of-day cutoff changes additions", () =>
{
    var early = Check(Session(7, startHour: 2)); var late = Check(Session(7, startHour: 15)); Valid(early); Valid(late);
    Equal(string.Join(",", early.Rows.Select(r => (r.Kind, r.Hours))), string.Join(",", late.Rows.Select(r => (r.Kind, r.Hours))));
});
Test("Large fill requires explicit confirmation warning", () =>
{
    var result = Check(Session(2)); Valid(result); True(result.Warnings.Any(w => w.Contains("large gap")), "Large gap warning is required.");
});
Test("Automatic task category comes from reference", () =>
{
    var session = Session(); session = session with { Reference = session.Reference with { Tasks = session.Reference.Tasks.Select(t => t.Id == 44 ? t with { CategoryId = 20 } : t).ToList() } };
    var result = Check(session); Valid(result); Equal(result.Rows.Single(r => r.Kind == "timecards").Category, 20);
});
Test("Missing automatic task blocks required automatic rows", () =>
{
    var session = Session(); session = session with { Settings = session.Settings with { InternalTaskId = 999 } }; Reject(Check(session), "unavailable");
});
Test("Every source must be covered", () =>
{
    var session = Session(); Reject(Rules.Validate(session, Proposal(session) with { Rows = [] }), "missing");
});
Test("Sources cannot occur twice", () =>
{
    var session = Session(); var proposal = Proposal(session); Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0], proposal.Rows[0]] }), "more than once");
});
Test("Invented source IDs block", () =>
{
    var session = Session(); var proposal = Proposal(session); Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { SourceEntryIds = [9999] }] }), "unknown");
});
Test("Copilot cannot add source-free automatic rows", () =>
{
    var session = Session(); var proposal = Proposal(session); Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0], proposal.Rows[0] with { SourceEntryIds = [] }] }), "automatic rows");
});
Test("Task category cannot be invented", () =>
{
    var session = Session(); var proposal = Proposal(session); Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { Category = 11 }] }), "does not belong");
});
Test("Assignment must belong to supplied project", () =>
{
    var session = Session(); var proposal = Proposal(session); Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { Project = 101 }] }), "does not belong");
});
Test("Only internal project can omit assignment", () =>
{
    var session = Session(); var proposal = Proposal(session); Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { Assignment = null }] }), "requires an assignment");
});
Test("Imported hours are never trusted", () =>
{
    var session = Session(); var proposal = Proposal(session);
    foreach (var hours in new[] { -1m, 0m, 7.01m, 100m }) Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { Hours = hours }] }), "do not match");
    Valid(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { Hours = 7m }] }));
});
Test("Dates session and employee must match", () =>
{
    var session = Session(); var proposal = Proposal(session);
    Reject(Rules.Validate(session, proposal with { SessionId = "another" }), "different read");
    Reject(Rules.Validate(session, proposal with { EmployeeId = "another" }), "employee");
    Reject(Rules.Validate(session, proposal with { SchemaVersion = 2 }), "schema");
    Reject(Rules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { Date = new DateOnly(2026, 9, 29) }] }), "date");
});
Test("Source freshness is required", () =>
{
    var session = Session(); Reject(Rules.Validate(session, Proposal(session), session.GeneratedAtUtc.AddHours(25)), "24 hours");
    Reject(Rules.Validate(session, Proposal(session), session.GeneratedAtUtc.AddMinutes(-6)), "future");
});
Test("Running and invalid durations block", () =>
{
    var session = Session(); var entry = session.Days[0].Entries[0];
    foreach (var bad in new[] { entry with { Running = true }, entry with { Stop = null }, entry with { DurationSeconds = -1 }, entry with { DurationSeconds = 0 }, entry with { DurationSeconds = long.MaxValue } })
        Reject(Check(session with { Days = [session.Days[0] with { Entries = [bad] }] }), "invalid duration");
});
Test("Inconsistent duration blocks", () =>
{
    var session = Session(); var entry = session.Days[0].Entries[0] with { DurationSeconds = 1 };
    Reject(Check(session with { Days = [session.Days[0] with { Entries = [entry] }] }), "inconsistent");
    Reject(Check(session with { Days = [session.Days[0] with { Entries = [entry with { Stop = DateTimeOffset.MinValue }] }] }), "inconsistent");
});
Test("Cross-midnight entry blocks but exact midnight finish works", () =>
{
    Reject(Check(Session(7, startHour: 20)), "date boundary");
    Valid(Check(Session(7, startHour: 17)));
});
Test("Possible duplicate requires explicit confirmation", () =>
{
    var session = Session(); session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 7m, 100, 60, 20, 501, "Client work"));
    var result = Check(session); Valid(result); True(result.Warnings.Any(w => w.Contains("Possible duplicate")), "Possible duplicate must require confirmation.");
});
Test("Legitimate repeated work remains possible after earlier work is linked", () =>
{
    var session = Session(1); var first = session.Days[0].Entries[0];
    session = session with { Days = [session.Days[0] with { Entries = [first, first with { Id = 1002, Start = first.Start.AddHours(2), Stop = first.Stop!.Value.AddHours(2) }] }] };
    session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 1m, 100, 60, 20, 501, "Morning client work"));
    var proposal = Proposal(session); proposal = proposal with { Rows = [proposal.Rows[0] with { SourceEntryIds = [1002], Description = "Afternoon client work" }], AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 8001 }] };
    var result = Rules.Validate(session, proposal); Valid(result);
    Equal(result.Rows.Single(r => r.Kind == "work").Hours, 1m);
    True(result.Warnings.Any(w => w.Contains("Possible duplicate")), "Repeated work needs review.");
});
Test("Timecards linked to a grouped internal record counts once", () =>
{
    var session = Session(1); session = session with { Days = [session.Days[0] with { Entries = [session.Days[0].Entries[0] with { Description = "Timecards" }] }] };
    session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 1m, 900, 44, 11, null, "Administration; Timecards"));
    var proposal = Proposal(session) with { Rows = [], AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 8001 }] };
    var result = Rules.Validate(session, proposal); Valid(result); Equal(result.Rows.Count(r => r.Kind == "timecards"), 0);
    Equal(result.Rows.Single(r => r.Kind == "misc_internal").Hours, 7m);
    session = WithExisting(session, session.Days[0].Existing[0] with { Project = 100, Assignment = 501 });
    result = Rules.Validate(session, proposal); Valid(result); Equal(result.Rows.Count(r => r.Kind == "timecards"), 1);
});
Test("Already recorded link is validated and explicitly warned", () =>
{
    var session = Session(); session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 7m, 100, 60, 20, 501, "Client work"));
    var proposal = Proposal(session) with { Rows = [], AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 8001 }] };
    var result = Rules.Validate(session, proposal); Valid(result); Equal(result.Rows.Sum(r => r.Hours), 1m); True(result.Warnings.Any(w => w.Contains("Confirm already recorded")), "Link must require confirmation.");
});
Test("Completed day reread adds nothing", () =>
{
    var session = Session(); var date = session.Days[0].Date;
    session = WithExisting(session, new ExistingTimecard(8001, date, 7m, 100, 60, 20, 501, "Client work"), new(8002, date, 0.17m, 900, 44, 11, null, "Timecards"), new(8003, date, 0.83m, 900, 44, 11, null, "Misc internal"));
    var result = Rules.Validate(session, Proposal(session) with { Rows = [], AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 8001 }] }); Valid(result); Equal(result.Rows.Count, 0);
});
Test("Already-recorded unknown cross-date and insufficient records block", () =>
{
    var session = Session(); session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 1m, 100, 60, 20, 501, "Client work"));
    var proposal = Proposal(session) with { Rows = [], AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 8001 }] };
    Reject(Rules.Validate(session, proposal), "exceed");
    Reject(Rules.Validate(session, proposal with { AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 9999 }] }), "not in this read");
    Reject(Rules.Validate(session, proposal with { AlreadyRecorded = [new() { SourceEntryId = 9999, ExistingRecordId = 8001 }] }), "unknown");
    var otherDay = new DaySnapshot { Date = session.Days[0].Date.AddDays(1), Existing = [session.Days[0].Existing[0] with { Date = session.Days[0].Date.AddDays(1), Hours = 7m }] };
    session = session with { Days = [session.Days[0] with { Existing = [] }, otherDay] };
    Reject(Rules.Validate(session, proposal), "different dates");
});
Test("Source cannot be both work and already recorded", () =>
{
    var session = Session(); session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 7m, 100, 60, 20, 501, "Client work"));
    Reject(Rules.Validate(session, Proposal(session) with { AlreadyRecorded = [new() { SourceEntryId = 1001, ExistingRecordId = 8001 }] }), "more than once");
});
Test("Daily hours sanity includes existing records", () =>
{
    var session = Session(9); session = WithExisting(session, new ExistingTimecard(8001, session.Days[0].Date, 16m, 101, 61, 20, 502, "Other work")); Reject(Check(session), "24 hours");
});
Test("Null records return validation errors", () =>
{
    var session = Session(); var proposal = Proposal(session);
    Reject(Rules.Validate(null!, proposal), "required");
    Reject(Rules.Validate(session, null!), "required");
    Reject(Rules.Validate(session, proposal with { Rows = null! }), "null");
    Reject(Rules.Validate(session, proposal with { Rows = [null!] }), "null");
    Reject(Rules.Validate(session, proposal with { AlreadyRecorded = [null!] }), "null");
    Reject(Rules.Validate(session with { Days = [null!] }, proposal), "null");
    Reject(Check(session with { Reference = session.Reference with { Tasks = [null!] } }), "Task reference");
});
Test("Closed and other-employee assignments require confirmation", () =>
{
    var session = Session();
    session = session with { Reference = session.Reference with { Assignments = session.Reference.Assignments.Select(a => a.Id == 501 ? a with { Status = "Closed", Employees = ["someoneelse@example.com"] } : a).ToList() } };
    var result = Check(session); Valid(result);
    True(result.Warnings.Any(w => w.Contains("status “Closed”")), "Closed assignment must warn.");
    True(result.Warnings.Any(w => w.Contains("does not list")), "Unassigned employee must warn.");
    session = session with { Reference = session.Reference with { Assignments = session.Reference.Assignments.Select(a => a with { Status = "Active", Employees = [session.Settings.Email.ToUpperInvariant()] }).ToList() } };
    result = Check(session); Valid(result); Equal(result.Warnings.Count, 0);
});
Test("Empty source session has no valid read", () =>
{
    var session = Session(); Reject(Rules.Validate(session with { Days = [] }, Proposal(session) with { Rows = [] }), "no dates");
});
Test("Timecards source merged with other internal work still counts once", () =>
{
    var session = Session(1); var first = session.Days[0].Entries[0] with { Description = "Timecards" };
    session = session with { Days = [session.Days[0] with { Entries = [first, first with { Id = 1002, Start = first.Stop!.Value, Stop = first.Stop.Value.AddHours(1), Description = "Internal meeting" }] }] };
    var row = Proposal(session).Rows[0] with { Project = 900, Assignment = null, Task = 44, Category = 11, SourceEntryIds = [1001, 1002], Description = "Timecards and internal meeting" };
    var result = Rules.Validate(session, Proposal(session) with { Rows = [row] }); Valid(result); Equal(result.Rows.Count(r => r.Kind == "timecards"), 0);
});
Test("Invalid settings are rejected", () =>
{
    var settings = Session().Settings; Equal(Rules.ValidateSettings(settings).Count, 0);
    foreach (var bad in new[] { settings with { Realm = "https://evil.example.com" }, settings with { Realm = "company.quickbase.com.evil.example" }, settings with { Email = "" }, settings with { EmployeeId = "" }, settings with { TargetHours = -1 }, settings with { TargetHours = 8.001m }, settings with { TimecardsHours = -1 }, settings with { TimeZoneId = "bad zone" }, settings with { CopilotUrl = "javascript:alert(1)" }, settings with { TimecardsTable = "bad/../../" } })
        True(Rules.ValidateSettings(bad).Count > 0, "Invalid settings were accepted.");
});
Test("Strict JSON round-trip preserves mapping", () =>
{
    var session = Session(); var proposal = ProposalExchange.Parse(Json(Proposal(session))); Valid(Rules.Validate(session, proposal));
});
Test("Setup permits ID discovery without weakening normal validation", () =>
{
    var settings = Session().Settings with { EmployeeId = "" };
    Equal(Rules.ValidateSettings(settings, allowMissingEmployeeId: true).Count, 0);
    True(Rules.ValidateSettings(settings).Count > 0, "Operational settings must still require a verified ID.");
    True(Rules.ValidateSettings(settings with { Email = "" }, allowMissingEmployeeId: true).Count > 0, "Discovery must still require a work email.");
    var session = Session() with { Settings = settings };
    Reject(Check(session), "user ID");
});
Test("Strict JSON rejects extra fields duplicate keys and nulls", () =>
{
    var valid = Json(Proposal(Session()));
    foreach (var json in new[] { "null", "[]", "{}", valid.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"schema_version\": 1"), valid.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"token\": \"secret\""), valid.Replace("\"already_recorded\": []", "\"already_recorded\": null"), valid.Replace("\"category\": 20", "\"category\": null"), valid.Replace("\"category\": 20", "\"category\": 20, \"arbitrary\": true"), "```json\n" + valid + "\n```" })
        Throws<FormatException>(() => ProposalExchange.Parse(json));
});
Test("Strict JSON rejects missing defaults instead of silently accepting", () =>
{
    var valid = Json(Proposal(Session()));
    Throws<FormatException>(() => ProposalExchange.Parse(valid.Replace("\"schema_version\": 1,", "")));
    Throws<FormatException>(() => ProposalExchange.Parse(valid.Replace("\"assignment\": 501,", "")));
    Throws<FormatException>(() => ProposalExchange.Parse(valid.Replace("\"date\": \"2026-09-28\"", "\"date\": \"2026-02-30\"")));
    Throws<FormatException>(() => ProposalExchange.Parse(new string('x', ProposalExchange.MaximumCharacters + 1)));
});
Test("Export contains complete schema references and policy without credentials", () =>
{
    var session = Session(); var json = ProposalExchange.Export(session); using var doc = JsonDocument.Parse(json);
    Equal(doc.RootElement.GetProperty("session_data").GetProperty("session_id").GetString(), session.SessionId);
    Equal(doc.RootElement.GetProperty("expected_envelope").GetProperty("employee_id").GetString(), session.Settings.EmployeeId);
    True(doc.RootElement.GetProperty("instructions").GetArrayLength() > 0, "Instructions missing.");
    True(!json.Contains("toggl_token") && !json.Contains("quickbase_token") && !json.Contains(session.Settings.Email), "Export must not contain credentials or unnecessary email.");
    Equal(doc.RootElement.GetProperty("session_data").GetProperty("reference").GetProperty("assignments").GetArrayLength(), 2);
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} offline tests passed.");
return failed == 0 ? 0 : 1;
