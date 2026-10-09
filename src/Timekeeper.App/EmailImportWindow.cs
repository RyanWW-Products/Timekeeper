using System.Windows;
using System.Windows.Controls;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class EmailImportWindow : Window
{
    private sealed record Choice(int? Id, string Label) { public override string ToString() => Label; }
    private sealed class Mapping(EmailActivity activity)
    {
        public EmailActivity Activity { get; } = activity;
        public int? Assignment;
        public bool Internal;
        public int? Task;
        public int? Existing;
        public override string ToString() => $"{(Existing.HasValue || ((Assignment.HasValue || Internal) && Task.HasValue) ? "✓" : "○")} {Activity.Date:MMM d} · {Activity.Minutes} min · {Activity.MatterHint}";
    }
    private readonly TimecardApi? _api;
    private readonly List<Mapping> _mappings;
    private readonly ListBox _activities = new() { MinWidth = 245, Margin = new Thickness(0, 0, 20, 0) };
    private readonly ComboBox _assignment = new() { MinWidth = 260 }, _task = new(), _existing = new();
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10) };
    private readonly Button _find = new() { Content = "Find assignment", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _continue = new() { Content = "Review timecards" };
    private readonly CheckBox _confirm = new() { Content = "These activities and confirmed minutes are mine.", Margin = new Thickness(0, 12, 0, 12) };
    private bool _loading;
    public ReadSession Session { get; private set; }
    public ProposalEnvelope Proposal { get; private set; } = new();

    public EmailImportWindow(Window owner, ReadSession session, TimecardApi? api, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Session = session; _api = api;
        if(session.EmailWorkbook is null || session.Reference.InternalProject is null)
            throw new InvalidOperationException("The email workbook or configured internal project is unavailable. Check the internal project in Settings and import again.");
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Review email import"; Width = 1050; Height = 780; MinWidth = 800; MinHeight = 580;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mappings = session.EmailWorkbook!.Activities.Where(a => session.Days.Any(d => d.Date == a.Date)).Select(a => new Mapping(a)).ToList();
        var activityText = new FrameworkElementFactory(typeof(TextBlock));
        activityText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        activityText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        activityText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        activityText.SetValue(TextBlock.MaxHeightProperty, 62d);
        _activities.ItemTemplate = new DataTemplate { VisualTree = activityText };
        _activities.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var listItem = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem))); listItem.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _activities.ItemContainerStyle = listItem;
        foreach (var map in _mappings)
        {
            var matches = session.Reference.Assignments.Where(a => Eq(map.Activity.AssignmentHint, a.Name) && (string.IsNullOrWhiteSpace(map.Activity.MatterHint) || Eq(map.Activity.MatterHint, a.ProjectName))).ToList();
            if (matches.Count == 1) map.Assignment = matches[0].Id;
            if (Eq(map.Activity.MatterHint, session.Reference.InternalProject!.Name) && string.IsNullOrWhiteSpace(map.Activity.AssignmentHint)) map.Internal = true;
            var tasks = session.Reference.Tasks.Where(t => Eq(map.Activity.TaskHint, t.Name)).ToList();
            if (tasks.Count == 1) map.Task = tasks[0].Id;
        }
        var outer = new Grid { Margin = new Thickness(28) };
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto }); outer.RowDefinitions.Add(new()); outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "EMAIL / QUICKBASE", Style = (Style)FindResource("SectionLabel") });
        var title = new TextBlock { Text = "Match your activities", FontSize = 30, Margin = new Thickness(0, 8, 0, 10) }; title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = $"{session.EmailWorkbook.Metadata.EmployeeEmail} · {session.EmailWorkbook.Metadata.TimeZone}\nSelect the Quickbase assignment and task for each activity. Hours come from your confirmed minutes. Billing can be changed in the next review.", TextWrapping = TextWrapping.Wrap }); outer.Children.Add(header);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(0.85, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(1.3, GridUnitType.Star) });
        _activities.ItemsSource = _mappings; _activities.SelectionChanged += (_, _) => Present(); body.Children.Add(_activities);
        var editor = new StackPanel(); editor.Children.Add(_details);
        Add(editor, "Already in Quickbase?", _existing); Add(editor, "Assignment and project", _assignment);
        editor.Children.Add(_find);
        editor.Children.Add(new TextBlock { Text = "Find and select an assignment beyond your usual list.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 });
        Add(editor, "Task", _task);
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll); Grid.SetRow(body, 1); outer.Children.Add(body);
        var footer = new StackPanel(); footer.Children.Add(_status); footer.Children.Add(_confirm);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) }); _continue.Style = (Style)FindResource("Primary"); buttons.Children.Add(_continue); footer.Children.Add(buttons); Grid.SetRow(footer, 2); outer.Children.Add(footer);
        _assignment.SelectionChanged += (_, _) => Changed(); _task.SelectionChanged += (_, _) => Changed(); _existing.SelectionChanged += (_, _) => Changed();
        _confirm.Checked += (_, _) => Update(); _confirm.Unchecked += (_, _) => Update();
        _find.Click += Find_Click; _continue.Click += (_, _) => Accept();
        Content = outer; _activities.SelectedIndex = 0; Update(); Appearance.Attach(this);
    }
    private static bool Eq(string a, string b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static void Add(Panel parent, string label, Control control)
    {
        parent.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 12, 0, 6) }); parent.Children.Add(control);
    }
    private void Present()
    {
        if (_activities.SelectedItem is not Mapping map) return;
        _loading = true;
        var a = map.Activity;
        _details.Text = $"{a.Date:dddd, MMMM d, yyyy} · {a.Minutes} confirmed minutes\n{(a.TimeBasis == "measured" ? "Measured time" : "Employee-confirmed estimate")}\n\n{a.Description}\n\nMatter: {a.MatterHint}\nAssignment: {a.AssignmentHint}\nTask: {a.TaskHint}\nBilling requested: {(a.Billable is null ? "Quickbase default" : a.Billable.Value ? "Billable" : "Non-billable")}";
        if (a.EvidenceSummary.Length > 0) _details.Text += "\n\nEvidence reviewed: " + a.EvidenceSummary;
        if (a.ReviewNotes.Length > 0) _details.Text += "\n\nNeeds attention: " + a.ReviewNotes;
        _assignment.ItemsSource = new[] { new Choice(null, "Choose an assignment"), new Choice(0, $"Internal · {Session.Reference.InternalProject!.Name}") }.Concat(Session.Reference.Assignments.OrderBy(a => a.ProjectName).ThenBy(a => a.Name).Select(a => new Choice(a.Id, $"{a.ProjectName} · {a.Name} (#{a.Id})"))).ToList();
        _assignment.SelectedItem = _assignment.Items.Cast<Choice>().First(c => c.Id == (map.Internal ? 0 : map.Assignment));
        _task.ItemsSource = new[] { new Choice(null, "Choose a task") }.Concat(Session.Reference.Tasks.OrderBy(t => t.Name).Select(t => new Choice(t.Id, $"{t.Name} (#{t.Id})"))).ToList();
        _task.SelectedItem = _task.Items.Cast<Choice>().First(c => c.Id == map.Task);
        _existing.ItemsSource = new[] { new Choice(null, "New work to submit") }.Concat(Session.Days.Single(d => d.Date == a.Date).Existing.Select(r => new Choice(r.RecordId, $"#{r.RecordId} · {r.Hours:0.00} h · {r.Description}"))).ToList();
        _existing.SelectedItem = _existing.Items.Cast<Choice>().First(c => c.Id == map.Existing);
        _assignment.IsEnabled = _task.IsEnabled = _find.IsEnabled = !map.Existing.HasValue;
        _loading = false;
    }
    private void Changed()
    {
        if (_loading || _activities.SelectedItem is not Mapping map) return;
        var assignment = (_assignment.SelectedItem as Choice)?.Id;
        map.Internal = assignment == 0; map.Assignment = assignment > 0 ? assignment : null;
        map.Task = (_task.SelectedItem as Choice)?.Id; map.Existing = (_existing.SelectedItem as Choice)?.Id;
        _assignment.IsEnabled = _task.IsEnabled = _find.IsEnabled = !map.Existing.HasValue;
        _confirm.IsChecked = false; _activities.Items.Refresh(); Update();
    }
    private void Update()
    {
        var count = _mappings.Count(m => m.Existing.HasValue || ((m.Assignment.HasValue || m.Internal) && m.Task.HasValue));
        _status.Text = $"{count} of {_mappings.Count} activities matched. Nothing is written until you review and click Write to Quickbase.";
        _continue.IsEnabled = count == _mappings.Count && _confirm.IsChecked == true;
    }
    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_activities.SelectedItem is not Mapping map || map.Existing.HasValue) return;
        var picker = new AssignmentSearchWindow(this, Session.Reference.Assignments,
            (query, token) => _api is null ? Task.FromResult(AssignmentSearchWindow.Filter(Session.Reference.Assignments, query)) : _api.FindAssignmentsAsync(query, token),
            initialQuery: map.Activity.MatterHint);
        if (picker.ShowDialog() != true || picker.SelectedAssignment is not AssignmentRecord selected) return;
        Session = Session with { Reference = Session.Reference with { Assignments = Session.Reference.Assignments.Where(a => a.Id != selected.Id).Append(selected).ToList() } };
        map.Internal = false; map.Assignment = selected.Id;
        Present(); _confirm.IsChecked = false; _activities.Items.Refresh(); Update();
    }
    private void Accept()
    {
        if (!_continue.IsEnabled) return;
        Proposal = BuildProposal();
        var result = EmailRules.Validate(Session, Proposal);
        if (!result.IsValid) { _status.Text = string.Join("\n", result.Errors); return; }
        DialogResult = true;
    }
    private ProposalEnvelope BuildProposal()
    {
        var rows = new List<ProposalRow>(); var recorded = new List<RecordedActivityLink>();
        foreach (var map in _mappings)
        {
            if (map.Existing is int existing) { recorded.Add(new() { ActivityId = map.Activity.ActivityId, ExistingRecordId = existing }); continue; }
            var assignment = map.Assignment.HasValue ? Session.Reference.Assignments.Single(a => a.Id == map.Assignment) : null;
            var task = Session.Reference.Tasks.Single(t => t.Id == map.Task);
            rows.Add(new() { Date = map.Activity.Date, SourceActivityIds = [map.Activity.ActivityId], Project = assignment?.ProjectId ?? Session.Reference.InternalProject!.Id, Assignment = assignment?.Id, Task = task.Id, Category = task.CategoryId, Description = map.Activity.Description, BillableOverride = map.Activity.Billable });
        }
        return new() { SessionId = Session.SessionId, EmployeeId = Session.Settings.EmployeeId, Rows = rows, EmailAlreadyRecorded = recorded };
    }
    internal string SmokeSummary()
    {
        if (_continue.IsEnabled) throw new InvalidOperationException("Email import accepted without confirmation.");
        var originalAssignment=_assignment.SelectedIndex; var originalTask=_task.SelectedIndex;
        _assignment.SelectedIndex = 1; _task.SelectedIndex = 1;
        _confirm.IsChecked = true;
        if (!_continue.IsEnabled) throw new InvalidOperationException("Mapped email activity could not be reviewed.");
        _assignment.SelectedIndex=originalAssignment; _task.SelectedIndex=originalTask; _confirm.IsChecked=true;
        Proposal=BuildProposal();
        if(!EmailRules.Validate(Session,Proposal).IsValid) throw new InvalidOperationException("Mapped email proposal was invalid.");
        return "PASS: email mapping requires an assignment/task and personal confirmation\n";
    }
}
