using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

/// <summary>All billing arithmetic and trust decisions happen locally, never in Copilot.</summary>
public static class Rules
{
    private const int MaxItems = 10_000;

    public static List<string> ValidateSettings(AppSettings settings, bool allowMissingEmployeeId = false)
    {
        var errors = new List<string>();
        if (settings is null) return ["Settings are missing."];
        if (string.IsNullOrWhiteSpace(settings.Realm) ||
            !Regex.IsMatch(settings.Realm, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\.quickbase\.com\z"))
            errors.Add("Enter your Quickbase realm, such as company.quickbase.com, without a URL or path.");
        if (string.IsNullOrWhiteSpace(settings.Email) || !MailAddress.TryCreate(settings.Email, out var email) || email.Address != settings.Email)
            errors.Add("Enter a valid employee email address.");
        if ((!allowMissingEmployeeId && string.IsNullOrWhiteSpace(settings.EmployeeId)) ||
            (settings.EmployeeId is not null && (settings.EmployeeId.Length > 256 || settings.EmployeeId.Any(char.IsControl))))
            errors.Add("Test connections in Settings to detect your Quickbase user ID.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId ?? ""); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { errors.Add("Select a valid time zone."); }
        if (settings.TargetHours < 0 || settings.TargetHours > 24 || decimal.Round(settings.TargetHours, 2) != settings.TargetHours)
            errors.Add("The weekday target must be between 0 and 24 hours, with at most two decimal places.");
        if (settings.TimecardsHours < 0 || settings.TimecardsHours > 8 || decimal.Round(settings.TimecardsHours, 2) != settings.TimecardsHours)
            errors.Add("Timecards time must be between 0 and 8 hours, with at most two decimal places.");
        if (settings.InternalProjectId < 0 || (settings.InternalProjectId == 0 && string.IsNullOrWhiteSpace(settings.InternalProjectSearch)))
            errors.Add("Provide the internal project record ID or an exact internal project name.");
        if (settings.InternalTaskId <= 0) errors.Add("The task for automatic internal time must be a positive Quickbase task ID.");
        foreach (var table in new[] { settings.TimecardsTable, settings.TasksTable, settings.ProjectsTable, settings.AssignmentsTable, settings.CategoriesTable })
            if (string.IsNullOrWhiteSpace(table) || !Regex.IsMatch(table, @"\A[a-zA-Z0-9]{5,32}\z"))
            { errors.Add("All Quickbase table IDs must contain 5–32 letters or digits."); break; }
        if (!Uri.TryCreate(settings.CopilotUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            errors.Add("The Copilot link must be an HTTPS URL.");
        return errors;
    }

    /// <summary>Round a single positive Toggl duration up to the next five minutes, then to hundredths.</summary>
    public static decimal RoundSeconds(long seconds)
    {
        if (seconds < 0 || seconds > 86_400) throw new ArgumentOutOfRangeException(nameof(seconds), "Duration must be between zero and 24 hours.");
        return Hours(RoundedSeconds(seconds));
    }

    private static long RoundedSeconds(long seconds) => ((seconds + 299) / 300) * 300;
    private static decimal Hours(long seconds) => decimal.Round(seconds / 3600m, 2, MidpointRounding.AwayFromZero);
    private static bool Named(string? name, string expected) => string.Equals(name?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    public static ValidationResult Validate(ReadSession session, ProposalEnvelope proposal, DateTimeOffset? now = null)
    {
        var result = new ValidationResult();
        var errors = result.Errors;
        var warnings = result.Warnings;
        if (session is null || proposal is null) { errors.Add("The source session and returned proposal are required."); return result; }
        if (session.Settings is null || session.Reference is null || session.Days is null || proposal.Rows is null || proposal.AlreadyRecorded is null)
        { errors.Add("The source session or proposal contains a null collection or settings object."); return result; }
        errors.AddRange(ValidateSettings(session.Settings));
        if (session.SchemaVersion != 1 || proposal.SchemaVersion != 1) errors.Add("Unsupported schema version. Read the source again with this application.");
        if (string.IsNullOrWhiteSpace(session.SessionId) || proposal.SessionId != session.SessionId) errors.Add("This proposal belongs to a different read session. Use the latest exported file.");
        if (proposal.EmployeeId != session.Settings.EmployeeId) errors.Add("The proposal employee ID does not match this profile.");
        var age = (now ?? DateTimeOffset.UtcNow) - session.GeneratedAtUtc;
        if (age > TimeSpan.FromHours(24)) errors.Add("Source data is more than 24 hours old. Read the data again before submitting.");
        if (age < TimeSpan.FromMinutes(-5)) errors.Add("The source timestamp is in the future. Check the computer clock and read again.");
        if (session.Days.Count == 0) errors.Add("The source session contains no dates. Read the data again.");
        if (session.Days.Count > 31 || proposal.Rows.Count > MaxItems || proposal.AlreadyRecorded.Count > MaxItems)
            errors.Add("The session or proposal exceeds the supported size.");
        if (session.Reference.Tasks is null || session.Reference.Categories is null || session.Reference.Assignments is null)
            errors.Add("The reference data is incomplete.");
        if (errors.Count > 0) return result;

        var reference = session.Reference;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(session.Settings.TimeZoneId);
        var days = new Dictionary<DateOnly, DaySnapshot>();
        var entries = new Dictionary<long, (TimeEntry Entry, DateOnly Date)>();
        var existing = new Dictionary<int, ExistingTimecard>();
        var tasks = new Dictionary<int, TaskRecord>();
        var categories = new Dictionary<int, CategoryRecord>();
        var assignments = new Dictionary<int, AssignmentRecord>();
        foreach (var item in reference.Tasks!)
            if (item is null || item.Id <= 0 || item.CategoryId <= 0 || string.IsNullOrWhiteSpace(item.Name) || !tasks.TryAdd(item.Id, item)) errors.Add("Task reference data contains a missing, invalid or duplicate task.");
        foreach (var item in reference.Categories!)
            if (item is null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || !categories.TryAdd(item.Id, item)) errors.Add("Category reference data contains a missing, invalid or duplicate category.");
        foreach (var item in reference.Assignments!)
            if (item is null || item.Id <= 0 || item.ProjectId <= 0 || item.Employees is null || string.IsNullOrWhiteSpace(item.Name) || !assignments.TryAdd(item.Id, item)) errors.Add("Assignment reference data contains a missing, invalid or duplicate assignment.");
        foreach (var task in tasks.Values)
            if (!categories.ContainsKey(task.CategoryId)) errors.Add($"Task {task.Id} refers to an unavailable category.");
        if (reference.InternalProject is not { Id: > 0 } || string.IsNullOrWhiteSpace(reference.InternalProject.Name)) errors.Add("The internal project could not be resolved from Quickbase.");
        if (session.Settings.InternalProjectId > 0 && reference.InternalProject?.Id != session.Settings.InternalProjectId) errors.Add("The internal project does not match the configured record ID.");
        foreach (var day in session.Days)
        {
            if (day is null || day.Date == default || day.Entries is null || day.Existing is null || !days.TryAdd(day.Date, day))
            { errors.Add("Source days contain an invalid date, duplicate date or null collection."); continue; }
            if (day.Entries.Count > MaxItems || day.Existing.Count > MaxItems) { errors.Add($"{day.Date}: too many source records."); continue; }
            foreach (var entry in day.Entries)
            {
                if (entry is null || entry.Id <= 0 || !entries.TryAdd(entry.Id, (entry, day.Date))) { errors.Add($"{day.Date}: invalid or duplicate Toggl source ID."); continue; }
                if (entry.Running || entry.Stop is null || entry.DurationSeconds <= 0 || entry.DurationSeconds > 86_400)
                { errors.Add($"Toggl entry {entry.Id} is running or has an invalid duration. Correct it in Toggl and read again."); continue; }
                if (entry.Stop <= entry.Start || Math.Abs((entry.Stop.Value - entry.Start).TotalSeconds - entry.DurationSeconds) > 1)
                { errors.Add($"Toggl entry {entry.Id} has inconsistent start, stop and duration values. Correct it and read again."); continue; }
                var startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.Start, zone).DateTime);
                var endDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.Stop.Value.AddTicks(-1), zone).DateTime);
                if (startDate != day.Date || endDate != day.Date)
                    errors.Add($"Toggl entry {entry.Id} crosses a date boundary or is filed under the wrong date. Split or correct it in Toggl and read again.");
            }
            foreach (var card in day.Existing)
            {
                if (card is null || card.RecordId <= 0 || !existing.TryAdd(card.RecordId, card)) { errors.Add($"{day.Date}: invalid or duplicate existing Quickbase record ID."); continue; }
                if (card.Date != day.Date || card.Hours <= 0 || card.Hours > 24 || decimal.Round(card.Hours, 2) != card.Hours)
                    errors.Add($"Quickbase record {card.RecordId} has an invalid date or hours. Review it before submitting.");
            }
        }
        if (errors.Count > 0) return result;

        var covered = new HashSet<long>();
        var linkedSeconds = new Dictionary<int, long>();
        foreach (var link in proposal.AlreadyRecorded)
        {
            if (link is null) { errors.Add("An already-recorded link is null."); continue; }
            if (!entries.TryGetValue(link.SourceEntryId, out var source)) { errors.Add($"Already-recorded source ID {link.SourceEntryId} is unknown."); continue; }
            if (!covered.Add(link.SourceEntryId)) errors.Add($"Source entry {link.SourceEntryId} is covered more than once.");
            if (!existing.TryGetValue(link.ExistingRecordId, out var card)) { errors.Add($"Quickbase record {link.ExistingRecordId} is not in this read."); continue; }
            if (card.Date != source.Date) errors.Add($"Source entry {link.SourceEntryId} and Quickbase record {card.RecordId} have different dates.");
            linkedSeconds[card.RecordId] = linkedSeconds.GetValueOrDefault(card.RecordId) + RoundedSeconds(source.Entry.DurationSeconds);
            warnings.Add($"Confirm already recorded: Toggl {link.SourceEntryId} “{source.Entry.Description}” ({RoundSeconds(source.Entry.DurationSeconds):0.00} hours) is represented by Quickbase record {card.RecordId} “{card.Description}” ({card.Hours:0.00} hours). This source will not be written again.");
        }
        foreach (var (recordId, seconds) in linkedSeconds)
            if (Hours(seconds) > existing[recordId].Hours) errors.Add($"Sources linked to Quickbase record {recordId} exceed its recorded hours.");

        var groups = new Dictionary<(DateOnly Date, int Project, int? Assignment, int Task), WorkGroup>();
        foreach (var row in proposal.Rows)
        {
            if (row is null || row.SourceEntryIds is null) { errors.Add("A proposal row or source ID list is null."); continue; }
            var rowErrorCount = errors.Count;
            if (!days.ContainsKey(row.Date)) errors.Add($"Proposal date {row.Date} was not included in this read.");
            if (row.SourceEntryIds.Count == 0 || row.SourceEntryIds.Count > MaxItems) errors.Add("Every work row must identify its Toggl source entries. Copilot must not add automatic rows.");
            if (string.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 4000 || row.Description.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) errors.Add("Every work row needs a description of at most 4,000 characters without control characters.");
            if (!tasks.TryGetValue(row.Task, out var task)) errors.Add($"Task {row.Task} is not in the trusted reference data.");
            if (!categories.ContainsKey(row.Category) || task?.CategoryId != row.Category) errors.Add($"Task {row.Task} does not belong to category {row.Category}.");
            if (row.Assignment is int assignmentId)
            {
                if (!assignments.TryGetValue(assignmentId, out var assignment) || assignment.ProjectId != row.Project)
                    errors.Add($"Assignment {assignmentId} does not belong to project {row.Project}.");
                else
                {
                    if (Regex.IsMatch(assignment.Status ?? "", @"\b(closed|complete|completed|cancelled|canceled|inactive|archived)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        warnings.Add($"Assignment {assignment.Id} “{assignment.Name}” has status “{assignment.Status}”. Confirm it is appropriate for this work.");
                    if (assignment.Employees.Count > 0 && !assignment.Employees.Any(e => string.Equals(e?.Trim(), session.Settings.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
                        warnings.Add($"Assignment {assignment.Id} “{assignment.Name}” does not list {session.Settings.Email}. Confirm this assignment is appropriate for you.");
                }
            }
            else if (row.Project != reference.InternalProject!.Id)
                errors.Add($"Project {row.Project} requires an assignment. Only the configured internal project may omit it.");
            long seconds = 0;
            foreach (var id in row.SourceEntryIds)
            {
                if (!entries.TryGetValue(id, out var source)) { errors.Add($"Source entry {id} is unknown."); continue; }
                if (!covered.Add(id)) errors.Add($"Source entry {id} is covered more than once.");
                if (source.Date != row.Date) errors.Add($"Source entry {id} belongs to {source.Date}, not {row.Date}.");
                seconds += RoundedSeconds(source.Entry.DurationSeconds);
            }
            if (row.Hours is decimal supplied && (supplied <= 0 || supplied > 24 || supplied != Hours(seconds)))
                errors.Add($"Hours supplied for {row.Date}, task {row.Task} do not match the locally calculated {Hours(seconds):0.00} hours.");
            if (errors.Count != rowErrorCount) continue;
            var key = (row.Date, row.Project, row.Assignment, row.Task);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new WorkGroup(row);
            group.Seconds += seconds;
            group.Ids.AddRange(row.SourceEntryIds);
            if (!group.Descriptions.Contains(row.Description.Trim(), StringComparer.Ordinal)) group.Descriptions.Add(row.Description.Trim());
        }
        foreach (var id in entries.Keys)
            if (!covered.Contains(id)) errors.Add($"Source entry {id} on {entries[id].Date:yyyy-MM-dd} is missing. Every source on the selected dates must be mapped exactly once or linked to an existing Quickbase record.");
        if (errors.Count > 0) return result;

        foreach (var group in groups.Values)
        {
            var row = group.Row;
            var task = tasks[row.Task];
            var assignment = row.Assignment is int id ? assignments[id] : null;
            var description = string.Join("; ", group.Descriptions);
            if (description.Length > 4000) { errors.Add($"Grouped description for task {row.Task} exceeds 4,000 characters. Shorten the descriptions."); continue; }
            var verified = new VerifiedRow
            {
                Date = row.Date, Hours = Hours(group.Seconds), Project = row.Project,
                ProjectName = assignment?.ProjectName ?? reference.InternalProject!.Name,
                Assignment = row.Assignment, AssignmentName = assignment?.Name ?? "Internal",
                Task = row.Task, TaskName = task.Name, Category = task.CategoryId,
                CategoryName = categories[task.CategoryId].Name, Description = description,
                SourceEntryIds = group.Ids.Order().ToList(), Kind = "work"
            };
            if (verified.Hours <= 0 || verified.Hours > 24) errors.Add($"Grouped hours for {row.Date}, task {row.Task} must be greater than zero and no more than 24.");
            var duplicate = days[row.Date].Existing.FirstOrDefault(c => c.Project == row.Project && c.Task == row.Task && c.Assignment == row.Assignment && c.Hours == verified.Hours);
            if (duplicate is not null) warnings.Add($"Possible duplicate: {row.Date}, task {task.Name}, {verified.Hours:0.00} hours matches Quickbase record {duplicate.RecordId}. Confirm this is additional work. If the source is already represented by that record, correct the proposal to use an already_recorded link before writing.");
            result.Rows.Add(verified);
        }

        foreach (var day in days.Values.OrderBy(d => d.Date))
        {
            if (day.Entries.Count == 0 || day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var total = day.Existing.Sum(c => c.Hours) + result.Rows.Where(r => r.Date == day.Date).Sum(r => r.Hours);
            var timecardsAlreadyPresent = day.Existing.Any(c => c.Project == reference.InternalProject!.Id && Named(c.Description, "Timecards")) ||
                proposal.Rows.Any(r => r.Date == day.Date && r.Project == reference.InternalProject!.Id &&
                    (Named(r.Description, "Timecards") || r.SourceEntryIds.Any(id => Named(entries[id].Entry.Description, "Timecards")))) ||
                proposal.AlreadyRecorded.Any(link => entries[link.SourceEntryId].Date == day.Date &&
                    Named(entries[link.SourceEntryId].Entry.Description, "Timecards") && existing[link.ExistingRecordId].Project == reference.InternalProject!.Id);
            if (session.Settings.AddTimecards && session.Settings.TimecardsHours > 0 && !timecardsAlreadyPresent)
            {
                if (TryAuto("Timecards", "timecards", session.Settings.TimecardsHours, day.Date, out var added))
                { result.Rows.Add(added!); total += added!.Hours; }
            }
            var gap = session.Settings.FillWeekdays ? Math.Max(0m, session.Settings.TargetHours - total) : 0m;
            if (gap > 0 && TryAuto("Misc internal", "misc_internal", gap, day.Date, out var fill))
            {
                result.Rows.Add(fill!);
                if (gap > 3m) warnings.Add($"{day.Date}: filling {gap:0.00} hours as Misc internal. Confirm this large gap is intended before writing.");
            }
        }
        foreach (var day in days.Values)
            if (day.Existing.Sum(c => c.Hours) + result.Rows.Where(r => r.Date == day.Date).Sum(r => r.Hours) > 24m)
                errors.Add($"{day.Date}: existing and proposed time exceeds 24 hours. Review overlapping entries and existing records.");
        result.Rows.Sort((a, b) => { var date = a.Date.CompareTo(b.Date); return date != 0 ? date : KindOrder(a.Kind).CompareTo(KindOrder(b.Kind)); });
        return result;

        bool TryAuto(string name, string kind, decimal hours, DateOnly date, out VerifiedRow? row)
        {
            row = null;
            if (!tasks.TryGetValue(session.Settings.InternalTaskId, out var task)) { errors.Add($"Internal task {session.Settings.InternalTaskId} is unavailable for automatic {name} time. Correct the setting or disable this addition."); return false; }
            row = new VerifiedRow { Date = date, Hours = hours, Project = reference.InternalProject!.Id, ProjectName = reference.InternalProject.Name,
                AssignmentName = "Internal", Task = task.Id, TaskName = task.Name, Category = task.CategoryId, CategoryName = categories[task.CategoryId].Name,
                Description = name, Kind = kind };
            return true;
        }
    }

    private static int KindOrder(string kind) => kind == "work" ? 0 : kind == "timecards" ? 1 : 2;
    private sealed class WorkGroup(ProposalRow row)
    {
        public ProposalRow Row { get; } = row;
        public long Seconds { get; set; }
        public List<long> Ids { get; } = [];
        public List<string> Descriptions { get; } = [];
    }
}
