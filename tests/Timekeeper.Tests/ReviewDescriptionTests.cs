using System.Text.Json;
using Timekeeper.Core;

static class ReviewDescriptionTests
{
    public static void Register(Action<string, Action> test)
    {
        test("description review edits preserve source identity mappings billing and edited hours", () =>
        {
            var session = DemoData.CreateSession(); var proposal = DemoData.CreateProposal(session);
            var originalSession = JsonSerializer.Serialize(session); var originalProposal = JsonSerializer.Serialize(proposal);
            var baseline = Rules.Validate(session, proposal); Valid(baseline); var original = baseline.Rows.First(r => r.Kind == "work");
            var withHours = ReviewDurations.Apply(session, baseline, [new(ReviewDurations.Key(original), original.Hours, .5m)]);
            var row = withHours.Rows.Single(r => ReviewDurations.Key(r) == ReviewDurations.Key(original));
            var edited = ReviewDescriptions.Apply(session, withHours, [new(ReviewDurations.Key(row), row.Description, "  Revised activity\r\nwith more detail.  ")]); Valid(edited);
            var changed = edited.Rows.Single(r => ReviewDurations.Key(r) == ReviewDurations.Key(row));
            Equal("Revised activity\r\nwith more detail.", changed.Description);
            Equal(JsonSerializer.Serialize(row), JsonSerializer.Serialize(changed with { Description = row.Description }));
            Equal(originalSession, JsonSerializer.Serialize(session)); Equal(originalProposal, JsonSerializer.Serialize(proposal));
            Equal(original.Description, baseline.Rows.First(r => r.Kind == "work").Description);
            Require(edited.Warnings.Any(w => w.Contains("description changed in review")));
        });
        test("description edits reject invalid text stale originals unknown keys and repeated keys", () =>
        {
            var session = DemoData.CreateSession(); var baseline = Rules.Validate(session, DemoData.CreateProposal(session)); var row = baseline.Rows[0];
            var key = ReviewDurations.Key(row);
            foreach (var text in new[] { null!, "", " \r\n\t ", new string('x', 4001), "Bad\0text", "Bad\u0007text" })
                Reject(ReviewDescriptions.Apply(session, baseline, [new(key, row.Description, text)]), "1 to 4,000");
            Reject(ReviewDescriptions.Apply(session, baseline, [new(key, "Stale activity", "New activity")]), "original timecard");
            Reject(ReviewDescriptions.Apply(session, baseline, [new("unknown", row.Description, "New activity")]), "original timecard");
            Reject(ReviewDescriptions.Apply(session, baseline, [new(key, row.Description, "New activity"), new(key, row.Description, "Another activity")]), "original timecard");
            Reject(ReviewDescriptions.Apply(session, baseline, [null!]), "original timecard");
            var duplicateRows = baseline with { Rows = [row, row with { RowId = "other" }] };
            Reject(ReviewDescriptions.Apply(session, duplicateRows, [new(key, row.Description, "New activity")]), "original timecard");
        });
        test("description edits support exact limit and no-op but preserve generated labels", () =>
        {
            var session = DemoData.CreateSession(); var baseline = Rules.Validate(session, DemoData.CreateProposal(session));
            var row = baseline.Rows.First();
            var edited = ReviewDescriptions.Apply(session, baseline, [new(ReviewDurations.Key(row), row.Description, new string('x', 4000))]); Valid(edited);
            Equal(4000, edited.Rows.First().Description.Length); Equal(baseline.Rows.Sum(r => r.Hours), edited.Rows.Sum(r => r.Hours));
            var unchanged = ReviewDescriptions.Apply(session, baseline, [new(ReviewDurations.Key(row), row.Description, row.Description)]); Valid(unchanged);
            Equal(baseline.Warnings.Count, unchanged.Warnings.Count);
            foreach (var (kind, description) in new[] { ("timecards", "Timecards"), ("misc_internal", "Misc internal") })
            {
                var automatic = row with { Kind = kind, Description = description, SourceEntryIds = [], Hours = .17m };
                var generated = baseline with { Rows = [automatic] };
                var automaticEdit = ReviewDescriptions.Apply(session, generated, [new(ReviewDurations.Key(automatic), automatic.Description, "Reviewed internal activity")]);
                Reject(automaticEdit, "Generated Timecards and Misc internal labels cannot be renamed");
                Equal(description, automaticEdit.Rows.Single().Description); Equal(.17m, automaticEdit.Rows.Single().Hours);
                var noOp = ReviewDescriptions.Apply(session, generated, [new(ReviewDurations.Key(automatic), automatic.Description, automatic.Description)]); Valid(noOp);
                Equal(generated.Warnings.Count, noOp.Warnings.Count); Equal(description, noOp.Rows.Single().Description);
            }
        });
        test("description overlays keep deduplicated manual aliases included once", () =>
        {
            var session = DemoData.CreateSession(); var proposal = DemoData.CreateProposal(session); var source = session.Days[0].Entries[0];
            var entry = new ManualTimecard { Date = session.Days[0].Date, Project = 100, Assignment = 501, Task = 60, Hours = 4m, Description = source.Description };
            session = session with { ManualTimecards = [entry] };
            var baseline = Rules.Validate(session, proposal); Valid(baseline);
            var combined = baseline.Rows.Single(r => r.ManualEntryIds.Contains(entry.Id)); Require(combined.Kind == "work");
            var edits = new[] { new ReviewDescription(ReviewDurations.Key(combined), combined.Description, "Revised combined activity") };
            var changed = ReviewDescriptions.Apply(session, baseline, edits); Valid(changed);
            var recomputed = ReviewDescriptions.Apply(session, Rules.Validate(session, proposal), edits); Valid(recomputed);
            Equal(baseline.Rows.Count, recomputed.Rows.Count); Equal(baseline.Rows.Sum(r => r.Hours), recomputed.Rows.Sum(r => r.Hours));
            var actual = recomputed.Rows.Single(r => r.ManualEntryIds.Contains(entry.Id)); Equal("Revised combined activity", actual.Description);
            Require(actual.SourceEntryIds.Contains(source.Id)); Equal(source.Description, session.ManualTimecards.Single().Description);
            var changedSources = baseline with { Rows = baseline.Rows.Select(r => r == combined ? r with { ManualEntryIds = [] } : r).ToList() };
            Reject(ReviewDescriptions.Apply(session, changedSources, edits), "original timecard");
        });
    }
    private static void Valid(ValidationResult result) { if (!result.IsValid) throw new Exception(string.Join(" | ", result.Errors)); }
    private static void Reject(ValidationResult result, string fragment) => Require(!result.IsValid && result.Errors.Any(e => e.Contains(fragment)));
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Require(bool condition) { if (!condition) throw new Exception("Expected true"); }
}
