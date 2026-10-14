using System.ComponentModel;
using System.Text.RegularExpressions;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed record EmailMappingChoice(int? Id, string Label, string? Detail = null)
{
    public string Tooltip => Detail ?? Label;
    public override string ToString() => Label;
}

internal sealed class EmailMappingRow : INotifyPropertyChanged
{
    private ReadSession _session;
    private readonly Action _changed;
    private bool _refreshing;
    private bool _assignmentEdited, _taskEdited;
    private string _suggestionReason;
    public EmailActivity Activity { get; }
    public int? Assignment { get; private set; }
    public bool Internal { get; private set; }
    public int? Task { get; private set; }
    public int? Existing { get; private set; }
    public IReadOnlyList<EmailMappingChoice> AssignmentChoices { get; private set; } = [];
    public IReadOnlyList<EmailMappingChoice> TaskChoices { get; private set; } = [];
    public IReadOnlyList<EmailMappingChoice> ExistingChoices { get; private set; } = [];
    public string DateAndMinutes => $"{Activity.Date:MMM d}\n{Activity.Minutes} min";
    public string ActivitySummary => Short(Activity.Description, 130);
    public string MatterSummary => Short(Activity.MatterHint, 70);
    public bool CanEditNew => !Existing.HasValue;
    public bool IsResolved => Existing.HasValue || ((Assignment.HasValue || Internal) && Task.HasValue);
    public string State => Existing.HasValue ? "Already recorded" : !IsResolved ? "Needs match" : _assignmentEdited || _taskEdited ? "Selected" : "Suggested";
    public string Reason => Existing.HasValue ? $"Linked to #{Existing.Value}. This activity will not be sent again."
        : !Assignment.HasValue && !Internal ? _suggestionReason + " Choose an assignment or internal project."
        : !Task.HasValue ? "Choose a task for this activity."
        : _assignmentEdited || _taskEdited ? "Check this selection in the final review." : _suggestionReason;

    public EmailMappingChoice? AssignmentSelection
    {
        get => AssignmentChoices.FirstOrDefault(c => c.Id == (Internal ? 0 : Assignment));
        set
        {
            if (_refreshing || value is null || value.Id == (Internal ? 0 : Assignment)) return;
            Internal = value.Id == 0;
            Assignment = value.Id > 0 ? value.Id : null;
            _assignmentEdited = true;
            if (!_taskEdited) Task = EmailSuggestions.SuggestTask(Activity, _session.Reference, Assignment, Internal, _session.Settings.InternalTaskId);
            Changed();
        }
    }
    public EmailMappingChoice? TaskSelection
    {
        get => TaskChoices.FirstOrDefault(c => c.Id == Task);
        set
        {
            if (_refreshing || value is null || value.Id == Task) return;
            Task = value.Id;
            _taskEdited = true;
            Changed();
        }
    }
    public EmailMappingChoice? ExistingSelection
    {
        get => ExistingChoices.FirstOrDefault(c => c.Id == Existing);
        set
        {
            if (_refreshing || value is null || value.Id == Existing) return;
            Existing = value.Id;
            Changed();
        }
    }

    public EmailMappingRow(EmailActivity activity, ReadSession session, Action changed)
    {
        Activity = activity; _session = session; _changed = changed;
        var suggestion = EmailSuggestions.Suggest(activity, session);
        Assignment = suggestion.AssignmentId; Internal = suggestion.Internal; Task = suggestion.TaskId;
        _suggestionReason = suggestion.Reason;
        RefreshChoices(session);
    }

    public void RefreshChoices(ReadSession session)
    {
        _session = session;
        _refreshing = true;
        try
        {
            AssignmentChoices = new[] { new EmailMappingChoice(null, "Choose assignment / project"), new EmailMappingChoice(0, $"Internal · {Short(session.Reference.InternalProject!.Name, 60)}") }
                .Concat(session.Reference.Assignments.OrderBy(a => a.ProjectName).ThenBy(a => a.Name)
                    .Select(a => new EmailMappingChoice(a.Id, $"{a.ProjectName} · {a.Name} (#{a.Id})"))).ToList();
            TaskChoices = new[] { new EmailMappingChoice(null, "Choose task") }.Concat(session.Reference.Tasks.OrderBy(t => t.Name)
                .Select(t => new EmailMappingChoice(t.Id, $"{t.Name} (#{t.Id})"))).ToList();
            ExistingChoices = new[] { new EmailMappingChoice(null, "New work") }.Concat(session.Days.Single(d => d.Date == Activity.Date).Existing
                .OrderBy(r => r.RecordId).Select(r =>
                {
                    var assignment = session.Reference.Assignments.FirstOrDefault(a => a.Id == r.Assignment);
                    var project = assignment?.ProjectName ?? (r.Project == session.Reference.InternalProject!.Id ? session.Reference.InternalProject.Name : $"Project #{r.Project}");
                    var task = session.Reference.Tasks.FirstOrDefault(t => t.Id == r.Task)?.Name ?? $"Task #{r.Task}";
                    return new EmailMappingChoice(r.RecordId, $"#{r.RecordId} · {r.Hours:0.00} h · {Short(r.Description, 44)}",
                        $"#{r.RecordId} · {r.Hours:0.00} h\n{project}\n{assignment?.Name ?? (r.Assignment is int id ? $"Assignment #{id}" : "Internal")} · {task}\n\n{r.Description}");
                })).ToList();
            foreach (var property in new[] { nameof(AssignmentChoices), nameof(TaskChoices), nameof(ExistingChoices), nameof(AssignmentSelection), nameof(TaskSelection), nameof(ExistingSelection) })
                PropertyChanged?.Invoke(this, new(property));
        }
        finally { _refreshing = false; }
    }

    internal sealed record Selection(int? Assignment, bool Internal, int? Task, int? Existing, bool AssignmentEdited, bool TaskEdited, string SuggestionReason);
    internal Selection Capture() => new(Assignment, Internal, Task, Existing, _assignmentEdited, _taskEdited, _suggestionReason);
    internal void Restore(Selection value)
    {
        Assignment = value.Assignment; Internal = value.Internal; Task = value.Task; Existing = value.Existing;
        _assignmentEdited = value.AssignmentEdited; _taskEdited = value.TaskEdited; _suggestionReason = value.SuggestionReason;
        Changed();
    }
    private void Changed()
    {
        foreach (var property in new[] { nameof(AssignmentSelection), nameof(TaskSelection), nameof(ExistingSelection), nameof(CanEditNew), nameof(IsResolved), nameof(State), nameof(Reason) })
            PropertyChanged?.Invoke(this, new(property));
        _changed();
    }
    private static string Short(string value, int maximum)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim();
        return text.Length <= maximum ? text : text[..(maximum - 1)] + "…";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
