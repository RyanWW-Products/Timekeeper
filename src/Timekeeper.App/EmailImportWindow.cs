using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class EmailImportWindow : Window
{
    private readonly TimecardApi? _api;
    private readonly List<EmailMappingRow> _mappings;
    private readonly DataGrid _activities = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        CanUserDeleteRows = false, CanUserReorderColumns = false, SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow, RowHeight = double.NaN, MinRowHeight = 94,
        EnableRowVirtualization = true, EnableColumnVirtualization = false, FrozenColumnCount = 1,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10), FontSize = 12 };
    private readonly Button _continue = new() { Content = "Review timecards" };
    private readonly Button _details = new() { Content = "View selected activity", HorizontalAlignment = HorizontalAlignment.Left };
    public ReadSession Session { get; private set; }
    public ProposalEnvelope Proposal { get; private set; } = new();

    public EmailImportWindow(Window owner, ReadSession session, TimecardApi? api, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Session = session; _api = api;
        if (session.EmailWorkbook is null || session.Reference.InternalProject is null)
            throw new InvalidOperationException("The email workbook or configured internal project is unavailable. Check the internal project in Settings and import again.");
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Match email activities"; Width = Math.Min(1380, SystemParameters.WorkArea.Width); Height = 780;
        MinWidth = Math.Min(920, SystemParameters.WorkArea.Width); MaxWidth = SystemParameters.WorkArea.Width; MinHeight = 580;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mappings = session.EmailWorkbook.Activities.Where(a => session.Days.Any(d => d.Date == a.Date))
            .Select(a => new EmailMappingRow(a, session, Update)).ToList();

        var outer = new Grid { Margin = new Thickness(24) };
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto }); outer.RowDefinitions.Add(new()); outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "EMAIL / QUICKBASE", Style = (Style)FindResource("SectionLabel") });
        var title = new TextBlock { Text = "Match your activities", FontSize = 30, Margin = new Thickness(0, 7, 0, 8) };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = "Check the suggested assignments and tasks below. Change any dropdown, or find an assignment. Use an existing record only when this work was already recorded.", TextWrapping = TextWrapping.Wrap });
        var account = new TextBlock { Text = $"{session.EmailWorkbook.Metadata.EmployeeEmail} · {session.EmailWorkbook.Metadata.TimeZone}", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        account.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); header.Children.Add(account); outer.Children.Add(header);

        _activities.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _activities.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_activities, "Email activity assignment and task matching table");
        var cellStyle = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 8, 7, 8)));
        _activities.CellStyle = cellStyle;
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Date / minutes", Width = 102, MinWidth = 102, CellTemplate = TextTemplate(nameof(EmailMappingRow.DateAndMinutes), null) });
        var activity = new FrameworkElementFactory(typeof(StackPanel));
        var matter = Text(nameof(EmailMappingRow.MatterSummary), bold: true, lines: 1);
        matter.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Activity.MatterHint")); activity.AppendChild(matter);
        var description = Text(nameof(EmailMappingRow.ActivitySummary), lines: 2);
        description.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Activity.Description")); activity.AppendChild(description);
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Activity", Width = new DataGridLength(1.15, DataGridLengthUnitType.Star), MinWidth = 180, CellTemplate = new DataTemplate { VisualTree = activity } });
        var assignment = new FrameworkElementFactory(typeof(StackPanel));
        assignment.AppendChild(Choice(nameof(EmailMappingRow.AssignmentChoices), nameof(EmailMappingRow.AssignmentSelection), "Assignment and project", true));
        var find = new FrameworkElementFactory(typeof(Button));
        find.SetValue(ContentControl.ContentProperty, "Find assignment"); find.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        find.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0)); find.SetValue(Control.PaddingProperty, new Thickness(9, 4, 9, 4));
        find.SetValue(FrameworkElement.MinHeightProperty, 26d); find.SetValue(Control.FontSizeProperty, 11d);
        find.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(EmailMappingRow.CanEditNew)));
        find.AddHandler(Button.ClickEvent, new RoutedEventHandler(Find_Click)); assignment.AppendChild(find);
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Assignment / project", Width = new DataGridLength(1.4, DataGridLengthUnitType.Star), MinWidth = 230, CellTemplate = new DataTemplate { VisualTree = assignment } });
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Task", Width = new DataGridLength(0.8, DataGridLengthUnitType.Star), MinWidth = 145, CellTemplate = new DataTemplate { VisualTree = Choice(nameof(EmailMappingRow.TaskChoices), nameof(EmailMappingRow.TaskSelection), "Task", true) } });
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Quickbase record", Width = new DataGridLength(0.9, DataGridLengthUnitType.Star), MinWidth = 165, CellTemplate = new DataTemplate { VisualTree = Choice(nameof(EmailMappingRow.ExistingChoices), nameof(EmailMappingRow.ExistingSelection), "New work or existing Quickbase record", false) } });
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Match", Width = new DataGridLength(0.8, DataGridLengthUnitType.Star), MinWidth = 150, CellTemplate = TextTemplate(nameof(EmailMappingRow.State), nameof(EmailMappingRow.Reason)) });
        _activities.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            // Explicit logical widths prevent WPF star columns from keeping their previous
            // widths when interface scale or the window size changes. Below the combined
            // minimum, the table scrolls horizontally instead of compressing the controls.
            var minimum = _activities.Columns.Select(column => column.MinWidth).ToArray();
            var extra = Math.Max(0, e.NewSize.Width - 20 - minimum.Sum());
            double[] weights = [0, 0.25, 0.32, 0.13, 0.15, 0.15];
            for (var i = 0; i < minimum.Length; i++) _activities.Columns[i].Width = minimum[i] + extra * weights[i];
        };
        _activities.ItemsSource = _mappings;
        _activities.SelectionChanged += (_, _) => _details.IsEnabled = _activities.SelectedItem is EmailMappingRow;
        Grid.SetRow(_activities, 1); outer.Children.Add(_activities);

        var footer = new StackPanel(); footer.Children.Add(_status);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _details.Click += (_, _) => ShowDetails(); actions.Children.Add(_details);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(10, 0, 10, 0) });
        _continue.Style = (Style)FindResource("Primary"); _continue.Click += (_, _) => Accept(); buttons.Children.Add(_continue);
        Grid.SetColumn(buttons, 1); actions.Children.Add(buttons); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); outer.Children.Add(footer);
        Content = outer; _activities.SelectedIndex = 0; Update(); Appearance.Attach(this);
    }

    private static FrameworkElementFactory Text(string property, bool bold = false, int lines = 2)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.MaxHeightProperty, lines * 18d); text.SetValue(TextBlock.FontSizeProperty, 12d);
        if (bold) text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        return text;
    }
    private static DataTemplate TextTemplate(string title, string? detail)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.AppendChild(Text(title, bold: detail is not null));
        if (detail is not null)
        {
            var text = Text(detail, lines: 3); text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
            text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(detail)); panel.AppendChild(text);
        }
        return new DataTemplate { VisualTree = panel };
    }
    private static FrameworkElementFactory Choice(string items, string selection, string accessibleName, bool enabledForNew)
    {
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items));
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(selection) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        combo.SetBinding(FrameworkElement.ToolTipProperty, new Binding(selection + ".Tooltip"));
        combo.SetValue(FrameworkElement.MinHeightProperty, 34d); combo.SetValue(Control.PaddingProperty, new Thickness(8, 6, 8, 6));
        combo.SetValue(Control.FontSizeProperty, 12d); combo.SetValue(ComboBox.MaxDropDownHeightProperty, 280d);
        combo.SetValue(AutomationProperties.NameProperty, accessibleName);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(EmailMappingChoice.Label)));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); label.SetValue(TextBlock.MaxWidthProperty, 430d);
        label.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(EmailMappingChoice.Tooltip)));
        combo.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = label });
        if (enabledForNew) combo.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(EmailMappingRow.CanEditNew)));
        return combo;
    }
    private void Update()
    {
        if (_mappings is null) return;
        var count = _mappings.Count(m => m.IsResolved);
        _status.Text = $"{count} of {_mappings.Count} activities matched. Review hours and billing next, then confirm before writing to Quickbase.";
        _continue.IsEnabled = _mappings.Count > 0 && count == _mappings.Count;
    }
    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EmailMappingRow map } || !map.CanEditNew) return;
        _activities.SelectedItem = map;
        var picker = new AssignmentSearchWindow(this, Session.Reference.Assignments,
            (query, token) => _api is null ? System.Threading.Tasks.Task.FromResult(AssignmentSearchWindow.Filter(Session.Reference.Assignments, query)) : _api.FindAssignmentsAsync(query, token),
            initialQuery: map.Activity.MatterHint);
        if (picker.ShowDialog() == true && picker.SelectedAssignment is AssignmentRecord selected) ApplyAssignment(map, selected);
    }
    private void ApplyAssignment(EmailMappingRow map, AssignmentRecord selected)
    {
        Session = Session with { Reference = Session.Reference with { Assignments = Session.Reference.Assignments.Where(a => a.Id != selected.Id).Append(selected).ToList() } };
        foreach (var row in _mappings) row.RefreshChoices(Session);
        map.AssignmentSelection = map.AssignmentChoices.Single(c => c.Id == selected.Id);
        Update();
    }
    private void ShowDetails()
    {
        if (_activities.SelectedItem is not EmailMappingRow map) return;
        var a = map.Activity;
        var text = $"{a.Date:dddd, MMMM d, yyyy} · {a.Minutes} minutes\n{(a.TimeBasis == "measured" ? "Measured time" : "Estimated time")}\n\n{a.Description}\n\nMatter: {a.MatterHint}\nAssignment hint: {a.AssignmentHint}\nTask hint: {a.TaskHint}\nBilling requested: {(a.Billable is null ? "Quickbase default" : a.Billable.Value ? "Billable" : "Nonbillable")}\n\n{map.State}: {map.Reason}";
        if (map.Existing is int id)
        {
            var existing = Session.Days.Single(d => d.Date == a.Date).Existing.Single(r => r.RecordId == id);
            text += "\n\nExisting Quickbase record:\n" + map.ExistingChoices.Single(c => c.Id == existing.RecordId).Tooltip;
        }
        if (a.EvidenceSummary.Length > 0) text += "\n\nEvidence reviewed: " + a.EvidenceSummary;
        if (a.ReviewNotes.Length > 0) text += "\n\nNeeds attention: " + a.ReviewNotes;
        new TextDialog(this, "Email activity", text).ShowDialog();
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
        if (_mappings.Count == 0 || !_continue.IsEnabled || _mappings.Any(m => m.Existing.HasValue)) throw new InvalidOperationException("Email suggestions did not prefill matching new work.");
        if (_activities.Columns.Count != 6)
            throw new InvalidOperationException("Email table structure or approval controls are incorrect.");
        var originalSession = Session; var originals = _mappings.Select(m => m.Capture()).ToArray();
        var first = _mappings[0];
        try
        {
            first.AssignmentSelection = first.AssignmentChoices[0];
            if (_continue.IsEnabled) throw new InvalidOperationException("An unmatched assignment could continue.");
            first.Restore(originals[0]);
            first.TaskSelection = first.TaskChoices[0];
            if (_continue.IsEnabled) throw new InvalidOperationException("An unmatched task could continue.");
            first.Restore(originals[0]);
            var manualTask = first.TaskChoices.First(c => c.Id.HasValue && c.Id != first.Task);
            first.TaskSelection = manualTask;
            var added = new AssignmentRecord(987654, "Smoke assignment", 987653, "Smoke project", "Smoke client", "Active", "Do not display this description in the dropdown");
            ApplyAssignment(first, added);
            if (first.Assignment != added.Id || first.Task != manualTask.Id || _mappings.Skip(1).Where((m, i) => m.Capture() != originals[i + 1]).Any())
                throw new InvalidOperationException("Assignment lookup changed manual tasks or another row.");
            foreach (var row in _mappings) row.RefreshChoices(Session);
            if (first.Assignment != added.Id || first.Task != manualTask.Id || first.AssignmentChoices.Any(c => c.Label.Contains(added.Notes, StringComparison.Ordinal)))
                throw new InvalidOperationException("Assignment refresh lost selections or showed full descriptions.");
            var synthetic = new ExistingTimecard(987652, first.Activity.Date, 1m, added.ProjectId, manualTask.Id!.Value, Session.Reference.Tasks.Single(t => t.Id == manualTask.Id).CategoryId, added.Id, "Synthetic existing timecard");
            Session = Session with { Days = Session.Days.Select(d => d.Date == synthetic.Date ? d with { Existing = d.Existing.Append(synthetic).ToList() } : d).ToList() };
            foreach (var row in _mappings) row.RefreshChoices(Session);
            first.ExistingSelection = first.ExistingChoices.Single(c => c.Id == synthetic.RecordId);
            var linked = BuildProposal();
            if (first.CanEditNew || !_continue.IsEnabled || linked.EmailAlreadyRecorded.Single().ExistingRecordId != synthetic.RecordId || linked.Rows.Any(r => r.SourceActivityIds.Contains(first.Activity.ActivityId)))
                throw new InvalidOperationException("Existing record mapping did not skip new submission.");
            if (!EmailRules.Validate(Session, linked).IsValid) throw new InvalidOperationException("Existing record mapping failed email validation.");
            first.ExistingSelection = first.ExistingChoices[0];
            if (!first.CanEditNew || first.Assignment != added.Id || first.Task != manualTask.Id) throw new InvalidOperationException("Returning to new work lost manual choices.");
        }
        finally
        {
            Session = originalSession;
            for (var i = 0; i < _mappings.Count; i++) { _mappings[i].RefreshChoices(Session); _mappings[i].Restore(originals[i]); }
        }
        Proposal = BuildProposal();
        if (!EmailRules.Validate(Session, Proposal).IsValid || !_continue.IsEnabled) throw new InvalidOperationException("Suggested email proposal was invalid.");
        if (Proposal.Rows.Any(row => row.BillableOverride != _mappings.Single(m => row.SourceActivityIds.Contains(m.Activity.ActivityId)).Activity.Billable))
            throw new InvalidOperationException("Email matching changed requested billing.");
        return "PASS: email table prefills suggestions, gates unmatched rows, preserves manual choices and billing, and links existing records without an opening checkbox\n";
    }
    internal string SmokeRenderedSummary()
    {
        _activities.UpdateLayout();
        var combos = VisualDescendants(_activities).OfType<ComboBox>().Where(c => c.DataContext == _mappings[0]).ToList();
        if (combos.Count != 3 || VisualDescendants(this).OfType<CheckBox>().Any()) throw new InvalidOperationException("The rendered email row does not expose all three dropdowns.");
        var task = combos.Single(c => AutomationProperties.GetName(c) == "Task");
        var original = _mappings[0].Capture();
        try
        {
            task.SelectedIndex = 0; task.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)!.UpdateSource();
            if (_mappings[0].Task.HasValue || _continue.IsEnabled) throw new InvalidOperationException("Rendered task dropdown did not update the row gate.");
            task.SelectedItem = _mappings[0].TaskChoices.First(c => c.Id.HasValue && c.Id != original.Task);
            task.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)!.UpdateSource();
            var proposal = BuildProposal();
            if (!_continue.IsEnabled || proposal.Rows.Single(r => r.SourceActivityIds.Contains(_mappings[0].Activity.ActivityId)).Task != ((EmailMappingChoice)task.SelectedItem).Id)
                throw new InvalidOperationException("Rendered task dropdown did not update the proposal.");
        }
        finally { _mappings[0].Restore(original); Proposal = BuildProposal(); }
        return "PASS: rendered email dropdown changes update the proposal and unmatched-row gate\n";
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
