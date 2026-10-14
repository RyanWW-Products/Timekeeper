using System.Text.Json;
using Timekeeper.Core;

static class EmailSuggestionTests
{
    public static void Register(Action<string, Action> test)
    {
        test("email suggestions normalize project assignment and task names", () =>
        {
            var session = Session();
            var result = EmailSuggestions.Suggest(Activity("  SMITH v. JONES ", "Presentation-design", "DESIGN"), session);
            Equal<int?>(501, result.AssignmentId); Equal<int?>(60, result.TaskId); Require(!result.Internal);
            Contains(result.Reason, "Exact project and assignment"); Contains(result.Reason, "Task name matches");
        });
        test("email suggestions recognize accented names without depending on reference order", () =>
        {
            var session = WithAssignments(new AssignmentRecord(503, "Dossier", 103, "García v. López", "", "Active", ""));
            var result = EmailSuggestions.Suggest(Activity("Garcia v Lopez", "Dossier", "Design"), session);
            Equal<int?>(503, result.AssignmentId);
        });
        test("email suggestions use distinctive partial project and assignment hints", () =>
        {
            var session = WithAssignments(new AssignmentRecord(503, "Competition graphics production", 103, "Gold Cup Competition 2026", "", "Active", ""));
            var result = EmailSuggestions.Suggest(Activity("Gold Cup", "Competition graphics", "Design"), session);
            Equal<int?>(503, result.AssignmentId); Contains(result.Reason, "Suggested assignment");
        });
        test("generic exact assignment names cannot override a contradictory matter", () =>
        {
            var session = Session();
            var result = EmailSuggestions.Suggest(Activity("Unrelated v. Other", "Presentation design", "Design"), session);
            Equal<int?>(null, result.AssignmentId); Require(!result.Internal); Equal<int?>(60, result.TaskId);
            Contains(result.Reason, "No matching assignment");
        });
        test("a shared party name cannot substitute another case", () =>
        {
            var session = WithAssignments(new AssignmentRecord(503, "Presentation design", 103, "Smith v. Anderson", "", "Active", ""));
            var result = EmailSuggestions.Suggest(Activity("Smith v Jones", "Presentation design", "Design"), session);
            Equal<int?>(null, result.AssignmentId);
        });
        test("project matching uses token boundaries rather than name substrings", () =>
        {
            var session = WithAssignments(new AssignmentRecord(503, "Presentation design", 103, "Fleet v. Home", "", "Active", ""));
            var result = EmailSuggestions.Suggest(Activity("Lee", "Presentation design", "Design"), session);
            Equal<int?>(null, result.AssignmentId);
        });
        test("exact project evidence wins over a generic assignment on another case", () =>
        {
            var session = WithAssignments(
                new(501, "Presentation design", 100, "Other v. Company", "", "Active", ""),
                new(502, "Exhibit graphics", 101, "Smith v. Jones", "", "Active", ""));
            var result = EmailSuggestions.Suggest(Activity("Smith v Jones", "Presentation design", "Design"), session);
            Equal<int?>(502, result.AssignmentId);
        });
        test("active equivalent assignments are preferred over closed records", () =>
        {
            var session = WithAssignments(
                new(501, "Presentation design", 100, "Smith v. Jones", "", "Closed", ""),
                new(502, "Presentation design", 100, "Smith v. Jones", "", "Active", ""));
            Equal<int?>(502, EmailSuggestions.Suggest(Activity(), session).AssignmentId);
        });
        test("a plausible inactive-only assignment remains a clearly labeled suggestion", () =>
        {
            var session = WithAssignments(new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Archived", ""));
            var result = EmailSuggestions.Suggest(Activity(), session);
            Equal<int?>(501, result.AssignmentId); Contains(result.Reason, "closed or inactive");
        });
        test("equally plausible assignments choose deterministically and disclose ambiguity", () =>
        {
            var first = new AssignmentRecord(502, "Presentation design", 100, "Smith v. Jones", "", "Active", "");
            var second = first with { Id = 501 };
            var forward = EmailSuggestions.Suggest(Activity(), WithAssignments(first, second));
            var reverse = EmailSuggestions.Suggest(Activity(), WithAssignments(second, first));
            Equal<int?>(501, forward.AssignmentId); Equal(forward, reverse); Contains(forward.Reason, "several matches");
        });
        test("description can support a project-specific suggestion when hints are unavailable", () =>
        {
            var activity = Activity("Unknown", "", "") with { Description = "Prepared the Smith v Jones presentation." };
            Equal<int?>(501, EmailSuggestions.Suggest(activity, Session()).AssignmentId);
        });
        test("description is secondary to explicit project and assignment hints", () =>
        {
            var activity = Activity() with { Description = "Reviewed Acme v Beta correspondence while preparing research." };
            var result = EmailSuggestions.Suggest(activity, Session());
            Equal<int?>(501, result.AssignmentId);
        });
        test("generic activity text does not select an unrelated first assignment or task", () =>
        {
            var activity = Activity("Unknown", "", "") with { Description = "Completed miscellaneous work." };
            var result = EmailSuggestions.Suggest(activity, Session());
            Equal<int?>(null, result.AssignmentId); Equal<int?>(null, result.TaskId); Require(!result.Internal);
        });
        test("clearly internal labels use the configured internal project and task", () =>
        {
            foreach (var matter in new[] { "Internal", "General internal", "EXAMPLE COMPANY INTERNAL", "Administrative" })
            {
                var result = EmailSuggestions.Suggest(Activity(matter, "", ""), Session());
                Require(result.Internal); Equal<int?>(null, result.AssignmentId); Equal<int?>(44, result.TaskId);
                Contains(result.Reason, "configured internal");
            }
        });
        test("a case activity mentioning internal work does not become an internal project", () =>
        {
            var activity = Activity() with { Description = "Internal discussion about the Smith v Jones presentation." };
            var result = EmailSuggestions.Suggest(activity, Session());
            Equal<int?>(501, result.AssignmentId); Require(!result.Internal);
        });
        test("internal suggestions require an available configured project", () =>
        {
            var session = Session(); session = session with { Reference = session.Reference with { InternalProject = null } };
            var result = EmailSuggestions.Suggest(Activity("Internal", "", ""), session);
            Require(!result.Internal); Equal<int?>(null, result.AssignmentId);
        });
        test("a live linked assignment task fills an absent task hint", () =>
        {
            var session = WithAssignments(new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Active", "") { TaskId = 61, CategoryId = 20 });
            var result = EmailSuggestions.Suggest(Activity(task: ""), session);
            Equal<int?>(61, result.TaskId); Contains(result.Reason, "selected Quickbase assignment");
        });
        test("an exact task hint takes priority over the assignment default", () =>
        {
            var session = WithAssignments(new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Active", "") { TaskId = 61, CategoryId = 20 });
            Equal<int?>(60, EmailSuggestions.Suggest(Activity(task: "Design"), session).TaskId);
        });
        test("missing or incompatible linked tasks are not silently selected", () =>
        {
            foreach (var assignment in new[]
            {
                new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Active", "") { TaskId = 99999 },
                new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Active", "") { TaskId = 44, CategoryId = 20 }
            })
            {
                var result = EmailSuggestions.Suggest(Activity(task: "") with { Description = "Completed requested work." }, WithAssignments(assignment));
                Equal<int?>(null, result.TaskId);
            }
        });
        test("task suggestions only use live tasks with available categories", () =>
        {
            var session = Session(); session = session with { Reference = session.Reference with { Categories = [new(11, "Internal")] } };
            Equal<int?>(null, EmailSuggestions.Suggest(Activity(), session).TaskId);
        });
        test("task hint partial matching beats description-only matching", () =>
        {
            var session = Session(); session = session with { Reference = session.Reference with { Tasks = [new(70, "Exhibit design", 20), new(71, "Research", 20)] } };
            var result = EmailSuggestions.Suggest(Activity(task: "Exhibit") with { Description = "Research project references." }, session);
            Equal<int?>(70, result.TaskId);
        });
        test("task name ambiguity prefers the assignment category and otherwise uses stable IDs", () =>
        {
            var session = WithAssignments(new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Active", "") { CategoryId = 20 });
            session = session with { Reference = session.Reference with { Tasks = [new(10, "Meeting", 11), new(70, "Meeting", 20)] } };
            Equal<int?>(70, EmailSuggestions.Suggest(Activity(task: "Meeting"), session).TaskId);
            var unknown = EmailSuggestions.Suggest(Activity("Unknown", "", "Meeting"), session);
            Equal<int?>(10, unknown.TaskId); Contains(unknown.Reason, "multiple tasks");
        });
        test("task helper recomputes from the newly selected assignment", () =>
        {
            var session = WithAssignments(
                new AssignmentRecord(501, "Presentation design", 100, "Smith v. Jones", "", "Active", "") { TaskId = 60, CategoryId = 20 },
                new AssignmentRecord(502, "Audience research", 101, "Acme v. Beta", "", "Active", "") { TaskId = 61, CategoryId = 20 });
            var activity = Activity(task: "");
            Equal<int?>(60, EmailSuggestions.SuggestTask(activity, session.Reference, 501, false, 44));
            Equal<int?>(61, EmailSuggestions.SuggestTask(activity, session.Reference, 502, false, 44));
            Equal<int?>(44, EmailSuggestions.SuggestTask(activity, session.Reference, null, true, 44));
        });
        test("mapping suggestions do not mutate sources or inspect existing timecards", () =>
        {
            var session = Session(); var activity = Activity();
            var expected = EmailSuggestions.Suggest(activity, session);
            session = session with { Days = [new DaySnapshot { Date = activity.Date, Existing = [new ExistingTimecard(9001, activity.Date, .25m, 100, 60, 20, 501, activity.Description)] }] };
            var before = JsonSerializer.Serialize(new { session, activity }, JsonDefaults.Options);
            Equal(expected, EmailSuggestions.Suggest(activity, session));
            Equal(before, JsonSerializer.Serialize(new { session, activity }, JsonDefaults.Options));
        });
    }

    private static EmailActivity Activity(string matter = "Smith v Jones", string assignment = "Presentation design", string task = "Design") => new()
    {
        Date = new DateOnly(2026, 10, 13), Minutes = 15, MatterHint = matter, AssignmentHint = assignment, TaskHint = task, Description = "Prepared requested materials."
    };
    private static ReadSession Session() => WithAssignments(
        new(501, "Presentation design", 100, "Smith v. Jones", "", "Active", ""),
        new(502, "Audience research", 101, "Acme v. Beta", "", "Active", ""));
    private static ReadSession WithAssignments(params AssignmentRecord[] assignments)
    {
        var seed = DemoData.CreateSession();
        return seed with { Reference = seed.Reference with { Assignments = assignments.ToList() } };
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Require(bool condition) { if (!condition) throw new Exception("Email mapping suggestion assertion failed."); }
    private static void Contains(string value, string fragment) { if (!value.Contains(fragment, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Expected '{fragment}' in '{value}'."); }
}
