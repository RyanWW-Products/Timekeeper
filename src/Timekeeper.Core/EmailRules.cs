using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

/// <summary>Validates confirmed email activities against current Quickbase references without invented timers.</summary>
public static class EmailRules
{
    public static string EvidenceKey(string employeeEmail, string immutableEvidenceId) =>
        "email:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(employeeEmail.Trim().ToLowerInvariant() + "\n" + immutableEvidenceId.Trim())));

    public static decimal RoundedHours(long minutes, int roundingMinutes)
    {
        if (minutes is < 0 or > 1440 || roundingMinutes is not (0 or 5 or 15)) throw new ArgumentOutOfRangeException(nameof(minutes));
        var rounded = roundingMinutes == 0 ? minutes : ((minutes + roundingMinutes - 1) / roundingMinutes) * roundingMinutes;
        return decimal.Round(rounded / 60m, 2, MidpointRounding.AwayFromZero);
    }

    public static ValidationResult Validate(ReadSession session, ProposalEnvelope proposal, DateTimeOffset? now = null, EmailReviewEdits? emailEdits = null)
    {
        var result = new ValidationResult();
        var errors = result.Errors;
        var warnings = result.Warnings;
        if (session?.EmailWorkbook is null || session.Settings is null || session.Reference is null || session.Days is null || proposal is null || proposal.Rows is null || proposal.AlreadyRecorded is null || proposal.EmailAlreadyRecorded is null)
        { errors.Add("The email workbook, source session and proposal are required."); return result; }
        errors.AddRange(Rules.ValidateSettings(session.Settings));
        errors.AddRange(ManualTimecards.ValidateDrafts(session.ManualTimecards));
        if (session.ManualOnly) errors.Add("A manual-only session cannot contain an email workbook.");
        errors.AddRange(EmailWorkbookReader.ValidateForReview(session.EmailWorkbook, session.Settings, now));
        // Every email import, including measured time without other warnings, needs
        // the final review checkbox. Suggestions and import never imply approval.
        warnings.Add("Email review: confirm these activities are yours, check the suggested assignments and tasks, and verify the hours and billing before writing. Best guesses may need correction.");
        if (session.EmailWorkbook.IsReviewWorkbook) warnings.Add(EmailWorkbookReader.ReviewLimitations);
        if (session.EmailWorkbook.IsMinimalImport) warnings.Add(EmailWorkbookReader.MinimalImportLimitations);
        if (session.SchemaVersion != 1 || proposal.SchemaVersion != 1) errors.Add("Unsupported email session or proposal schema version.");
        if (string.IsNullOrWhiteSpace(session.SessionId) || proposal.SessionId != session.SessionId) errors.Add("This proposal belongs to a different import session. Import the workbook again.");
        if (proposal.EmployeeId != session.Settings.EmployeeId) errors.Add("The proposal employee ID does not match the verified account.");
        var age = (now ?? DateTimeOffset.UtcNow) - session.GeneratedAtUtc;
        if (age > TimeSpan.FromHours(24)) errors.Add("Quickbase reference data is more than 24 hours old. Import the workbook again before submitting.");
        if (age < TimeSpan.FromMinutes(-5)) errors.Add("The import timestamp is in the future. Check your computer clock.");
        if (session.Settings.EmailRoundingMinutes is not (0 or 5 or 15)) errors.Add("Email rounding must be 0, 5 or 15 minutes.");
        if (proposal.AlreadyRecorded.Count > 0) errors.Add("Toggl already-recorded links cannot be used for an email workbook.");
        if (session.Days.Count is 0 or > 31 || proposal.Rows.Count > EmailWorkbookReader.MaximumActivities || proposal.EmailAlreadyRecorded.Count > EmailWorkbookReader.MaximumActivities) errors.Add("The email session or proposal exceeds the supported size.");
        if (session.Reference.Tasks is null || session.Reference.Categories is null || session.Reference.Assignments is null || session.Reference.Projects is null) errors.Add("Quickbase reference data is incomplete.");
        if (errors.Count > 0) return result;

        var reference = session.Reference;
        var days = new Dictionary<DateOnly, DaySnapshot>();
        var existing = new Dictionary<int, ExistingTimecard>();
        var tasks = new Dictionary<int, TaskRecord>();
        var categories = new Dictionary<int, CategoryRecord>();
        var assignments = new Dictionary<int, AssignmentRecord>();
        var projects = new Dictionary<int, ProjectRecord>();
        foreach (var item in reference.Projects!)
            if (item is null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || !projects.TryAdd(item.Id, item)) errors.Add("Project reference data contains an invalid or duplicate project.");
        foreach (var item in reference.Tasks!)
            if (item is null || item.Id <= 0 || item.CategoryId <= 0 || string.IsNullOrWhiteSpace(item.Name) || !tasks.TryAdd(item.Id, item)) errors.Add("Task reference data contains an invalid or duplicate task.");
        foreach (var item in reference.Categories!)
            if (item is null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || !categories.TryAdd(item.Id, item)) errors.Add("Category reference data contains an invalid or duplicate category.");
        foreach (var item in reference.Assignments!)
            if (item is null || item.Id <= 0 || item.ProjectId <= 0 || item.Employees is null || string.IsNullOrWhiteSpace(item.Name) || !assignments.TryAdd(item.Id, item)) errors.Add("Assignment reference data contains an invalid or duplicate assignment.");
        foreach (var item in tasks.Values)
            if (!categories.ContainsKey(item.CategoryId)) errors.Add($"Task {item.Id} refers to an unavailable category.");
        if (reference.InternalProject is not { Id: > 0 } || string.IsNullOrWhiteSpace(reference.InternalProject.Name)) errors.Add("The internal project could not be resolved from Quickbase.");
        if (session.Settings.InternalProjectId > 0 && session.Settings.InternalProjectId != reference.InternalProject?.Id) errors.Add("The internal project does not match your settings.");
        foreach (var day in session.Days)
        {
            if (day is null || day.Date == default || day.Entries is null || day.Existing is null || !days.TryAdd(day.Date, day)) { errors.Add("The source days contain an invalid or duplicate date."); continue; }
            if (day.Entries.Count != 0) errors.Add("An email import cannot also contain Toggl timers.");
            // Extra Quickbase-only baseline dates can remain after a carried manual entry
            // is removed. Workbook activity dates are checked against its own period.
            if (day.Existing.Count > 10_000) { errors.Add("A source day has too many Quickbase records."); continue; }
            foreach (var card in day.Existing)
            {
                if (card is null || card.RecordId <= 0 || !existing.TryAdd(card.RecordId, card)) { errors.Add("Quickbase source data contains an invalid or duplicate record ID."); continue; }
                if (card.Date != day.Date || card.Hours is <= 0 or > 24 || decimal.Round(card.Hours, 2) != card.Hours) errors.Add($"Quickbase record {card.RecordId} has invalid hours or date.");
            }
        }
        if (errors.Count > 0) return result;
        var activities = session.EmailWorkbook.Activities.Where(a => days.ContainsKey(a.Date)).ToDictionary(a => a.ActivityId, StringComparer.Ordinal);
        var policy = emailEdits ?? EmailReviewEdits.FromSettings(session.Settings);
        var hourEdits = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (policy.TargetHours is < 0 or > 24 || decimal.Round(policy.TargetHours, 2) != policy.TargetHours)
            errors.Add("The weekday fill target must be between 0 and 24 hours, with up to two decimal places.");
        if (policy.ActivityHours is null || policy.ActivityHours.Count > EmailWorkbookReader.MaximumActivities)
            errors.Add("Email duration edits are missing or exceed the supported size.");
        else foreach (var edit in policy.ActivityHours)
        {
            var source = edit is null ? null : session.EmailWorkbook.Activities.SingleOrDefault(a => a.ActivityId == edit.ActivityId);
            if (edit is null || source is null || source.Minutes != edit.OriginalMinutes || !hourEdits.TryAdd(edit.ActivityId, edit.Hours))
            { errors.Add("An email duration edit no longer matches its original activity. Import and review again."); continue; }
            if (edit.Hours is <= 0 or > 24 || decimal.Round(edit.Hours, 2) != edit.Hours)
                errors.Add("Edited email hours must be greater than zero, at most 24, and have up to two decimal places.");
            if (!activities.ContainsKey(edit.ActivityId)) continue; // A date selection may exclude a valid original edit.
            if (proposal.EmailAlreadyRecorded.Any(link => link?.ActivityId == edit.ActivityId)) errors.Add("An already-recorded activity cannot also have edited hours. Return it to new work to change its hours.");
            warnings.Add($"{source.Date:yyyy-MM-dd} · {source.Description}: email hours changed from {source.Minutes / 60m:0.00} to {edit.Hours:0.00}. Edited hours are exact; unedited activities use your rounding preference. Weekday fill is recalculated.");
        }
        if (errors.Count > 0) return result;
        foreach (var day in activities.Values.GroupBy(a => a.Date))
            if (day.Sum(a => hourEdits.GetValueOrDefault(a.ActivityId, a.Minutes / 60m)) > 24m) errors.Add($"{day.Key:yyyy-MM-dd}: edited email activity exceeds 24 hours.");
        if (errors.Count > 0) return result;
        if (emailEdits is not null && (policy.FillWeekdays || policy.AddTimecards))
            warnings.Add($"Import additions: weekday fill {(policy.FillWeekdays ? $"to {policy.TargetHours:0.00} hours" : "off")}; Timecards {(policy.AddTimecards ? $"{session.Settings.TimecardsHours:0.00} hours" : "off")}. Existing Quickbase time counts toward the target; work above it is retained.");
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var linkedMinutes = new Dictionary<int, long>();
        foreach (var link in proposal.EmailAlreadyRecorded)
        {
            if (link is null || !activities.TryGetValue(link.ActivityId ?? "", out var activity)) { errors.Add("An already-recorded activity is not on the selected dates."); continue; }
            if (!covered.Add(activity.ActivityId)) errors.Add($"Activity '{activity.ActivityId}' is covered more than once.");
            if (!existing.TryGetValue(link.ExistingRecordId, out var card)) { errors.Add($"Quickbase record {link.ExistingRecordId} is not in this import."); continue; }
            if (card.Date != activity.Date) errors.Add($"Activity '{activity.ActivityId}' and Quickbase record {card.RecordId} have different dates.");
            linkedMinutes[card.RecordId] = linkedMinutes.GetValueOrDefault(card.RecordId) + activity.Minutes;
            warnings.Add($"Confirm already recorded: email activity '{activity.ActivityId}' ({activity.Minutes} minutes) is represented by Quickbase record #{card.RecordId} ({card.Hours:0.00} hours). This activity will not be sent again.");
        }
        foreach (var (recordId, minutes) in linkedMinutes)
            // Linking existing work does not rebill it under today's rounding preference.
            if (minutes > 1440 || RoundedHours(minutes, 0) > existing[recordId].Hours) errors.Add($"Activities linked to Quickbase record {recordId} exceed its recorded hours.");

        var groups = new Dictionary<(DateOnly Date, int Project, int? Assignment, int Task, bool? Billable), Group>();
        foreach (var row in proposal.Rows)
        {
            if (row is null || row.SourceActivityIds is null || row.SourceEntryIds is null) { errors.Add("An email proposal row or source activity list is missing."); continue; }
            var before = errors.Count;
            if (row.SourceEntryIds.Count > 0) errors.Add("Email proposal rows cannot contain Toggl source IDs.");
            if (!days.ContainsKey(row.Date)) errors.Add($"Proposal date {row.Date:yyyy-MM-dd} is not among the selected dates.");
            if (row.SourceActivityIds.Count is 0 or > EmailWorkbookReader.MaximumActivities) errors.Add("Every email work row must identify its source activities.");
            if (string.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 4000 || row.Description.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) errors.Add("Every email work row needs a plain description of at most 4,000 characters.");
            if (!tasks.TryGetValue(row.Task, out var task)) errors.Add($"Task {row.Task} is not in the trusted Quickbase reference data.");
            if (!categories.ContainsKey(row.Category) || task?.CategoryId != row.Category) errors.Add($"Task {row.Task} does not belong to category {row.Category}.");
            if (row.Assignment is int assignmentId)
            {
                if (!assignments.TryGetValue(assignmentId, out var assignment) || assignment.ProjectId != row.Project) errors.Add($"Assignment {assignmentId} does not belong to project {row.Project}.");
                else
                {
                    if (Regex.IsMatch(assignment.Status ?? "", @"\b(closed|complete|completed|cancelled|canceled|inactive|archived)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) warnings.Add($"Assignment {assignment.Id} has status '{assignment.Status}'. Confirm it is appropriate for this work.");
                    if (assignment.Employees.Count > 0 && !assignment.Employees.Any(e => string.Equals(e?.Trim(), session.Settings.Email.Trim(), StringComparison.OrdinalIgnoreCase))) warnings.Add($"Assignment {assignment.Id} does not list {session.Settings.Email}. Confirm it is appropriate for you.");
                }
            }
            else if (row.Project != reference.InternalProject!.Id && !projects.ContainsKey(row.Project)) errors.Add($"Project {row.Project} is not in the trusted project choices. Find and select the project in Timekeeper before applying time directly to it.");
            long minutes = 0;
            var source = new List<EmailActivity>();
            foreach (var id in row.SourceActivityIds)
            {
                if (!activities.TryGetValue(id ?? "", out var activity)) { errors.Add($"Email activity '{id}' is unknown or outside the selected dates."); continue; }
                if (!covered.Add(activity.ActivityId)) errors.Add($"Activity '{activity.ActivityId}' is covered more than once.");
                if (activity.Date != row.Date) errors.Add($"Activity '{activity.ActivityId}' belongs to {activity.Date:yyyy-MM-dd}, not {row.Date:yyyy-MM-dd}.");
                minutes += activity.Minutes;
                source.Add(activity);
            }
            if (minutes > 1440) errors.Add($"{row.Date:yyyy-MM-dd}: mapped activity exceeds 24 hours.");
            else if (row.Hours.HasValue && row.Hours != RoundedHours(minutes, session.Settings.EmailRoundingMinutes)) errors.Add("Supplied hours do not match the locally calculated email activity hours. Omit hours from the proposal.");
            if (errors.Count != before) continue;
            var key = (row.Date, row.Project, row.Assignment, row.Task, row.BillableOverride);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new Group(row);
            group.Activities.AddRange(source);
            if (!group.Descriptions.Contains(row.Description.Trim(), StringComparer.Ordinal)) group.Descriptions.Add(row.Description.Trim());
        }
        foreach (var activity in activities.Values)
            if (!covered.Contains(activity.ActivityId)) errors.Add($"Email activity '{activity.ActivityId}' on {activity.Date:yyyy-MM-dd} is missing. Map every activity on the selected dates or link it to an existing Quickbase record.");
        if (errors.Count > 0) return result;
        foreach (var group in groups.Values)
        {
            var row = group.Row;
            var minutes = group.Activities.Sum(a => (long)a.Minutes);
            if (minutes > 1440) { errors.Add("Grouped email activity exceeds 24 hours."); continue; }
            var description = string.Join("; ", group.Descriptions);
            if (description.Length > 4000) { errors.Add($"Grouped description for task {row.Task} exceeds 4,000 characters."); continue; }
            var assignment = row.Assignment is int id ? assignments[id] : null;
            var originalHours = RoundedHours(minutes, session.Settings.EmailRoundingMinutes);
            var hours = RoundedHours(group.Activities.Where(a => !hourEdits.ContainsKey(a.ActivityId)).Sum(a => (long)a.Minutes), session.Settings.EmailRoundingMinutes)
                + group.Activities.Where(a => hourEdits.ContainsKey(a.ActivityId)).Sum(a => hourEdits[a.ActivityId]);
            result.Rows.Add(new VerifiedRow
            {
                Date = row.Date, Hours = hours, OriginalHours = hours != originalHours ? originalHours : null, Project = row.Project, ProjectName = assignment?.ProjectName ?? (row.Project == reference.InternalProject!.Id ? reference.InternalProject.Name : projects[row.Project].Name),
                Assignment = row.Assignment, AssignmentName = assignment?.Name ?? (row.Project == reference.InternalProject!.Id ? "Internal" : "Direct to project"), Task = row.Task, TaskName = tasks[row.Task].Name, Category = row.Category, CategoryName = categories[row.Category].Name,
                Description = description, Kind = "work", BillableOverride = row.BillableOverride,
                SourceActivityIds = group.Activities.Select(a => a.ActivityId).Order(StringComparer.Ordinal).ToList(),
                EmailEvidenceKeys = group.Activities.SelectMany(a => a.EvidenceIds).Select(id => EvidenceKey(session.EmailWorkbook.Metadata.EmployeeEmail, id)).Order(StringComparer.Ordinal).ToList()
            });
        }
        ManualTimecards.Append(session, result);
        if (!result.IsValid) return result;
        foreach (var day in days.Values.OrderBy(d => d.Date))
        {
            var source = activities.Values.Where(a => a.Date == day.Date).ToList();
            var work = result.Rows.Where(r => r.Date == day.Date).ToList();
            if (source.Any(a => a.TimeBasis == "employee_confirmed_estimate")) warnings.Add($"{day.Date:yyyy-MM-dd}: email activity includes employee-confirmed estimated time. Confirm the allocations represent work performed and do not overlap meetings, timers or other timecards.");
            if (day.Existing.Count > 0 && work.Count > 0) warnings.Add($"{day.Date:yyyy-MM-dd}: Quickbase already contains {day.Existing.Sum(c => c.Hours):0.00} hours. Confirm the proposed email work is additional. Link already-covered activities to existing records instead of adding them again.");
            if ((policy.FillWeekdays || policy.AddTimecards) && (source.Count > 0 || session.ManualTimecards.Any(m => m.Date == day.Date)) && day.Date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var total = day.Existing.Sum(c => c.Hours) + work.Sum(r => r.Hours);
                bool Timecards(string text) => string.Equals(text.Trim(), "Timecards", StringComparison.OrdinalIgnoreCase);
                var hasTimecards = day.Existing.Any(c => c.Project == reference.InternalProject!.Id && Timecards(c.Description))
                    || work.Any(r => r.Project == reference.InternalProject!.Id && (Timecards(r.Description) || r.SourceActivityIds.Any(id => Timecards(activities[id].Description))))
                    || proposal.EmailAlreadyRecorded.Any(link => activities[link.ActivityId].Date == day.Date && Timecards(activities[link.ActivityId].Description) && existing[link.ExistingRecordId].Project == reference.InternalProject!.Id);
                if (policy.AddTimecards && session.Settings.TimecardsHours > 0 && !hasTimecards)
                {
                    AddAutomatic(day.Date, "Timecards", "timecards", session.Settings.TimecardsHours);
                    total += session.Settings.TimecardsHours;
                }
                var gap = policy.FillWeekdays ? Math.Max(0m, policy.TargetHours - total) : 0m;
                if (gap > 0)
                {
                    AddAutomatic(day.Date, "Misc internal", "misc_internal", gap);
                    if (gap > 3m) warnings.Add($"{day.Date:yyyy-MM-dd}: filling {gap:0.00} hours as Misc internal. Confirm this large gap is intended before writing.");
                }
            }
            if (day.Existing.Sum(c => c.Hours) + result.Rows.Where(r => r.Date == day.Date).Sum(r => r.Hours) > 24m) errors.Add($"{day.Date:yyyy-MM-dd}: existing and proposed time exceeds 24 hours.");
        }
        foreach (var note in session.EmailWorkbook.ReviewNotes.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()) warnings.Add("Workbook review note: " + note);
        foreach (var activity in activities.Values.Where(a => !string.IsNullOrWhiteSpace(a.ReviewNotes)))
            warnings.Add($"{activity.Date:yyyy-MM-dd} · {activity.MatterHint}: {activity.ReviewNotes}");
        if (!string.IsNullOrWhiteSpace(session.EmailWorkbook.Metadata.RetrievalLimitations) && !string.Equals(session.EmailWorkbook.Metadata.RetrievalLimitations.Trim(), "none", StringComparison.OrdinalIgnoreCase)) warnings.Add("Email retrieval limitations: " + session.EmailWorkbook.Metadata.RetrievalLimitations.Trim());
        return result;

        void AddAutomatic(DateOnly date, string description, string kind, decimal hours)
        {
            if (!tasks.TryGetValue(session.Settings.InternalTaskId, out var task)) { errors.Add("The task for automatic internal time is unavailable."); return; }
            result.Rows.Add(new VerifiedRow { Date = date, Hours = hours, Project = reference.InternalProject!.Id, ProjectName = reference.InternalProject.Name, AssignmentName = "Internal", Task = task.Id, TaskName = task.Name, Category = task.CategoryId, CategoryName = categories[task.CategoryId].Name, Description = description, Kind = kind });
        }
    }

    private sealed class Group(ProposalRow row)
    {
        public ProposalRow Row { get; } = row;
        public List<EmailActivity> Activities { get; } = [];
        public List<string> Descriptions { get; } = [];
    }
}
