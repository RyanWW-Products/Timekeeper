using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed record EmailMappingChoice(int? Id, string Label, string? Detail = null, int? ProjectId = null)
{
    public string Tooltip => Detail ?? Label;
    public override string ToString() => Label;
}

internal sealed class EmailBillingChoice(string label) : INotifyPropertyChanged
{
    public string Label { get; private set; } = label;
    public void Update(string value)
    {
        if (Label == value) return;
        Label = value; PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
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
    public int? DirectProjectId { get; private set; }
    public bool Internal { get; private set; }
    public int? Task { get; private set; }
    public int? Existing { get; private set; }
    public IReadOnlyList<EmailMappingChoice> AssignmentChoices { get; private set; } = [];
    public IReadOnlyList<EmailMappingChoice> TaskChoices { get; private set; } = [];
    public IReadOnlyList<EmailMappingChoice> ExistingChoices { get; private set; } = [];
    public string WorkDate => Activity.Date.ToString("MMM d");
    public decimal? EditedHours { get; private set; }
    public decimal Hours => EditedHours ?? decimal.Round(Activity.Minutes / 60m, 2, MidpointRounding.AwayFromZero);
    public string HoursLabel => $"{Hours:0.00} h{(EditedHours.HasValue ? " *" : "")}";
    public string HoursHint => $"Imported: {Activity.Minutes / 60m:0.00} h. Double-click or press F2 to edit. Edited hours are exact; unedited activities are grouped and rounded in the submission preview.";
    public string? EditedDescription { get; private set; }
    public string Description => CanEditNew ? EditedDescription ?? Activity.Description : Activity.Description;
    public bool IsDescriptionEdited => CanEditNew && EditedDescription is not null;
    public string DescriptionHint => (CanEditNew
        ? (IsDescriptionEdited ? "Edited in Timekeeper. " : "") + "Double-click or press F2 to edit the description sent to Quickbase."
        : "Already recorded in Quickbase. This activity is linked, so its description will not be sent or changed.") + "\n\n" + Description;
    public bool? BillableOverride { get; private set; }
    public bool CanOverride => CanEditNew && _session.Reference.Billing?.CanOverride == true;
    public bool HasUnavailableOverride => CanEditNew && !CanOverride && BillableOverride.HasValue;
    public int ProjectId => Internal ? _session.Reference.InternalProject!.Id : DirectProjectId ?? _session.Reference.Assignments.FirstOrDefault(a => a.Id == Assignment)?.ProjectId ?? 0;
    private bool? EffectiveBillable => Existing is int id
        ? _session.Days.SelectMany(d => d.Existing).Single(r => r.RecordId == id).Billable
        : BillableOverride ?? _session.Reference.Billing?.DefaultFor(ProjectId, Task ?? 0);
    public string DefaultBillingLabel => _session.Reference.Billing?.DefaultFor(ProjectId, Task ?? 0) switch { true => "Billable - Default", false => "Nonbillable - Default", _ => "Default unavailable" };
    public string BillingHint => Existing.HasValue ? "Shows the recorded Quickbase billing. This activity will not be submitted again." : !IsResolved ? "Choose the project or assignment and task to resolve billing."
        : (_session.Reference.Billing?.DefaultUnavailableReason(ProjectId, Task ?? 0) ?? "Billing has not been checked. Refresh billing to read Quickbase again.") is { Length: > 0 } reason
        ? reason : CanOverride ? "Quickbase default is resolved. An override changes billing only; the project and task stay the same." : "Quickbase default is resolved. Your account cannot change the billing override.";
    public IReadOnlyList<EmailBillingChoice> BillingChoices { get; } = [new("Default unavailable"), new("Billable"), new("Nonbillable")];
    public string BillingLabel => Existing.HasValue ? EffectiveBillable switch { true => "Billable - Recorded", false => "Nonbillable - Recorded", _ => "Recorded billing unavailable" } : BillableOverride switch { true => "Billable", false => "Nonbillable", _ => DefaultBillingLabel };
    public Brush BillingLight => new SolidColorBrush(EffectiveBillable switch { true => Color.FromRgb(63,203,125), false => Color.FromRgb(139,75,78), _ => Color.FromRgb(125,133,143) });
    public int BillingIndex
    {
        get => BillableOverride is null ? 0 : BillableOverride.Value ? 1 : 2;
        set
        {
            if (!CanEditNew || value is < 0 or > 2 || value == BillingIndex || (!CanOverride && value != 0)) return;
            BillableOverride = value == 0 ? null : value == 1; Changed();
        }
    }
    public void SetHours(decimal? hours)
    {
        if (!CanEditNew) return;
        if (hours is decimal value && (value is <= 0 or > 24 || decimal.Round(value, 2) != value)) throw new InvalidOperationException("Enter decimal hours greater than 0 and at most 24, with up to two decimal places.");
        EditedHours = hours; Changed();
    }
    public void SetDescription(string description)
    {
        if (!CanEditNew) return;
        if (string.IsNullOrWhiteSpace(description) || description.Length > 4000 || description.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new InvalidOperationException("Enter a plain description of 1 to 4,000 characters.");
        var edited = description.Trim();
        EditedDescription = string.Equals(edited, Activity.Description, StringComparison.Ordinal) ? null : edited;
        Changed();
    }
    public string ActivitySummary => Short(Description, 130);
    public string MatterSummary => Short(Activity.MatterHint, 70);
    public bool CanEditNew => !Existing.HasValue;
    public bool IsResolved => Existing.HasValue || ((Assignment.HasValue || DirectProjectId.HasValue || Internal) && Task.HasValue);
    public string State => Existing.HasValue ? "Already recorded" : !IsResolved ? "Needs match" : _assignmentEdited || _taskEdited ? "Selected" : "Suggested";
    public string Reason => Existing.HasValue ? $"Linked to #{Existing.Value}. This activity will not be sent again."
        : !Assignment.HasValue && !DirectProjectId.HasValue && !Internal ? _suggestionReason + " Choose an assignment or use Direct to project."
        : !Task.HasValue ? "Choose a task for this activity."
        : DirectProjectId.HasValue ? "Direct to project. No assignment will be set. Check the task and billing."
        : _assignmentEdited || _taskEdited ? "Check this selection in the final review." : _suggestionReason;

    public EmailMappingChoice? AssignmentSelection
    {
        get => DirectProjectId is int id ? AssignmentChoices.FirstOrDefault(c => c.ProjectId == id) : AssignmentChoices.FirstOrDefault(c => c.ProjectId is null && c.Id == (Internal ? 0 : Assignment));
        set
        {
            if (_refreshing || value is null || (value.ProjectId == DirectProjectId && value.Id == (Internal ? 0 : Assignment))) return;
            DirectProjectId = value.ProjectId;
            Internal = value.ProjectId is null && value.Id == 0;
            Assignment = value.ProjectId is null && value.Id > 0 ? value.Id : null;
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
        Activity = activity; _session = session; _changed = changed; BillableOverride = activity.Billable;
        var suggestion = EmailSuggestions.Suggest(activity, session);
        Assignment = suggestion.AssignmentId; DirectProjectId = suggestion.ProjectId; Internal = suggestion.Internal; Task = suggestion.TaskId;
        _suggestionReason = suggestion.Reason;
        RefreshChoices(session);
    }

    public void RefreshChoices(ReadSession session)
    {
        _session = session;
        BillingChoices[0].Update(DefaultBillingLabel);
        _refreshing = true;
        try
        {
            AssignmentChoices = new[] { new EmailMappingChoice(null, "Choose assignment / project"), new EmailMappingChoice(0, $"Internal · {Short(session.Reference.InternalProject!.Name, 60)}") }
                .Concat(session.Reference.Projects.Where(p => p.Id != session.Reference.InternalProject.Id).OrderBy(p => p.Name).Select(p => new EmailMappingChoice(null, $"Project · {p.Name} (#{p.Id}) · No assignment", ProjectId: p.Id)))
                .Concat(session.Reference.Assignments.OrderBy(a => a.ProjectName).ThenBy(a => a.Name)
                    .Select(a => new EmailMappingChoice(a.Id, $"{a.ProjectName} · {a.Name} (#{a.Id})"))).ToList();
            TaskChoices = new[] { new EmailMappingChoice(null, "Choose task") }.Concat(session.Reference.Tasks.OrderBy(t => t.Name)
                .Select(t => new EmailMappingChoice(t.Id, $"{t.Name} (#{t.Id})"))).ToList();
            ExistingChoices = new[] { new EmailMappingChoice(null, "New work") }.Concat(session.Days.Single(d => d.Date == Activity.Date).Existing
                .OrderBy(r => r.RecordId).Select(r =>
                {
                    var assignment = session.Reference.Assignments.FirstOrDefault(a => a.Id == r.Assignment);
                    var project = assignment?.ProjectName ?? (r.Project == session.Reference.InternalProject!.Id ? session.Reference.InternalProject.Name : session.Reference.Projects.FirstOrDefault(p => p.Id == r.Project)?.Name ?? $"Project #{r.Project}");
                    var task = session.Reference.Tasks.FirstOrDefault(t => t.Id == r.Task)?.Name ?? $"Task #{r.Task}";
                    return new EmailMappingChoice(r.RecordId, $"#{r.RecordId} · {r.Hours:0.00} h · {Short(r.Description, 44)}",
                        $"#{r.RecordId} · {r.Hours:0.00} h\n{project}\n{assignment?.Name ?? (r.Assignment is int id ? $"Assignment #{id}" : "No assignment")} · {task}\n\n{r.Description}");
                })).ToList();
            foreach (var property in new[] { nameof(AssignmentChoices), nameof(TaskChoices), nameof(ExistingChoices), nameof(AssignmentSelection), nameof(TaskSelection), nameof(ExistingSelection), nameof(CanOverride), nameof(DefaultBillingLabel), nameof(BillingIndex), nameof(BillingLabel), nameof(BillingLight), nameof(BillingHint), nameof(HasUnavailableOverride) })
                PropertyChanged?.Invoke(this, new(property));
        }
        finally { _refreshing = false; }
    }

    internal sealed record Selection(int? Assignment, bool Internal, int? Task, int? Existing, bool AssignmentEdited, bool TaskEdited, string SuggestionReason, decimal? EditedHours, bool? BillableOverride, int? DirectProjectId, string? EditedDescription);
    internal Selection Capture() => new(Assignment, Internal, Task, Existing, _assignmentEdited, _taskEdited, _suggestionReason, EditedHours, BillableOverride, DirectProjectId, EditedDescription);
    internal void Restore(Selection value)
    {
        Assignment = value.Assignment; Internal = value.Internal; Task = value.Task; Existing = value.Existing;
        DirectProjectId = value.DirectProjectId;
        _assignmentEdited = value.AssignmentEdited; _taskEdited = value.TaskEdited; _suggestionReason = value.SuggestionReason;
        EditedHours = value.EditedHours; BillableOverride = value.BillableOverride;
        EditedDescription = value.EditedDescription;
        Changed();
    }
    private void Changed()
    {
        BillingChoices[0].Update(DefaultBillingLabel);
        foreach (var property in new[] { nameof(AssignmentSelection), nameof(TaskSelection), nameof(ExistingSelection), nameof(CanEditNew), nameof(IsResolved), nameof(State), nameof(Reason), nameof(Hours), nameof(HoursLabel), nameof(HoursHint), nameof(Description), nameof(EditedDescription), nameof(IsDescriptionEdited), nameof(ActivitySummary), nameof(DescriptionHint), nameof(CanOverride), nameof(DefaultBillingLabel), nameof(BillingIndex), nameof(BillingLabel), nameof(BillingLight), nameof(BillingHint), nameof(HasUnavailableOverride) })
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
