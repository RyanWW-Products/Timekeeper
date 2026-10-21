using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

/// <summary>Edits a local draft only. Saving never submits a Quickbase record.</summary>
internal sealed class ManualTimecardWindow : Window, INotifyPropertyChanged
{
    private readonly TimecardApi? _api;
    private readonly ManualTimecard? _existing;
    private readonly DateOnly? _initialDate;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _date = new();
    private readonly TextBox _hours = new() { MaxLength = 12 };
    private readonly TextBox _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 94, MaxHeight = 150, MaxLength = 4000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly SearchableTaskComboBox _task = new();
    private readonly ComboBox _billing = new();
    private readonly TextBlock _billingLabel = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _billingHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 7, 0, 0) };
    private readonly System.Windows.Shapes.Ellipse _light = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _useDefault = new() { Content = "Use Quickbase default", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _refreshBilling = new() { Content = "Refresh billing", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _project = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _assignment = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 10) };
    private readonly Button _find = new() { Content = "Find assignment or project", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _internal = new() { Content = "Use internal project" };
    private readonly Button _save = new();
    private readonly StackPanel _form = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly List<EmailBillingChoice> _billingChoices = [new("Default unavailable"), new("Billable"), new("Nonbillable")];
    private List<EmailMappingChoice> _taskChoices = [];
    private EmailMappingChoice? _taskChoice;
    private int _projectId;
    private int? _assignmentId;
    private bool? _billableOverride;
    private bool _busy, _closed, _updating, _taskEdited;

    public ReadSession Session { get; private set; }
    public ManualTimecard? Entry { get; private set; }
    public EmailMappingChoice? TaskChoice
    {
        get => _taskChoice;
        set
        {
            if (Equals(value, _taskChoice)) return;
            _taskChoice = value; _taskEdited = true;
            PropertyChanged?.Invoke(this, new(nameof(TaskChoice))); UpdateBilling();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ManualTimecardWindow(Window owner, ReadSession session, TimecardApi? api, ManualTimecard? existing = null, bool smoke = false, DateOnly? initialDate = null)
    {
        if (!smoke) Owner = owner;
        Session = session; _api = api; _existing = existing; _initialDate = existing?.Date ?? initialDate;
        _projectId = existing?.Project ?? session.Reference.InternalProject?.Id ?? 0;
        _assignmentId = existing?.Assignment;
        _billableOverride = existing is not null ? existing.BillableOverride : session.Reference.Billing?.CanOverride == true ? false : null;
        _taskEdited = existing is not null;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = existing is null ? "Timekeeper · Add manual timecard" : "Timekeeper · Edit manual timecard";
        Width = 760; Height = 850; MinWidth = 560; MinHeight = 580;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(26, 22, 26, 22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "TIMECARDS / MANUAL", Style = (Style)FindResource("SectionLabel") });
        var heading = new TextBlock { Text = existing is null ? "Add manual timecard" : "Edit manual timecard", FontSize = 29, Margin = new Thickness(0, 7, 0, 10) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(heading);
        header.Children.Add(new TextBlock { Text = "Save work now, then append it to a Toggl read or email import later. Nothing is sent until you review and submit it.", TextWrapping = TextWrapping.Wrap }); root.Children.Add(header);

        var dates = session.Days.Select(day => day.Date).Distinct().Order().Select(date => new DateChoice(date)).ToList();
        _date.ItemsSource = dates; _date.SelectedItem = _initialDate is DateOnly chosen ? dates.FirstOrDefault(date => date.Date == chosen) : dates.FirstOrDefault();
        _hours.Text = (existing?.Hours ?? .25m).ToString("0.00", CultureInfo.CurrentCulture);
        _description.Text = existing?.Description ?? "";
        var firstRow = new Grid(); firstRow.ColumnDefinitions.Add(new()); firstRow.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); firstRow.ColumnDefinitions.Add(new());
        firstRow.Children.Add(Field("Date", _date));
        var hoursField = Field("Decimal hours", _hours, "0.25 h = 15 minutes. Manual hours are used exactly."); Grid.SetColumn(hoursField, 2); firstRow.Children.Add(hoursField); _form.Children.Add(firstRow);
        _form.Children.Add(Field("Description", _description, "Briefly describe the work performed."));

        var mapping = new StackPanel(); mapping.Children.Add(_project); mapping.Children.Add(_assignment);
        var mappingButtons = new WrapPanel(); mappingButtons.Children.Add(_find); mappingButtons.Children.Add(_internal); mapping.Children.Add(mappingButtons);
        _form.Children.Add(Field("Project / assignment", new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14), Child = mapping }));
        _form.Children.Add(Field("Task", _task, "Search by task name or ID. Choosing a task also sets its Quickbase category."));
        _task.SetBinding(SearchableTaskComboBox.SelectedItemProperty, new Binding(nameof(TaskChoice)) { Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        RefreshTasks(existing?.Task ?? session.Settings.InternalTaskId);

        _billing.ItemsSource = _billingChoices;
        var choiceLabel = new FrameworkElementFactory(typeof(TextBlock)); choiceLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(EmailBillingChoice.Label)));
        _billing.ItemTemplate = new DataTemplate { VisualTree = choiceLabel };
        var billingLine = new Grid(); billingLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); billingLine.ColumnDefinitions.Add(new());
        billingLine.Children.Add(_light); Grid.SetColumn(_billing, 1); billingLine.Children.Add(_billing); Grid.SetColumn(_billingLabel, 1); billingLine.Children.Add(_billingLabel);
        var billingBody = new StackPanel(); billingBody.Children.Add(billingLine); billingBody.Children.Add(_billingHint); billingBody.Children.Add(_useDefault); billingBody.Children.Add(_refreshBilling);
        _form.Children.Add(Field("Billing", billingBody));
        _billingHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _assignment.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel(); footer.Children.Add(_error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        _save.Content = existing is null ? "Add timecard" : "Save changes"; _save.Style = (Style)FindResource("Primary"); _save.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(_save); footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer);
        _find.Click += async (_, _) => await FindAsync();
        _internal.Click += (_, _) => { if (Session.Reference.InternalProject is { } project) { _projectId = project.Id; _assignmentId = null; UpdateMapping(); UpdateBilling(); } };
        _refreshBilling.Click += async (_, _) => await RefreshBillingAsync();
        _useDefault.Click += (_, _) => { _billableOverride = null; UpdateBilling(); };
        _billing.SelectionChanged += (_, _) =>
        {
            if (_updating || Session.Reference.Billing?.CanOverride != true || _billing.SelectedIndex is < 0 or > 2) return;
            _billableOverride = _billing.SelectedIndex == 0 ? null : _billing.SelectedIndex == 1; UpdateBilling();
        };
        AutomationProperties.SetName(_date, "Manual timecard date"); AutomationProperties.SetName(_hours, "Manual timecard decimal hours");
        AutomationProperties.SetName(_description, "Manual timecard description"); AutomationProperties.SetName(_task, "Manual timecard task");
        AutomationProperties.SetName(_billing, "Manual timecard billing");
        Content = root; Appearance.Attach(this); UpdateMapping(); UpdateBilling(); UpdateBusy();
        Loaded += (_, _) => { _description.Focus(); _description.CaretIndex = _description.Text.Length; };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); if (!_busy) _lifetime.Dispose(); };
    }

    private static FrameworkElement Field(string label, FrameworkElement control, string? hint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }); panel.Children.Add(control);
        if (hint is not null)
        {
            var help = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            help.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(help);
        }
        return panel;
    }
    private void RefreshTasks(int id)
    {
        _taskChoices = Session.Reference.Tasks.OrderBy(task => task.Name).Select(task => new EmailMappingChoice(task.Id, $"{task.Name} (#{task.Id})")).ToList();
        _task.ItemsSource = _taskChoices; _taskChoice = _taskChoices.FirstOrDefault(task => task.Id == id);
        PropertyChanged?.Invoke(this, new(nameof(TaskChoice)));
    }
    private void UpdateMapping()
    {
        var assignment = Session.Reference.Assignments.FirstOrDefault(item => item.Id == _assignmentId);
        var project = Session.Reference.InternalProject?.Id == _projectId ? Session.Reference.InternalProject
            : Session.Reference.Projects.FirstOrDefault(item => item.Id == _projectId);
        _project.Text = $"{project?.Name ?? assignment?.ProjectName ?? "Choose a project"}{(_projectId > 0 ? $" (#{_projectId})" : "")}";
        _assignment.Text = assignment is not null ? $"Assignment: {assignment.Name} (#{assignment.Id})"
            : _assignmentId is int missing ? $"Assignment #{missing} is no longer in the available reference data. Choose it again."
            : "No assignment. Time applies directly to this project.";
        _internal.IsEnabled = !_busy && Session.Reference.InternalProject is not null;
    }
    private string DefaultLabel => Session.Reference.Billing?.DefaultFor(_projectId, _taskChoice?.Id ?? 0) switch
    { true => "Billable - Default", false => "Nonbillable - Default", _ => "Default unavailable" };
    private void UpdateBilling()
    {
        _updating = true;
        try
        {
            var billing = Session.Reference.Billing;
            var canOverride = billing?.CanOverride == true;
            var effective = _billableOverride ?? billing?.DefaultFor(_projectId, _taskChoice?.Id ?? 0);
            _billingChoices[0].Update(DefaultLabel); _billing.SelectedIndex = _billableOverride is null ? 0 : _billableOverride.Value ? 1 : 2;
            _billing.Visibility = canOverride ? Visibility.Visible : Visibility.Collapsed;
            _billingLabel.Visibility = canOverride ? Visibility.Collapsed : Visibility.Visible;
            _billingLabel.Text = _billableOverride switch { true => "Billable - Requested override", false => "Nonbillable - Requested override", _ => DefaultLabel };
            _light.Fill = new SolidColorBrush(effective switch { true => Color.FromRgb(63, 203, 125), false => Color.FromRgb(139, 75, 78), _ => Color.FromRgb(125, 133, 143) });
            _light.ToolTip = _billableOverride switch { true => "Billable", false => "Nonbillable", _ => DefaultLabel };
            var reason = billing?.DefaultUnavailableReason(_projectId, _taskChoice?.Id ?? 0) ?? "Billing has not been checked. Refresh billing to read Quickbase again.";
            _billingHint.Text = !canOverride && _billableOverride.HasValue
                ? "Your account cannot apply the saved billing override. Use Quickbase default to continue, or refresh billing after your permissions change."
                : _billableOverride.HasValue ? $"{(_billableOverride.Value ? "Billable" : "Nonbillable")} override selected. Project, assignment and task stay the same."
                : reason.Length > 0 ? reason
                : canOverride ? $"{DefaultLabel}. Billing changes keep the same project, assignment and task."
                : $"{DefaultLabel}. Your account cannot change the billing override.";
            _useDefault.Visibility = !canOverride && _billableOverride.HasValue ? Visibility.Visible : Visibility.Collapsed;
            _refreshBilling.IsEnabled = !_busy && _api is not null;
        }
        finally { _updating = false; }
    }
    private void UpdateBusy()
    {
        _form.IsEnabled = _save.IsEnabled = !_busy;
        _refreshBilling.IsEnabled = !_busy && _api is not null;
        _internal.IsEnabled = !_busy && Session.Reference.InternalProject is not null;
    }

    private async Task FindAsync()
    {
        if (_closed || _busy) return;
        var picker = new AssignmentSearchWindow(this, Session.Reference.Assignments,
            (query, token) => _api is null ? Task.FromResult(AssignmentSearchWindow.Filter(Session.Reference.Assignments, query)) : _api.FindAssignmentsAsync(query, token),
            availableProjects: Session.Reference.Projects,
            searchProjects: (query, token) => _api is null ? Task.FromResult(AssignmentSearchWindow.FilterProjects(Session.Reference.Projects, query)) : _api.FindProjectsAsync(query, token));
        if (picker.ShowDialog() != true || _closed) return;
        var selectedAssignment = picker.SelectedAssignment; var selectedProject = picker.SelectedProject;
        if (selectedAssignment is null && selectedProject is null) return;
        await RunAsync(async () =>
        {
            var reference = Session.Reference;
            if (selectedAssignment is not null) reference = reference with { Assignments = reference.Assignments.Where(item => item.Id != selectedAssignment.Id).Append(selectedAssignment).ToList() };
            if (selectedProject is not null) reference = reference with { Projects = reference.Projects.Where(item => item.Id != selectedProject.Id).Append(selectedProject).ToList() };
            var projectId = selectedProject?.Id ?? selectedAssignment!.ProjectId;
            if (_api is not null) reference = reference with { Billing = await _api.ReadBillingDefaultsAsync(reference.Assignments.Select(item => item.ProjectId).Concat(reference.Projects.Select(item => item.Id)).Append(projectId).Append(reference.InternalProject?.Id ?? 0).Where(id => id > 0), _lifetime.Token) };
            if (_closed) return;
            Session = Session with { Reference = reference }; _projectId = projectId; _assignmentId = selectedAssignment?.Id;
            if (!_taskEdited && selectedAssignment?.TaskId is int suggested && reference.Tasks.Any(task => task.Id == suggested)) RefreshTasks(suggested);
            UpdateMapping(); UpdateBilling();
        }, "Reading Quickbase billing for the selected project...");
    }
    private async Task RefreshBillingAsync()
    {
        if (_api is null || _closed || _busy) return;
        await RunAsync(async () =>
        {
            var billing = await _api.ReadBillingDefaultsAsync(Session.Reference.Assignments.Select(item => item.ProjectId).Concat(Session.Reference.Projects.Select(item => item.Id)).Append(_projectId).Append(Session.Reference.InternalProject?.Id ?? 0).Where(id => id > 0), _lifetime.Token, refresh: true);
            if (_closed) return;
            Session = Session with { Reference = Session.Reference with { Billing = billing } }; UpdateBilling();
        }, "Refreshing Quickbase billing...");
    }
    private async Task RunAsync(Func<Task> operation, string message)
    {
        if (_busy || _closed) return;
        _busy = true; UpdateBusy(); _error.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _error.Text = message;
        try { await operation(); if (!_closed) _error.Text = ""; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) { _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); _error.Text = error.Message; } }
        finally { _busy = false; if (_closed) _lifetime.Dispose(); else UpdateBusy(); }
    }
    private Task SaveAsync()
    {
        if (_busy || _closed) return Task.CompletedTask;
        if (!TryBuild(out var entry, out var error)) { _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); _error.Text = error; return Task.CompletedTask; }
        Entry = entry; DialogResult = true; return Task.CompletedTask;
    }
    private bool TryBuild(out ManualTimecard? entry, out string error)
    {
        entry = null; error = "";
        if (_date.SelectedItem is not DateChoice date || !Session.Days.Any(day => day.Date == date.Date)) error = "Choose a date from the days currently loaded in Timekeeper.";
        else if (!TryHours(_hours.Text, out _)) error = "Enter more than 0 and at most 24 hours, with up to two decimal places. For example, 0.25 is 15 minutes.";
        else if (string.IsNullOrWhiteSpace(_description.Text) || _description.Text.Length > 4000 || _description.Text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')) error = "Enter a description of the work, up to 4,000 characters.";
        else if (_projectId <= 0) error = "Choose an internal project, case or assignment.";
        else if (_assignmentId.HasValue && !Session.Reference.Assignments.Any(item => item.Id == _assignmentId && item.ProjectId == _projectId)) error = "Choose the assignment again so its project can be verified.";
        else if (!_assignmentId.HasValue && Session.Reference.InternalProject?.Id != _projectId && !Session.Reference.Projects.Any(item => item.Id == _projectId)) error = "Find and select this project again so its Quickbase record can be verified.";
        else if (_taskChoice?.Id is not int task || !Session.Reference.Tasks.Any(item => item.Id == task && Session.Reference.Categories.Any(category => category.Id == item.CategoryId))) error = "Choose a task with an available Quickbase category.";
        else if (_billableOverride.HasValue && Session.Reference.Billing?.CanOverride != true) error = "Your account cannot apply this billing override. Choose Use Quickbase default, or refresh billing after your permissions change.";
        if (error.Length > 0) return false;
        TryHours(_hours.Text, out var hours);
        entry = new ManualTimecard { Id = _existing?.Id ?? Guid.NewGuid().ToString("N"), Date = ((DateChoice)_date.SelectedItem).Date, Hours = hours, Project = _projectId, Assignment = _assignmentId, Task = _taskChoice!.Id!.Value, Description = _description.Text.Trim(), BillableOverride = _billableOverride };
        var candidate = entry;
        var errors = ManualTimecards.ValidateDrafts(Session.ManualTimecards.Where(item => item.Id != candidate.Id).Append(candidate));
        if (errors.Count > 0) { error = string.Join(" ", errors); entry = null; return false; }
        return true;
    }
    private static bool TryHours(string text, out decimal value)
    {
        text = text.Trim();
        return (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out value) || decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) && value is > 0 and <= 24 && decimal.Round(value, 2) == value;
    }

    internal string SmokeSummary()
    {
        var savedSession = Session; var savedTaskEdited = _taskEdited;
        var savedHours = _hours.Text; var savedDescription = _description.Text; var savedTask = _taskChoice;
        var savedDate = _date.SelectedItem; var savedProject = _projectId; var savedAssignment = _assignmentId; var savedBilling = _billableOverride;
        try
        {
            if (_existing is null && (_projectId != Session.Reference.InternalProject?.Id || _assignmentId is not null || _taskChoice?.Id != Session.Settings.InternalTaskId || _billableOverride != (Session.Reference.Billing?.CanOverride == true ? false : null)))
                throw new InvalidOperationException("Manual timecards must start with the configured internal mapping and permission-aware nonbillable default.");
            _description.Text = "Manual UI smoke test"; _hours.Text = ".25";
            if (!TryBuild(out var valid, out var failure) || valid is null || valid.Hours != .25m) throw new InvalidOperationException("Valid manual timecard was rejected: " + failure);
            if (_initialDate.HasValue && valid.Date != _initialDate.Value)
                throw new InvalidOperationException("Manual timecards must use the chosen workspace date even when older dates are loaded.");
            if (_existing is not null && (valid.Id != _existing.Id || valid.Project != _existing.Project || valid.Assignment != _existing.Assignment || valid.Task != _existing.Task || valid.BillableOverride != _existing.BillableOverride))
                throw new InvalidOperationException("Editing a manual timecard must preserve its identity and mapping.");
            foreach (var badHours in new[] { "", "0", "-1", "24.01", "0.001", "abc", "1,000" })
            {
                _hours.Text = badHours;
                if (TryBuild(out _, out _)) throw new InvalidOperationException("Manual editor accepted invalid hours: " + badHours);
            }
            _hours.Text = "0.25"; _description.Text = " ";
            if (TryBuild(out _, out _)) throw new InvalidOperationException("Manual editor accepted an empty description.");
            _description.Text = "Manual UI smoke test"; _date.SelectedItem = null;
            if (TryBuild(out _, out _)) throw new InvalidOperationException("Manual editor accepted an unloaded date.");
            _date.SelectedItem = savedDate; _taskChoice = null;
            if (TryBuild(out _, out _)) throw new InvalidOperationException("Manual editor accepted an absent task.");
            _taskChoice = savedTask; _assignmentId = int.MaxValue;
            if (TryBuild(out _, out _)) throw new InvalidOperationException("Manual editor accepted an unknown assignment.");
            _assignmentId = savedAssignment;
            var alternate = _taskChoices.FirstOrDefault(choice => choice.Id != savedTask?.Id);
            if (alternate is not null)
            {
                TaskChoice = alternate;
                if (_projectId != savedProject || _assignmentId != savedAssignment || _hours.Text != "0.25")
                    throw new InvalidOperationException("Changing a manual task must retain its project, assignment and hours.");
                _taskChoice = savedTask;
            }
            Session = Session with { Reference = Session.Reference with { Billing = (Session.Reference.Billing ?? new BillingCapabilities()) with { CanOverride = false } } };
            _billableOverride = null; UpdateBilling();
            if (_billing.Visibility != Visibility.Collapsed || _billingLabel.Text != DefaultLabel || !TryBuild(out _, out _))
                throw new InvalidOperationException("Restricted accounts must see the actual default without an editable billing dropdown.");
            _billableOverride = false; UpdateBilling();
            if (TryBuild(out _, out _) || _useDefault.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Unavailable saved billing overrides must require an explicit return to Quickbase default.");
            if (Entry is not null) throw new InvalidOperationException("Manual validation must not save an entry or write Quickbase.");
            return "PASS: manual editor defaults, decimal hours, required fields, verified mapping, edit identity and permission-aware billing\n";
        }
        finally
        {
            Session = savedSession; _taskEdited = savedTaskEdited;
            _hours.Text = savedHours; _description.Text = savedDescription; _date.SelectedItem = savedDate; _taskChoice = savedTask;
            _projectId = savedProject; _assignmentId = savedAssignment; _billableOverride = savedBilling;
            PropertyChanged?.Invoke(this, new(nameof(TaskChoice))); UpdateMapping(); UpdateBilling();
        }
    }
    private sealed record DateChoice(DateOnly Date) { public override string ToString() => Date.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture); }
}
