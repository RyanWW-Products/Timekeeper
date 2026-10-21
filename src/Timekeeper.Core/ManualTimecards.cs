using System.Text.RegularExpressions;

namespace Timekeeper.Core;

/// <summary>Locally entered work. This is never part of the agent proposal schema.</summary>
public sealed record ManualTimecard
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateOnly Date { get; init; }
    public decimal Hours { get; init; }
    public int Project { get; init; }
    public int? Assignment { get; init; }
    public int Task { get; init; }
    public string Description { get; init; } = "";
    public bool? BillableOverride { get; init; }
}

public static class ManualTimecards
{
    public const int MaximumEntries = 1000;
    public static List<string> ValidateDrafts(IEnumerable<ManualTimecard>? entries)
    {
        var errors = new List<string>();
        if (entries is null) return ["Manual timecards are missing."];
        var rows = entries.Take(MaximumEntries + 1).ToList();
        if (rows.Count > MaximumEntries) return [$"Manual timecards exceed the {MaximumEntries}-entry limit."];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null) { errors.Add("A manual timecard is missing."); continue; }
            if (!Guid.TryParseExact(row.Id, "N", out var id) || row.Id != id.ToString("N") || !ids.Add(row.Id))
                errors.Add("A manual timecard has an invalid or repeated ID. Create each manual entry separately.");
            if (row.Date == default) errors.Add("Choose a date for every manual timecard.");
            if (row.Hours is <= 0 or > 24 || decimal.Round(row.Hours, 2) != row.Hours)
                errors.Add("Manual hours must be greater than zero, at most 24, and have up to two decimal places.");
            if (row.Project <= 0 || row.Task <= 0 || row.Assignment is <= 0) errors.Add("Choose a valid project, task and optional assignment for manual time.");
            if (string.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 4000 || row.Description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                errors.Add("Manual descriptions must contain 1 to 4,000 characters and no unsupported control characters.");
        }
        if (errors.Count == 0)
            foreach (var day in rows.GroupBy(r => r.Date))
                if (day.Sum(r => r.Hours) > 24m) errors.Add($"{day.Key:yyyy-MM-dd}: manual time exceeds 24 hours.");
        return errors;
    }

    /// <summary>Append trusted local time before automatic additions. Never round manual hours.</summary>
    internal static void Append(ReadSession session, ValidationResult result)
    {
        result.Errors.AddRange(ValidateDrafts(session.ManualTimecards));
        if (!result.IsValid || session.ManualTimecards.Count == 0) return;
        result.Warnings.Add("Manual time: confirm the dates, decimal hours, project, task and billing, and check that this work is not already covered by imported or existing time.");
        var reference = session.Reference;
        var imported = result.Rows.ToList();
        var claimedImported = new HashSet<string>(StringComparer.Ordinal);
        var claimedExisting = new HashSet<int>();
        foreach (var entry in session.ManualTimecards)
        {
            var before = result.Errors.Count;
            var day = session.Days.SingleOrDefault(d => d.Date == entry.Date);
            if (day is null) result.Errors.Add($"Manual time on {entry.Date:yyyy-MM-dd} has no Quickbase baseline. Read that date before submitting.");
            var task = reference.Tasks.SingleOrDefault(t => t.Id == entry.Task);
            var category = task is null ? null : reference.Categories.SingleOrDefault(c => c.Id == task.CategoryId);
            if (task is null || category is null) result.Errors.Add($"Manual task {entry.Task} or its category is unavailable. Choose a current Quickbase task.");
            var assignment = entry.Assignment is int id ? reference.Assignments.SingleOrDefault(a => a.Id == id) : null;
            string? projectName;
            if (entry.Assignment.HasValue)
            {
                if (assignment is null || assignment.ProjectId != entry.Project) result.Errors.Add($"Manual assignment {entry.Assignment} does not belong to project {entry.Project}.");
                projectName = assignment?.ProjectName;
                if (assignment is not null)
                {
                    if (TimecardApi.IsClosed(assignment.Status)) result.Warnings.Add($"Manual assignment #{assignment.Id} has status {assignment.Status}. Confirm it is appropriate for this work.");
                    if (assignment.Employees.Count > 0 && !assignment.Employees.Contains(session.Settings.Email, StringComparer.OrdinalIgnoreCase))
                        result.Warnings.Add($"Manual assignment #{assignment.Id} does not list {session.Settings.Email}. Confirm it is appropriate for you.");
                }
            }
            else
            {
                projectName = entry.Project == reference.InternalProject?.Id ? reference.InternalProject.Name : reference.Projects.SingleOrDefault(p => p.Id == entry.Project)?.Name;
                if (projectName is null) result.Errors.Add($"Manual project {entry.Project} is not in the trusted project choices. Find and select the project first.");
            }
            if (result.Errors.Count != before) continue;
            var row = new VerifiedRow
            {
                Date = entry.Date, Hours = entry.Hours, Project = entry.Project, ProjectName = projectName!,
                Assignment = entry.Assignment, AssignmentName = assignment?.Name ?? (entry.Project == reference.InternalProject?.Id ? "Internal" : "Direct to project"),
                Task = task!.Id, TaskName = task.Name, Category = category!.Id, CategoryName = category.Name,
                Description = entry.Description.Trim(), Kind = "manual", BillableOverride = entry.BillableOverride, ManualEntryIds = [entry.Id]
            };
            var exactImported = imported.Where(r => SameWork(row, r) && CompatibleBilling(row.BillableOverride, r.BillableOverride, reference.Billing?.DefaultFor(row.Project, row.Task))).ToList();
            if (exactImported.Count == 1 && claimedImported.Add(exactImported[0].RowId))
            {
                var index = result.Rows.FindIndex(r => r.RowId == exactImported[0].RowId);
                result.Rows[index] = result.Rows[index] with { ManualEntryIds = result.Rows[index].ManualEntryIds.Append(entry.Id).Order(StringComparer.Ordinal).ToList() };
                result.Warnings.Add($"{entry.Date:yyyy-MM-dd}: manual entry {entry.Id[..8]} exactly matches imported work ({entry.Hours:0.00} h, {task.Name}). It is included once, with both source references retained.");
                continue;
            }
            var effective = row.BillableOverride ?? reference.Billing?.DefaultFor(row.Project, row.Task);
            var exactExisting = day!.Existing.Where(c => SameWork(row, c) && effective.HasValue && c.Billable == effective).ToList();
            if (exactExisting.Count == 1 && claimedExisting.Add(exactExisting[0].RecordId))
            {
                result.AlreadyRecordedManualEntryIds.Add(entry.Id);
                result.Warnings.Add($"{entry.Date:yyyy-MM-dd}: manual entry {entry.Id[..8]} exactly matches Quickbase record #{exactExisting[0].RecordId} ({entry.Hours:0.00} h, {task.Name}). It is already recorded and will not be sent again.");
                continue;
            }
            if (result.Rows.Any(r => Similar(row, r)) || day.Existing.Any(c => c.Project == row.Project && c.Assignment == row.Assignment && c.Task == row.Task))
                result.Warnings.Add($"Possible overlap: manual entry {entry.Id[..8]} on {entry.Date:yyyy-MM-dd} shares a project, assignment and task with other imported, manual or existing time. Hours, description or billing do not prove a unique duplicate. This {entry.Hours:0.00} h entry remains included; remove it if already covered.");
            result.Rows.Add(row);
        }
    }

    private static bool CompatibleBilling(bool? first, bool? second, bool? defaultValue) => first == second || (first ?? defaultValue) is bool a && (second ?? defaultValue) is bool b && a == b;
    private static string Description(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
    private static bool Similar(VerifiedRow a, VerifiedRow b) => a.Date == b.Date && a.Project == b.Project && a.Assignment == b.Assignment && a.Task == b.Task;
    private static bool SameWork(VerifiedRow a, VerifiedRow b) => Similar(a, b) && a.Category == b.Category && a.Hours == b.Hours && string.Equals(Description(a.Description), Description(b.Description), StringComparison.OrdinalIgnoreCase);
    private static bool SameWork(VerifiedRow a, ExistingTimecard b) => a.Date == b.Date && a.Project == b.Project && a.Assignment == b.Assignment && a.Task == b.Task && a.Category == b.Category && a.Hours == b.Hours && string.Equals(Description(a.Description), Description(b.Description), StringComparison.OrdinalIgnoreCase);
}
