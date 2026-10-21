using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Timekeeper.Core;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void True(bool value, string message = "Expected true.") { if (!value) throw new Exception(message); }
void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}."); }
void Valid(ValidationResult result) => True(result.IsValid, string.Join(" | ", result.Errors));
void Reject(ValidationResult result, string text) => True(!result.IsValid && result.Errors.Any(e => e.Contains(text, StringComparison.OrdinalIgnoreCase)), $"Expected '{text}': {string.Join(" | ", result.Errors)}");
void Throws(Action run, string text)
{
    try { run(); } catch (InvalidDataException ex) { True(ex.Message.Contains(text, StringComparison.OrdinalIgnoreCase), ex.Message); return; }
    throw new Exception("Expected workbook rejection: " + text);
}
ReadSession Session(params int[] minutes)
{
    if (minutes.Length == 0) minutes = [5];
    var seed = DemoData.CreateSession();
    var day = new DateOnly(2026, 9, 28);
    return seed with
    {
        Days = [new DaySnapshot { Date = day }],
        EmailWorkbook = new EmailWorkbook
        {
            Metadata = new EmailWorkbookMetadata { EmployeeEmail = seed.Settings.Email, TimeZone = seed.Settings.TimeZoneId, PeriodStart = day, PeriodEnd = day, GeneratedAtUtc = DateTimeOffset.UtcNow, EmployeeConfirmed = true, RetrievalLimitations = "none" },
            Activities = minutes.Select((m, i) => new EmailActivity { ActivityId = "a" + i, Date = day, Minutes = m, Description = "Confirmed case correspondence " + i, MatterHint = "Example", TimeBasis = "employee_confirmed_estimate", EvidenceIds = ["<message-" + i + "@example.com>"] }).ToList()
        }
    };
}
ProposalEnvelope Proposal(ReadSession session) => new()
{
    SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId,
    Rows = session.EmailWorkbook!.Activities.Where(a => session.Days.Any(d => d.Date == a.Date)).Select(a => new ProposalRow { Date = a.Date, SourceActivityIds = [a.ActivityId], Project = 100, Assignment = 501, Task = 60, Category = 20, Description = a.Description }).ToList()
};
ReadSession Existing(ReadSession session, decimal hours, string description = "Already recorded") => session with { Days = [session.Days[0] with { Existing = [new ExistingTimecard(8001, session.Days[0].Date, hours, 100, 60, 20, 501, description)] }] };

Test("Pending local review preserves source approval and carries legacy notes to final reviewer", () =>
{
    True(EmailWorkbookReader.ValidateForReview(new EmailWorkbook { Metadata = null! }).Count > 0);
    var session = Session(); var original = session.EmailWorkbook!;
    var workbook = original with
    {
        Metadata = original.Metadata with { Format = "timekeeper_email_review", EmployeeConfirmed = false },
        ReviewNotes = ["Calendar coverage is incomplete."],
        Activities = original.Activities.Select(a => a with { ReviewNotes = "Confirm meeting duration." }).ToList()
    };
    session = session with { EmailWorkbook = workbook };
    Equal(EmailWorkbookReader.ValidateForReview(workbook, session.Settings).Count, 0);
    True(!workbook.Metadata.EmployeeConfirmed && !workbook.ReviewConfirmedAtUtc.HasValue);
    var result = EmailRules.Validate(session, Proposal(session)); Valid(result);
    True(result.Warnings.Any(w => w.Contains("Calendar coverage is incomplete.")));
    True(result.Warnings.Any(w => w.Contains("Confirm meeting duration.")));
    True(result.Warnings.Any(w => w.StartsWith("Email review:")));
});
Test("Email-only session validates without any Toggl timer or automatic filler", () =>
{
    var session = Session(); var result = EmailRules.Validate(session, Proposal(session)); Valid(result);
    Equal(result.Rows.Count, 1); Equal(result.Rows[0].Hours, 0.08m); Equal(result.Rows[0].SourceEntryIds.Count, 0);
    True(result.Warnings.Any(w => w.Contains("estimated time")));
});
Test("Table hours edits are exact while unedited fragments still round once", () =>
{
    var session = Session(1, 1); var source = JsonSerializer.Serialize(session, JsonDefaults.Options);
    var edit = EmailReviewEdits.FromSettings(session.Settings) with { ActivityHours = [new("a0", 1, .10m)] };
    var result = EmailRules.Validate(session, Proposal(session), emailEdits: edit); Valid(result);
    Equal(result.Rows.Single().Hours, .18m); Equal(result.Rows.Single().OriginalHours, (decimal?).08m);
    True(result.Warnings.Any(w => w.Contains("changed from")));
    Equal(JsonSerializer.Serialize(session, JsonDefaults.Options), source);
    var precise = Session(15);
    var exact = EmailRules.Validate(precise, Proposal(precise), emailEdits: new() { ActivityHours = [new("a0", 15, .26m)] });
    Valid(exact); Equal(exact.Rows.Single().Hours, .26m);
});
Test("Table fill recalculates after hour edits and counts existing Quickbase time", () =>
{
    var session = Existing(Session(5), 1m); var settings = session.Settings;
    var edits = new EmailReviewEdits { ActivityHours = [new("a0", 5, .50m)], FillWeekdays = true, TargetHours = 8m, AddTimecards = true };
    var result = EmailRules.Validate(session, Proposal(session), emailEdits: edits); Valid(result);
    Equal(result.Rows.Single(r => r.Kind == "work").Hours, .50m);
    Equal(result.Rows.Single(r => r.Kind == "timecards").Hours, .17m);
    Equal(result.Rows.Single(r => r.Kind == "misc_internal").Hours, 6.33m);
    Equal(session.Settings, settings);
    var above = Session(5); var overtime = EmailRules.Validate(above, Proposal(above), emailEdits: edits with { ActivityHours = [new("a0", 5, 9m)] });
    Valid(overtime); Equal(overtime.Rows.Sum(r => r.Hours), 9.17m); True(overtime.Rows.All(r => r.Kind != "misc_internal"));
});
Test("Table weekday additions remain off on weekends", () =>
{
    var session = Session(15); var date = new DateOnly(2026, 10, 11); var workbook = session.EmailWorkbook!;
    session = session with { Days = [new() { Date = date }], EmailWorkbook = workbook with { Metadata = workbook.Metadata with { PeriodStart = date, PeriodEnd = date }, Activities = workbook.Activities.Select(a => a with { Date = date }).ToList() } };
    var result = EmailRules.Validate(session, Proposal(session), emailEdits: new() { FillWeekdays = true, AddTimecards = true, TargetHours = 8m });
    Valid(result); Equal(result.Rows.Count, 1); Equal(result.Rows.Single().Hours, .25m);
});
Test("Table edits reject invalid values stale source identity and duplicate edits", () =>
{
    var session = Session(5);
    foreach (var edits in new EmailReviewEdits[]
    {
        new() { ActivityHours = [new("a0", 4, .25m)] }, new() { ActivityHours = [new("missing", 5, .25m)] },
        new() { ActivityHours = [new("a0", 5, .25m), new("a0", 5, .5m)] }, new() { ActivityHours = null! },
        new() { ActivityHours = [null!] }, new() { TargetHours = 24.01m }, new() { TargetHours = 7.555m },
        new() { ActivityHours = [new("a0", 5, 0)] }, new() { ActivityHours = [new("a0", 5, decimal.MaxValue)] },
        new() { ActivityHours = [new("a0", 5, .123m)] }
    }) True(!EmailRules.Validate(session, Proposal(session), emailEdits: edits).IsValid);
    var two = Session(5, 5);
    Reject(EmailRules.Validate(two, Proposal(two), emailEdits: new() { ActivityHours = [new("a0", 5, 13), new("a1", 5, 12)] }), "24 hours");
});
Test("Already recorded activities cannot silently carry table duration edits", () =>
{
    var session = Existing(Session(5), 1m); var proposal = Proposal(session) with { Rows = [], EmailAlreadyRecorded = [new() { ActivityId = "a0", ExistingRecordId = 8001 }] };
    Reject(EmailRules.Validate(session, proposal, emailEdits: new() { ActivityHours = [new("a0", 5, .5m)] }), "already-recorded");
});
Test("Billing regrouping retains explicit table hours and dashboard edits layer on that baseline", () =>
{
    var session = Session(1, 1); var proposal = Proposal(session);
    var edits = new EmailReviewEdits { ActivityHours = [new("a0", 1, .10m), new("a1", 1, .20m)] };
    var split = proposal with { Rows = [proposal.Rows[0] with { BillableOverride = true }, proposal.Rows[1] with { BillableOverride = false }] };
    var separated = EmailRules.Validate(session, split, emailEdits: edits); Valid(separated); Equal(separated.Rows.Sum(r => r.Hours), .30m);
    var merged = EmailRules.Validate(session, proposal, emailEdits: edits); Valid(merged); Equal(merged.Rows.Single().Hours, .30m);
    var row = merged.Rows.Single(); var changed = ReviewDurations.Apply(session, merged, [new(ReviewDurations.Key(row), .30m, .50m)]);
    Valid(changed); Equal(changed.Rows.Single().Hours, .50m); Equal(changed.Rows.Single().OriginalHours, (decimal?).08m);
});
Test("Email table options cannot apply to a Toggl proposal", () =>
{
    var session = DemoData.CreateSession();
    Reject(Rules.Validate(session, DemoData.CreateProposal(session), emailEdits: new()), "email import");
});
Test("Changing selected dates scopes table hours and fill without losing original edits", () =>
{
    var session = Session(15, 15); var workbook = session.EmailWorkbook!; var next = session.Days[0].Date.AddDays(1);
    session = session with { Days = [session.Days[0], new() { Date = next }], EmailWorkbook = workbook with { Metadata = workbook.Metadata with { PeriodEnd = next }, Activities = [workbook.Activities[0], workbook.Activities[1] with { Date = next }] } };
    var edits = new EmailReviewEdits { ActivityHours = [new("a0", 15, .5m), new("a1", 15, 1m)], FillWeekdays = true, TargetHours = 8m };
    var scope = ProposalScope.Select(session, Proposal(session), [next]);
    var selected = EmailRules.Validate(scope.Session, scope.Proposal, emailEdits: edits); Valid(selected);
    Equal(selected.Rows.Sum(r => r.Hours), 8m); True(selected.Rows.All(r => r.Date == next));
    Equal(selected.Rows.Single(r => r.Kind == "work").Hours, 1m);
    var restored = EmailRules.Validate(session, Proposal(session), emailEdits: edits); Valid(restored);
    Equal(restored.Rows.Sum(r => r.Hours), 16m); Equal(restored.Rows.Single(r => r.Kind == "work" && r.Date != next).Hours, .5m);
});
Test("Confirmed fragments combine before five-minute rounding", () =>
{
    var session = Session(1, 1, 1); var result = EmailRules.Validate(session, Proposal(session)); Valid(result);
    Equal(result.Rows.Count, 1); Equal(result.Rows[0].Hours, 0.08m); Equal(result.Rows[0].SourceActivityIds.Count, 3);
});
Test("Rounding is configurable and retains small amounts", () =>
{
    foreach (var (increment, hours) in new[] { (0, 0.02m), (5, 0.08m), (15, 0.25m) })
    {
        var session = Session(1); session = session with { Settings = session.Settings with { EmailRoundingMinutes = increment } };
        var result = EmailRules.Validate(session, Proposal(session)); Valid(result); Equal(result.Rows[0].Hours, hours);
    }
});
Test("Different billing choices stay separate with the same assignment and task", () =>
{
    var session = Session(5, 5); var proposal = Proposal(session);
    proposal = proposal with { Rows = [proposal.Rows[0] with { BillableOverride = true }, proposal.Rows[1] with { BillableOverride = false }] };
    var result = EmailRules.Validate(session, proposal); Valid(result); Equal(result.Rows.Count, 2);
    True(result.Rows[0].BillableOverride == true && result.Rows[1].BillableOverride == false);
    Equal(result.Rows[0].Assignment, result.Rows[1].Assignment); Equal(result.Rows[0].Task, result.Rows[1].Task);
});
Test("Revalidating billing changes merges compatible fragments before rounding", () =>
{
    var session = Session(1, 1); var proposal = Proposal(session);
    proposal = proposal with { Rows = [proposal.Rows[0] with { BillableOverride = true }, proposal.Rows[1] with { BillableOverride = false }] };
    var split = EmailRules.Validate(session, proposal); Valid(split); Equal(split.Rows.Sum(r => r.Hours), 0.16m);
    var merged = EmailRules.Validate(session, proposal with { Rows = proposal.Rows.Select(r => r with { BillableOverride = false }).ToList() });
    Valid(merged); Equal(merged.Rows.Count, 1); Equal(merged.Rows[0].Hours, 0.08m); Equal(merged.Rows[0].EmailEvidenceKeys.Count, 2);
});
Test("Evidence keys survive renamed rows descriptions and changed minutes", () =>
{
    var first = Session(5); var initial = EmailRules.Validate(first, Proposal(first)); Valid(initial);
    var second = first with { EmailWorkbook = first.EmailWorkbook! with { Activities = [first.EmailWorkbook!.Activities[0] with { ActivityId = "changed", Minutes = 11, Description = "Different summary" }] } };
    var changed = EmailRules.Validate(second, Proposal(second)); Valid(changed);
    Equal(initial.Rows[0].EmailEvidenceKeys[0], changed.Rows[0].EmailEvidenceKeys[0]);
    Equal(EmailRules.EvidenceKey("ALEX@EXAMPLE.COM", "message"), EmailRules.EvidenceKey("alex@example.com", "message"));
    True(EmailRules.EvidenceKey("other@example.com", "message") != EmailRules.EvidenceKey("alex@example.com", "message"));
    True(EmailRules.EvidenceKey("alex@example.com", "Message") != EmailRules.EvidenceKey("alex@example.com", "message"));
});
Test("Workbook must match the verified employee and timezone", () =>
{
    var session = Session();
    Reject(EmailRules.Validate(session with { EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { EmployeeEmail = "other@example.com" } } }, Proposal(session)), "employee email");
    Reject(EmailRules.Validate(session with { EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { TimeZone = "Pacific Standard Time" } } }, Proposal(session)), "time zone");
    var iana = session with { EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { TimeZone = "America/New_York" } } };
    Valid(EmailRules.Validate(iana, Proposal(iana)));
});
Test("Workbook requires employee confirmation of every allocation", () =>
{
    var session = Session(); session = session with { EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { EmployeeConfirmed = false } } };
    Reject(EmailRules.Validate(session, Proposal(session)), "employee_confirmed");
});
Test("One evidence message cannot be reused between activities or dates", () =>
{
    var session = Session(5, 5); var rows = session.EmailWorkbook!.Activities;
    session = session with { EmailWorkbook = session.EmailWorkbook with { Activities = [rows[0], rows[1] with { EvidenceIds = rows[0].EvidenceIds }] } };
    Reject(EmailRules.Validate(session, Proposal(session)), "only once");
});
Test("Every selected-date activity needs exactly one mapping", () =>
{
    var session = Session(5, 5); var proposal = Proposal(session);
    Reject(EmailRules.Validate(session, proposal with { Rows = [proposal.Rows[0]] }), "missing");
    Reject(EmailRules.Validate(session, proposal with { Rows = [proposal.Rows[0], proposal.Rows[0]] }), "more than once");
});
Test("Unknown project assignment task and category relationships are rejected", () =>
{
    var session = Session(); var proposal = Proposal(session); var row = proposal.Rows[0];
    foreach (var changed in new[] { row with { Project = 101 }, row with { Assignment = null }, row with { Assignment = 9999 }, row with { Task = 9999 }, row with { Category = 11 } })
        True(!EmailRules.Validate(session, proposal with { Rows = [changed] }).IsValid);
});
Test("Direct email project time groups separately from assignment work and retains local corrections", () =>
{
    var session = Session(5, 5); session = session with { Reference = session.Reference with { Projects = [new(100, "Example case")] } };
    var proposal = Proposal(session); proposal = proposal with { Rows = [proposal.Rows[0] with { Assignment = null }, proposal.Rows[1]] };
    var result = EmailRules.Validate(session, proposal, emailEdits: new() { ActivityHours = [new("a0", 5, .25m)] }); Valid(result);
    Equal(result.Rows.Count, 2); var direct = result.Rows.Single(r => r.Assignment is null);
    Equal(direct.ProjectName, "Example case"); Equal(direct.AssignmentName, "Direct to project"); Equal(direct.Hours, .25m);
    Equal(direct.SourceActivityIds.Single(), "a0"); True(direct.EmailEvidenceKeys.Count == 1);
    Reject(EmailRules.Validate(session with { Reference = session.Reference with { Projects = [] } }, proposal), "trusted project choices");
    Reject(EmailRules.Validate(session, proposal with { Rows = [proposal.Rows[0], proposal.Rows[0]] }), "more than once");
});
Test("Manual email additions retain source identity use exact hours and reduce weekday fill", () =>
{
    var session = Session(5); var original = JsonSerializer.Serialize(session.EmailWorkbook, JsonDefaults.Options);
    var manual = new ManualTimecard { Date = session.Days[0].Date, Hours = .26m, Project = 100, Assignment = 501, Task = 60, Description = "Additional administration" };
    session = session with { ManualTimecards = [manual] };
    var result = EmailRules.Validate(session, Proposal(session), emailEdits: new() { FillWeekdays = true, AddTimecards = true }); Valid(result);
    Equal(result.Rows.Single(r => r.Kind == "manual").Hours, .26m); Equal(result.Rows.Single(r => r.Kind == "misc_internal").Hours, 7.49m); Equal(result.Rows.Sum(r => r.Hours), 8m);
    Equal(JsonSerializer.Serialize(session.EmailWorkbook, JsonDefaults.Options), original);
    Reject(EmailRules.Validate(session, Proposal(session) with { Rows = [] }), "missing");
});
Test("Manual exact email duplicate keeps email evidence and stable manual ID once", () =>
{
    var session = Session(5); var activity = session.EmailWorkbook!.Activities.Single();
    var manual = new ManualTimecard { Date = activity.Date, Hours = .08m, Project = 100, Assignment = 501, Task = 60, Description = activity.Description };
    session = session with { ManualTimecards = [manual] };
    var result = EmailRules.Validate(session, Proposal(session)); Valid(result); Equal(result.Rows.Count, 1);
    var row = result.Rows.Single(); Equal(row.Kind, "work"); Equal(row.Hours, .08m); Equal(row.ManualEntryIds.Single(), manual.Id); Equal(row.SourceActivityIds.Single(), activity.ActivityId); Equal(row.EmailEvidenceKeys.Count, 1);
    var edits = new EmailReviewEdits { ActivityHours = [new(activity.ActivityId, activity.Minutes, .25m)] };
    var corrected = EmailRules.Validate(session, Proposal(session), emailEdits: edits); Valid(corrected); Equal(corrected.Rows.Count, 2); Equal(corrected.Rows.Sum(r => r.Hours), .33m);
});
Test("Manual email dates outside workbook preserve scope and empty leftover baselines add nothing", () =>
{
    var session = Session(5); var next = session.Days[0].Date.AddDays(1); var workbook = session.EmailWorkbook!;
    var manual = new ManualTimecard { Date = next, Hours = .25m, Project = 100, Assignment = 501, Task = 60, Description = "Next-day administration" };
    session = session with { ManualTimecards = [manual], Days = [session.Days[0], new() { Date = next }] };
    var policy = new EmailReviewEdits { FillWeekdays = true, AddTimecards = true };
    var result = EmailRules.Validate(session, Proposal(session), emailEdits: policy); Valid(result); Equal(result.Rows.Sum(r => r.Hours), 16m);
    var scope = ProposalScope.Select(session, Proposal(session), [next]); var selected = EmailRules.Validate(scope.Session, scope.Proposal, emailEdits: policy); Valid(selected); Equal(selected.Rows.Sum(r => r.Hours), 8m);
    Equal(session.EmailWorkbook, workbook); Equal(scope.Session.EmailWorkbook, workbook);
    var removed = EmailRules.Validate(session with { ManualTimecards = [] }, Proposal(session), emailEdits: policy); Valid(removed); RequireNoNextDay();
    void RequireNoNextDay() => True(removed.Rows.All(r => r.Date != next));
});
Test("Email direct project suggestions require a unique trusted exact match", () =>
{
    var session = Session(); var activity = session.EmailWorkbook!.Activities[0] with { MatterHint = "Case intake example", TaskHint = "Internal work" };
    session = session with { Reference = session.Reference with { Assignments = [], Projects = [new(777, "Case intake example")] } };
    var suggestion = EmailSuggestions.Suggest(activity, session); Equal(suggestion.ProjectId, 777); Equal(suggestion.AssignmentId, null); True(!suggestion.Internal);
    Equal(EmailSuggestions.Suggest(activity with { MatterHint = "Different" }, session).ProjectId, null);
    Equal(EmailSuggestions.Suggest(activity, session with { Reference = session.Reference with { Projects = [new(777, "Case intake example"), new(778, "Case intake example")] } }).ProjectId, null);
    var invalid = session with { Reference = session.Reference with { Projects = [new(777, "Case intake example"), new(777, "Case intake example")] } };
    Equal(EmailSuggestions.Suggest(activity, invalid).ProjectId, null);
    True(!EmailRules.Validate(invalid, Proposal(invalid)).IsValid);
});
Test("Selected dates narrow coverage without discarding original workbook", () =>
{
    var session = Session(5, 5); var day = session.Days[0].Date;
    session = session with { EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { PeriodEnd = day.AddDays(1) }, Activities = [session.EmailWorkbook.Activities[0], session.EmailWorkbook.Activities[1] with { Date = day.AddDays(1) }] } };
    var result = EmailRules.Validate(session, Proposal(session)); Valid(result); Equal(result.Rows.Count, 1);
});
Test("Date scope filters excluded existing links but preserves unknown links for rejection", () =>
{
    var session = Session(5, 5); var day = session.Days[0].Date;
    session = session with { Days = [session.Days[0], new DaySnapshot { Date = day.AddDays(1), Existing = [new ExistingTimecard(8001, day.AddDays(1), 0.08m, 100, 60, 20, 501, "Existing next day")] }],
        EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { PeriodEnd = day.AddDays(1) }, Activities = [session.EmailWorkbook.Activities[0], session.EmailWorkbook.Activities[1] with { Date = day.AddDays(1) }] } };
    var proposal = Proposal(session); proposal = proposal with { Rows = [proposal.Rows[0]], EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "a1", ExistingRecordId = 8001 }] };
    var scope = ProposalScope.Select(session, proposal, [day]);
    Equal(scope.Proposal.EmailAlreadyRecorded.Count, 0); Valid(EmailRules.Validate(scope.Session, scope.Proposal));
    proposal = proposal with { EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "invented", ExistingRecordId = 8001 }] };
    scope = ProposalScope.Select(session, proposal, [day]); Equal(scope.Proposal.EmailAlreadyRecorded.Count, 1); Reject(EmailRules.Validate(scope.Session, scope.Proposal), "not on the selected dates");
});
Test("Already-recorded links cover work but cannot exceed existing hours", () =>
{
    var session = Existing(Session(5), 0.08m); var proposal = Proposal(session) with { Rows = [], EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "a0", ExistingRecordId = 8001 }] };
    var result = EmailRules.Validate(session, proposal); Valid(result); Equal(result.Rows.Count, 0); True(result.Warnings.Any(w => w.Contains("Confirm already recorded")));
    Reject(EmailRules.Validate(Existing(session, 0.01m), proposal), "exceed");
    Reject(EmailRules.Validate(session, proposal with { EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "a0", ExistingRecordId = 9999 }] }), "not in this import");
});
Test("Changing rounding policy does not rebill already recorded activity", () =>
{
    var session = Existing(Session(5), 0.08m); session = session with { Settings = session.Settings with { EmailRoundingMinutes = 15 } };
    var proposal = Proposal(session) with { Rows = [], EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "a0", ExistingRecordId = 8001 }] };
    var result = EmailRules.Validate(session, proposal); Valid(result); Equal(result.Rows.Count, 0);
});
Test("Already-recorded links cannot also be submitted or point to another day", () =>
{
    var session = Existing(Session(5), 0.08m); var proposal = Proposal(session) with { EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "a0", ExistingRecordId = 8001 }] };
    Reject(EmailRules.Validate(session, proposal), "more than once");
    var day = session.Days[0].Date; var card = session.Days[0].Existing[0] with { Date = day.AddDays(1) };
    session = session with { Days = [session.Days[0] with { Existing = [] }, new DaySnapshot { Date = day.AddDays(1), Existing = [card] }], EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { PeriodEnd = day.AddDays(1) } } };
    Reject(EmailRules.Validate(session, proposal with { Rows = [] }), "different dates");
});
Test("Existing and proposed time cannot exceed 24 hours", () =>
{
    var session = Existing(Session(60), 23.5m); Reject(EmailRules.Validate(session, Proposal(session)), "exceeds 24 hours");
});
Test("No billing cap or automatic filler is applied by default", () =>
{
    var session = Session(540); var result = EmailRules.Validate(session, Proposal(session)); Valid(result); Equal(result.Rows.Sum(r => r.Hours), 9m);
});
Test("Opt-in daily defaults add timecards and fill without reducing worked time", () =>
{
    foreach (var (minutes, total) in new[] { (420, 8m), (540, 9.17m) })
    {
        var session = Session(minutes); session = session with { Settings = session.Settings with { EmailApplyDailyDefaults = true } };
        var result = EmailRules.Validate(session, Proposal(session)); Valid(result); Equal(result.Rows.Sum(r => r.Hours), total);
    }
});
Test("Existing Timecards is not added twice", () =>
{
    var session = Session(420); session = session with { Settings = session.Settings with { EmailApplyDailyDefaults = true }, Days = [session.Days[0] with { Existing = [new ExistingTimecard(8001, session.Days[0].Date, 0.17m, 900, 44, 11, null, "Timecards")] }] };
    var result = EmailRules.Validate(session, Proposal(session)); Valid(result); True(result.Rows.All(r => r.Kind != "timecards")); Equal(result.Rows.Sum(r => r.Hours), 7.83m);
});
Test("Already-recorded Timecards source avoids duplicate even with a different record description", () =>
{
    var session = Session(420, 10); session = session with { Settings = session.Settings with { EmailApplyDailyDefaults = true },
        EmailWorkbook = session.EmailWorkbook! with { Activities = [session.EmailWorkbook!.Activities[0], session.EmailWorkbook.Activities[1] with { Description = "Timecards" }] },
        Days = [session.Days[0] with { Existing = [new ExistingTimecard(8001, session.Days[0].Date, 0.17m, 900, 44, 11, null, "Daily timecard preparation")] }] };
    var proposal = Proposal(session); proposal = proposal with { Rows = [proposal.Rows[0]], EmailAlreadyRecorded = [new RecordedActivityLink { ActivityId = "a1", ExistingRecordId = 8001 }] };
    var result = EmailRules.Validate(session, proposal); Valid(result); True(result.Rows.All(r => r.Kind != "timecards")); Equal(result.Rows.Sum(r => r.Hours), 7.83m);
});
Test("Existing time and incomplete retrieval produce review warnings", () =>
{
    var session = Existing(Session(5), 1m); session = session with { EmailWorkbook = session.EmailWorkbook! with { Metadata = session.EmailWorkbook!.Metadata with { RetrievalLimitations = "Teams calls unavailable" } } };
    var result = EmailRules.Validate(session, Proposal(session)); Valid(result);
    True(result.Warnings.Any(w => w.Contains("already contains"))); True(result.Warnings.Any(w => w.Contains("Teams calls unavailable")));
});
Test("Import identity age and mixed Toggl sources cannot bypass validation", () =>
{
    var session = Session(); var proposal = Proposal(session);
    Reject(EmailRules.Validate(session, proposal with { SessionId = "different" }), "different import");
    Reject(EmailRules.Validate(session with { GeneratedAtUtc = DateTimeOffset.UtcNow.AddHours(-25) }, proposal), "24 hours");
    Reject(EmailRules.Validate(session, proposal with { Rows = [proposal.Rows[0] with { SourceEntryIds = [1] }] }), "Toggl");
});
Test("Invalid evidence, duration, basis and date are rejected before mapping", () =>
{
    var session = Session(); var activity = session.EmailWorkbook!.Activities[0];
    foreach (var invalid in new[] { activity with { Minutes = 0 }, activity with { Minutes = 1441 }, activity with { EvidenceIds = [] }, activity with { EvidenceIds = [""] }, activity with { Date = activity.Date.AddDays(1) }, activity with { TimeBasis = "AI_guess" } })
        True(!EmailRules.Validate(session with { EmailWorkbook = session.EmailWorkbook with { Activities = [invalid] } }, Proposal(session)).IsValid);
});
Test("Email source and evidence persist in the saved session and receipt schema", () =>
{
    var session = Session(); var restored = JsonSerializer.Deserialize<ReadSession>(JsonSerializer.Serialize(session, JsonDefaults.Options), JsonDefaults.Options)!;
    var result = EmailRules.Validate(restored, Proposal(restored)); Valid(result);
    var receipt = new SubmissionReceipt { Rows = [new RowOutcome { Row = result.Rows[0], Status = "created", RecordId = 8001 }] };
    var saved = JsonSerializer.Deserialize<SubmissionReceipt>(JsonSerializer.Serialize(receipt, JsonDefaults.Options), JsonDefaults.Options)!;
    Equal(saved.Rows[0].Row.EmailEvidenceKeys[0], result.Rows[0].EmailEvidenceKeys[0]);
});

Test("Plain inline-string XLSX imports exact metadata dates and evidence", () =>
{
    using var book = new WorkbookFixture(); var result = EmailWorkbookReader.Read(book.Path);
    Equal(result.Activities.Count, 1); Equal(result.Activities[0].Minutes, 5); Equal(result.Activities[0].Billable, false); Equal(result.ContentSha256.Length, 64); Equal(result.Metadata.PeriodStart, new DateOnly(2026, 9, 28));
});
Test("Shared string and Boolean XLSX cells are supported", () =>
{
    using var book = new WorkbookFixture(parts =>
    {
        parts["xl/sharedStrings.xml"] = "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><si><t>a0</t></si></sst>";
        parts["xl/worksheets/sheet2.xml"] = parts["xl/worksheets/sheet2.xml"].Replace("<c r=\"A2\" t=\"inlineStr\"><is><t>a0</t></is></c>", "<c r=\"A2\" t=\"s\"><v>0</v></c>").Replace("<c r=\"H2\" t=\"inlineStr\"><is><t>false</t></is></c>", "<c r=\"H2\" t=\"b\"><v>0</v></c>");
    });
    var result = EmailWorkbookReader.Read(book.Path); Equal(result.Activities[0].ActivityId, "a0"); Equal(result.Activities[0].Billable, false);
});
Test("Numeric formatting does not change whole minutes", () =>
{
    using var book = new WorkbookFixture(p => p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("<v>5</v>", "<v>5.0</v>"));
    Equal(EmailWorkbookReader.Read(book.Path).Activities[0].Minutes, 5);
});
Test("Blank formatted cells outside the table do not add columns", () =>
{
    using var book = new WorkbookFixture(p => p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("</row><row r=\"2\">", "<c r=\"K1\" s=\"1\" /></row><row r=\"2\">"));
    Equal(EmailWorkbookReader.Read(book.Path).Activities.Count, 1);
});
Test("Missing or duplicate metadata is rejected", () =>
{
    using var missing = new WorkbookFixture(p => p["xl/worksheets/sheet1.xml"] = p["xl/worksheets/sheet1.xml"].Replace("employee_email", "unknown_key"));
    Throws(() => EmailWorkbookReader.Read(missing.Path), "unknown key");
});
Test("Formula cells cannot provide cached values to bypass validation", () =>
{
    using var book = new WorkbookFixture(p => p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("<v>5</v>", "<f>2+3</f><v>5</v>"));
    Throws(() => EmailWorkbookReader.Read(book.Path), "Formula");
});
Test("External links macros and embedded data are refused", () =>
{
    using var external = new WorkbookFixture(p => p["xl/_rels/workbook.xml.rels"] = p["xl/_rels/workbook.xml.rels"].Replace("Target=", "TargetMode='External' Target="));
    Throws(() => EmailWorkbookReader.Read(external.Path), "External links");
    using var macros = new WorkbookFixture(p => p["xl/vbaProject.bin"] = "macro");
    Throws(() => EmailWorkbookReader.Read(macros.Path), "macros");
    using var embedded = new WorkbookFixture(p => p["xl/embeddings/object.bin"] = "object");
    Throws(() => EmailWorkbookReader.Read(embedded.Path), "embedded objects");
});
Test("DTD XML and path traversal are refused", () =>
{
    using var dtd = new WorkbookFixture(p => p["xl/sharedStrings.xml"] = "<!DOCTYPE sst [<!ENTITY bad SYSTEM 'file:///C:/Windows/win.ini'>]><sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><si><t>&bad;</t></si></sst>");
    Throws(() => EmailWorkbookReader.Read(dtd.Path), "structure is invalid");
    using var traversal = new WorkbookFixture(p => p["xl/../untrusted.xml"] = "<x />");
    Throws(() => EmailWorkbookReader.Read(traversal.Path), "package path");
});
Test("Expanded package limit catches tiny compressed bombs", () =>
{
    using var book = new WorkbookFixture(p => p["xl/oversized.xml"] = new string('A', 8 * 1024 * 1024 + 1));
    Throws(() => EmailWorkbookReader.Read(book.Path), "expands beyond");
});
Test("Duplicate ZIP parts and worksheet coordinates are rejected", () =>
{
    using var book = new WorkbookFixture();
    using (var archive = ZipFile.Open(book.Path, ZipArchiveMode.Update))
    using (var writer = new StreamWriter(archive.CreateEntry("xl/workbook.xml").Open())) writer.Write("<x />");
    Throws(() => EmailWorkbookReader.Read(book.Path), "duplicate package");
    using var cells = new WorkbookFixture(p => p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("r=\"B2\"", "r=\"A2\""));
    Throws(() => EmailWorkbookReader.Read(cells.Path), "repeated cell");
});
Test("Ambiguous date text and fractional minutes cannot be silently coerced", () =>
{
    using var date = new WorkbookFixture(p => p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("2026-09-28", "09/10/2026"));
    Throws(() => EmailWorkbookReader.Read(date.Path), "ambiguous date");
    using var minutes = new WorkbookFixture(p => p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("<v>5</v>", "<v>5.5</v>"));
    Throws(() => EmailWorkbookReader.Read(minutes.Path), "whole number");
});

Test("review workbook stays a draft until explicitly confirmed locally", () =>
{
    using var book = new WorkbookFixture(ReviewBook.Convert);
    var draft = EmailWorkbookReader.Read(book.Path);
    True(draft.IsReviewWorkbook); True(!draft.Metadata.EmployeeConfirmed); Equal(draft.Activities.Count, 1); Equal(draft.Activities[0].Minutes, 15);
    True(EmailWorkbookReader.Validate(draft).Any(e => e.Contains("confirmation")));
    var confirmed = EmailWorkbookReader.ConfirmReview(draft);
    Equal(EmailWorkbookReader.Validate(confirmed).Count, 0); True(confirmed.ReviewConfirmedAtUtc.HasValue);
    True(!draft.Metadata.EmployeeConfirmed); True(confirmed.Activities[0].EvidenceIds[0].StartsWith("review-row:"));
    var settings = new AppSettings { Email = "different@example.com" };
    True(EmailWorkbookReader.Validate(confirmed, settings).Any(e => e.Contains("does not match")));
});
Test("review imports reject inconsistent duration and total cells", () =>
{
    using var hours = new WorkbookFixture(p => { ReviewBook.Convert(p); p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("0.25", "0.50"); });
    Throws(() => EmailWorkbookReader.Read(hours.Path), "disagree");
    using var totals = new WorkbookFixture(p => { ReviewBook.Convert(p); p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("<t>15</t>", "<t>20</t>"); });
    Throws(() => EmailWorkbookReader.Read(totals.Path), "disagree");
});
Test("review row fingerprints survive duration and description edits", () =>
{
    using var first = new WorkbookFixture(ReviewBook.Convert);
    using var second = new WorkbookFixture(p => { ReviewBook.Convert(p); p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("<t>15</t>", "<t>30</t>").Replace("0.25", "0.50").Replace("Worked on client layout", "Revised description"); });
    var a = EmailWorkbookReader.Read(first.Path); var b = EmailWorkbookReader.Read(second.Path);
    Equal(a.Activities[0].EvidenceIds[0], b.Activities[0].EvidenceIds[0]); True(a.ContentSha256 != b.ContentSha256);
});
Test("review imports reject formulas and merged activity cells", () =>
{
    using var formula = new WorkbookFixture(p => { ReviewBook.Convert(p); p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("</sheetData>", "</sheetData><f>1+1</f>"); });
    Throws(() => EmailWorkbookReader.Read(formula.Path), "Formula");
    using var merge = new WorkbookFixture(p => { ReviewBook.Convert(p); p["xl/worksheets/sheet2.xml"] = p["xl/worksheets/sheet2.xml"].Replace("</sheetData>", "</sheetData><mergeCells><mergeCell ref='A4:B4'/></mergeCells>"); });
    Throws(() => EmailWorkbookReader.Read(merge.Path), "merged title");
});
Test("review format retains limited duplicate detection warning through validation", () =>
{
    using var book = new WorkbookFixture(ReviewBook.Convert);
    var draft = EmailWorkbookReader.Read(book.Path);
    var session = Session(15); var confirmed = EmailWorkbookReader.ConfirmReview(draft);
    session = session with { EmailWorkbook = confirmed with { Metadata = confirmed.Metadata with { EmployeeEmail = session.Settings.Email } } };
    var result = Rules.Validate(session, Proposal(session)); Valid(result);
    True(result.Warnings.Any(w => w.Contains("no stable message")));
    var serialized = JsonSerializer.Serialize(session, JsonDefaults.Options);
    var restored = JsonSerializer.Deserialize<ReadSession>(serialized, JsonDefaults.Options)!;
    True(restored.EmailWorkbook!.IsReviewWorkbook); True(restored.EmailWorkbook.ReviewConfirmedAtUtc.HasValue);
});

Test("review hours and totals are recalculated instead of trusting formula caches", () =>
{
    using var book = new WorkbookFixture(p =>
    {
        ReviewBook.Convert(p);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var doc = XDocument.Parse(p["xl/worksheets/sheet2.xml"]);
        foreach (var (address, formula) in new[] { ("G4", "F4/60"), ("F5", "SUM(F4:F4)"), ("G5", "SUM(G4:G4)") })
        {
            var cell = doc.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == address);
            cell.Attribute("t")?.Remove(); cell.ReplaceNodes(new XElement(ns + "f", formula), new XElement(ns + "v", "999"));
        }
        p["xl/worksheets/sheet2.xml"] = doc.ToString();
    });
    var workbook = EmailWorkbookReader.Read(book.Path); Equal(workbook.Activities.Single().Minutes, 15);
});
Test("review formula cannot substitute another row or an external calculation", () =>
{
    foreach (var expression in new[] { "F5/60", "WEBSERVICE(1)", "SUM(F4:F999)" })
    {
        using var book = new WorkbookFixture(p =>
        {
            ReviewBook.Convert(p); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var doc = XDocument.Parse(p["xl/worksheets/sheet2.xml"]);
            var cell = doc.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "G4");
            cell.Attribute("t")?.Remove(); cell.ReplaceNodes(new XElement(ns + "f", expression), new XElement(ns + "v", "0.25"));
            p["xl/worksheets/sheet2.xml"] = doc.ToString();
        });
        Throws(() => EmailWorkbookReader.Read(book.Path), "unsupported");
    }
});
WorkbookValueTests.Register(Test);
WorkbookNameTests.Register(Test);
MinimalWorkbookTests.Register(Test);
EmailSuggestionTests.Register(Test);
if (args.Length == 2 && args[0] == "--inspect-workbook")
{
    try
    {
        var workbook = EmailWorkbookReader.Read(args[1]);
        Console.WriteLine($"Reference workbook: {workbook.Activities.Count} activities, {workbook.Activities.Sum(a => a.Minutes)} minutes, format={workbook.Metadata.Format}, confirmed={workbook.Metadata.EmployeeConfirmed}, {workbook.ReviewNotes.Count} notes.");
        var confirmed = workbook.IsMinimalImport ? EmailWorkbookReader.ConfirmMinimalImport(workbook)
            : workbook.IsReviewWorkbook ? EmailWorkbookReader.ConfirmReview(workbook) : workbook;
        Equal(EmailWorkbookReader.Validate(confirmed).Count, 0);
        Console.WriteLine("PASS reference workbook parses and validates after local confirmation; no network calls or records written.");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Workbook inspection failed: " + error.Message);
        return 1;
    }
}
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} email checks passed.");
return failed == 0 ? 0 : 1;

sealed class WorkbookFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "timekeeper-email-tests-" + Guid.NewGuid().ToString("N") + ".xlsx");
    public WorkbookFixture(Action<Dictionary<string, string>>? change = null)
    {
        var ns = (XNamespace)"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string Sheet(string[][] rows) => new XDocument(new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows.Select((values, r) => new XElement(ns + "row", new XAttribute("r", r + 1), values.Select((value, c) => value.StartsWith('#')
            ? new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + c)}{r + 1}"), new XElement(ns + "v", value[1..]))
            : new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + c)}{r + 1}"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value))))))))).ToString(SaveOptions.DisableFormatting);
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/></Types>",
            ["xl/workbook.xml"] = "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Metadata' sheetId='1' r:id='r1'/><sheet name='Activities' sheetId='2' r:id='r2'/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/><Relationship Id='r2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = Sheet([["key", "value"], ["format", "timekeeper_email_activity"], ["schema_version", "1"], ["employee_email", "alex@example.com"], ["time_zone", "America/New_York"], ["period_start", "2026-09-28"], ["period_end", "2026-09-28"], ["generated_at_utc", "2026-09-28T22:00:00Z"], ["employee_confirmed", "true"], ["retrieval_limitations", "none"]]),
            ["xl/worksheets/sheet2.xml"] = Sheet([["activity_id", "date", "minutes", "matter_hint", "assignment_hint", "task_hint", "description", "billable", "time_basis", "evidence_ids"], ["a0", "2026-09-28", "#5", "Example case", "Presentation design", "Design", "Reviewed confirmed case correspondence", "false", "employee_confirmed_estimate", "<message-1@example.com>"]])
        };
        change?.Invoke(parts);
        using var archive = ZipFile.Open(Path, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            using var writer = new StreamWriter(archive.CreateEntry(part.Key, CompressionLevel.SmallestSize).Open(), new UTF8Encoding(false));
            writer.Write(part.Value);
        }
    }
    public void Dispose() => File.Delete(Path);
}

static class ReviewBook
{
    public static void Convert(Dictionary<string, string> parts)
    {
        parts["xl/workbook.xml"] = parts["xl/workbook.xml"].Replace("Activities", "Review");
        parts["xl/worksheets/sheet1.xml"] = parts["xl/worksheets/sheet1.xml"].Replace("timekeeper_email_activity", "timekeeper_email_review");
        string[][] rows = [["DRAFT - REVIEW ONLY"], [], ["Date", "Matter / Project", "Assignment / Category", "Detailed work summary", "Billing", "Minutes", "Decimal Hours", "Evidence Reviewed", "Needs Attention"],
            ["2026-09-28", "Example case", "Presentation", "Worked on client layout", "QUICKBASE DEFAULT", "15", "0.25", "Client layout email", "Confirm estimated time"],
            ["TOTAL", "", "", "", "", "15", "0.25", "", ""]];
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        parts["xl/worksheets/sheet2.xml"] = new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows.Select((values, r) =>
            new XElement(ns + "row", new XAttribute("r", r + 1), values.Select((value, c) => new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + c)}{r + 1}"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value)))))))).ToString(SaveOptions.DisableFormatting);
    }
}
