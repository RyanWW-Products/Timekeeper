using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Globalization;
using Timekeeper.Core;
using ValidationResult = Timekeeper.Core.ValidationResult;

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
    private readonly Button _continue = new() { Content = "Review in Timekeeper dashboard" };
    private readonly Button _submit = new() { Content = "Submit directly to Quickbase" };
    private readonly CheckBox _fill = new() { Content = "Fill weekdays to", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _target = new() { Width = 65, Margin = new Thickness(8, 0, 6, 0) };
    private readonly CheckBox _timecards = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0) };
    private readonly Button _preview = new() { Content = "Show submission preview", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ScrollViewer _previewBody = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private bool _previewOpen;
    private readonly DataGrid _previewRows = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, MaxHeight = 160, MinHeight = 70 };
    private readonly TextBlock _totals = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) };
    private readonly TextBlock _warnings = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly CheckBox _approve = new() { Content = new TextBlock { Text = "I reviewed the timecards, hours, billing and warnings, and checked for existing time.", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 10, 0, 6) };
    private ValidationResult? _review;
    private bool _finding;
    private readonly Button _details = new() { Content = "View selected activity", HorizontalAlignment = HorizontalAlignment.Left };
    public ReadSession Session { get; private set; }
    public ProposalEnvelope Proposal { get; private set; } = new();
    public EmailReviewEdits ReviewEdits { get; private set; } = new();
    public bool SubmitDirectly { get; private set; }
    public IReadOnlyList<VerifiedRow> ReviewedRows => _review?.Rows ?? [];

    public EmailImportWindow(Window owner, ReadSession session, TimecardApi? api, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Session = session; _api = api;
        if (session.EmailWorkbook is null || session.Reference.InternalProject is null)
            throw new InvalidOperationException("The email workbook or configured internal project is unavailable. Check the internal project in Settings and import again.");
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Review email activities"; Width = Math.Min(1480, SystemParameters.WorkArea.Width); Height = 860;
        MinWidth = Math.Min(960, SystemParameters.WorkArea.Width); MaxWidth = SystemParameters.WorkArea.Width; MinHeight = 640;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var policy = EmailReviewEdits.FromSettings(session.Settings);
        _fill.IsChecked = policy.FillWeekdays; _target.Text = policy.TargetHours.ToString("0.00", CultureInfo.CurrentCulture);
        _timecards.IsChecked = policy.AddTimecards; _timecards.Content = $"Add Timecards ({session.Settings.TimecardsHours:0.00} h)";
        _mappings = session.EmailWorkbook.Activities.Where(a => session.Days.Any(d => d.Date == a.Date))
            .Select(a => new EmailMappingRow(a, session, Update)).ToList();

        var outer = new Grid { Margin = new Thickness(24) };
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto }); outer.RowDefinitions.Add(new()); outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "EMAIL / QUICKBASE", Style = (Style)FindResource("SectionLabel") });
        var title = new TextBlock { Text = "Review email activities", FontSize = 30, Margin = new Thickness(0, 7, 0, 8) };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = "Check suggested matches. Double-click hours to edit (0.25 h = 15 minutes). Billing keeps the same assignment and task. Review the submission preview for grouped hours and additions.", TextWrapping = TextWrapping.Wrap });
        var account = new TextBlock { Text = $"{session.EmailWorkbook.Metadata.EmployeeEmail} · {session.EmailWorkbook.Metadata.TimeZone}", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        account.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); header.Children.Add(account); outer.Children.Add(header);

        _activities.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _activities.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_activities, "Email activity assignment and task matching table");
        var cellStyle = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 8, 7, 8)));
        _activities.CellStyle = cellStyle;
        var duration = new FrameworkElementFactory(typeof(StackPanel)); duration.AppendChild(Text(nameof(EmailMappingRow.WorkDate)));
        var hours = new FrameworkElementFactory(typeof(TextBox));
        hours.SetBinding(TextBox.TextProperty, new Binding(nameof(EmailMappingRow.HoursLabel)) { Mode = BindingMode.OneWay });
        hours.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(EmailMappingRow.HoursHint)));
        hours.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(EmailMappingRow.CanEditNew)));
        hours.SetValue(TextBox.IsReadOnlyProperty, true); hours.SetValue(Control.PaddingProperty, new Thickness(6)); hours.SetValue(AutomationProperties.NameProperty, "Activity decimal hours; double-click or F2 to edit");
        hours.AddHandler(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(Hours_DoubleClick)); hours.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Hours_KeyDown)); duration.AppendChild(hours);
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Date / hours", Width = 102, MinWidth = 102, CellTemplate = new DataTemplate { VisualTree = duration } });
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
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Billing", MinWidth = 178, CellTemplate = BillingTemplate() });
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Quickbase record", Width = new DataGridLength(0.9, DataGridLengthUnitType.Star), MinWidth = 165, CellTemplate = new DataTemplate { VisualTree = Choice(nameof(EmailMappingRow.ExistingChoices), nameof(EmailMappingRow.ExistingSelection), "New work or existing Quickbase record", false) } });
        _activities.Columns.Add(new DataGridTemplateColumn { Header = "Match", Width = new DataGridLength(0.8, DataGridLengthUnitType.Star), MinWidth = 115, CellTemplate = TextTemplate(nameof(EmailMappingRow.State), nameof(EmailMappingRow.Reason)) });
        _activities.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            // Explicit logical widths prevent WPF star columns from keeping their previous
            // widths when interface scale or the window size changes. Below the combined
            // minimum, the table scrolls horizontally instead of compressing the controls.
            var minimum = _activities.Columns.Select(column => column.MinWidth).ToArray();
            var extra = Math.Max(0, e.NewSize.Width - 20 - minimum.Sum());
            double[] weights = [0, 0.24, 0.30, 0.12, 0.12, 0.12, 0.10];
            for (var i = 0; i < minimum.Length; i++) _activities.Columns[i].Width = minimum[i] + extra * weights[i];
        };
        _activities.ItemsSource = _mappings;
        _activities.SelectionChanged += (_, _) => _details.IsEnabled = _activities.SelectedItem is EmailMappingRow;
        Grid.SetRow(_activities, 1); outer.Children.Add(_activities);
        Grid.SetRow(_previewBody, 1); outer.Children.Add(_previewBody);

        var footer = new StackPanel();
        var policyPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        policyPanel.Children.Add(_fill); policyPanel.Children.Add(_target); policyPanel.Children.Add(new TextBlock { Text = "hours", VerticalAlignment = VerticalAlignment.Center }); policyPanel.Children.Add(_timecards);
        _details.Margin = new Thickness(22, 0, 0, 0); _details.Click += (_, _) => ShowDetails(); policyPanel.Children.Add(_details); footer.Children.Add(policyPanel);
        foreach (var box in new[] { _fill, _timecards }) { box.Checked += (_, _) => Update(); box.Unchecked += (_, _) => Update(); }
        _target.TextChanged += (_, _) => Update();
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new Binding("Date") { StringFormat = "yyyy-MM-dd" }, Width = 100 });
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Hours", Binding = new Binding("Hours") { StringFormat = "0.00" }, Width = 75 });
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Project", Binding = new Binding("Project"), Width = 190 });
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Assignment", Binding = new Binding("Assignment"), Width = 170 });
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Task", Binding = new Binding("Task"), Width = 120 });
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Billing", Binding = new Binding("Billing"), Width = 150 });
        _previewRows.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new Binding("Description"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 230 });
        _warnings.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        var previewContent = new StackPanel(); previewContent.Children.Add(_previewRows); previewContent.Children.Add(_totals); previewContent.Children.Add(_warnings); previewContent.Children.Add(_approve);
        _previewBody.Content = previewContent; _preview.Click += (_, _) => ShowPreview(!_previewOpen); footer.Children.Add(_preview); footer.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(10, 0, 10, 0) });
        _continue.Margin = new Thickness(0, 0, 10, 0); _continue.Click += (_, _) => Accept(false); buttons.Children.Add(_continue);
        _submit.Style = (Style)FindResource("Primary"); _submit.Click += (_, _) => Accept(true); buttons.Children.Add(_submit); footer.Children.Add(buttons);
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
    private DataTemplate BillingTemplate()
    {
        Style Visible(Type type, string property, bool value)
        {
            var style = new Style(type, (Style)FindResource(type)); style.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            var trigger = new DataTrigger { Binding = new Binding(property), Value = value }; trigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible)); style.Triggers.Add(trigger); return style;
        }
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var line = new FrameworkElementFactory(typeof(StackPanel)); line.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var light = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse)); light.SetValue(FrameworkElement.WidthProperty, 9d); light.SetValue(FrameworkElement.HeightProperty, 9d); light.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 5)); light.SetBinding(System.Windows.Shapes.Shape.FillProperty, new Binding(nameof(EmailMappingRow.BillingLight))); light.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(EmailMappingRow.BillingLabel))); line.AppendChild(light);
        var label = Text(nameof(EmailMappingRow.BillingLabel)); label.SetValue(TextBlock.MaxWidthProperty, 145d); label.SetValue(FrameworkElement.StyleProperty, Visible(typeof(TextBlock), nameof(EmailMappingRow.CanOverride), false)); line.AppendChild(label); panel.AppendChild(line);
        var combo = new FrameworkElementFactory(typeof(ComboBox)); combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(EmailMappingRow.BillingChoices))); combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, new Binding(nameof(EmailMappingRow.BillingIndex)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var choiceLabel = new FrameworkElementFactory(typeof(TextBlock)); choiceLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(EmailBillingChoice.Label)));
        combo.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = choiceLabel });
        combo.SetValue(FrameworkElement.StyleProperty, Visible(typeof(ComboBox), nameof(EmailMappingRow.CanOverride), true)); combo.SetValue(AutomationProperties.NameProperty, "Activity billing status"); combo.SetValue(Control.FontSizeProperty, 12d); combo.SetValue(Control.PaddingProperty, new Thickness(6)); panel.AppendChild(combo);
        var clear = new FrameworkElementFactory(typeof(Button)); clear.SetValue(ContentControl.ContentProperty, "Use default"); clear.SetValue(Control.PaddingProperty, new Thickness(6, 4, 6, 4)); clear.SetValue(Control.FontSizeProperty, 11d);
        clear.SetValue(FrameworkElement.StyleProperty, Visible(typeof(Button), nameof(EmailMappingRow.HasUnavailableOverride), true));
        clear.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (sender is FrameworkElement { DataContext: EmailMappingRow row }) row.BillingIndex = 0; })); panel.AppendChild(clear);
        return new DataTemplate { VisualTree = panel };
    }
    private void Hours_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; if (sender is FrameworkElement { DataContext: EmailMappingRow row }) EditHours(row);
    }
    private void Hours_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return; e.Handled = true; if (sender is FrameworkElement { DataContext: EmailMappingRow row }) EditHours(row);
    }
    private void EditHours(EmailMappingRow row)
    {
        if (!row.CanEditNew || _finding) return;
        var dialog = new DurationEditWindow(this, new VerifiedRow { Date = row.Activity.Date, Hours = row.Hours, Description = row.Activity.Description, TaskName = row.TaskSelection?.Label ?? row.Activity.TaskHint }, emailActivity: true);
        if (dialog.ShowDialog() == true) row.SetHours(dialog.ResetRequested ? null : dialog.Hours);
    }
    private EmailReviewEdits BuildEdits(decimal target) => new()
    {
        FillWeekdays = _fill.IsChecked == true, TargetHours = target, AddTimecards = _timecards.IsChecked == true,
        ActivityHours = _mappings.Where(m => m.CanEditNew && m.EditedHours.HasValue).Select(m => new EmailActivityHours(m.Activity.ActivityId, m.Activity.Minutes, m.EditedHours!.Value)).ToList()
    };
    private void Update()
    {
        if (_mappings is null) return;
        _approve.IsChecked = false;
        _target.IsEnabled = _fill.IsChecked == true;
        var count = _mappings.Count(m => m.IsResolved);
        var validTarget = decimal.TryParse(_target.Text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var target) && target is >= 0 and <= 24 && decimal.Round(target, 2) == target;
        if (!validTarget && _fill.IsChecked != true) { target = Session.Settings.TargetHours; validTarget = true; }
        _review = null;
        if (_mappings.Count > 0 && count == _mappings.Count && validTarget)
        {
            ReviewEdits = BuildEdits(target); _review = EmailRules.Validate(Session, BuildProposal(), emailEdits: ReviewEdits);
        }
        _continue.IsEnabled = !_finding && _review is { IsValid: true };
        _submit.IsEnabled = _continue.IsEnabled && _review!.Rows.Count > 0 && !_mappings.Any(m => m.HasUnavailableOverride) && !Session.Demo;
        _previewRows.ItemsSource = _review?.Rows.Select(r => new { r.Date, r.Hours, Project = r.ProjectName, Assignment = r.AssignmentName, Task = r.TaskName, Billing = (r.BillableOverride ?? Session.Reference.Billing?.DefaultFor(r.Project, r.Task)) switch { true => "Billable", false => "Nonbillable", _ => "Default unavailable" }, r.Description }).ToList();
        UpdatePreviewLabel();
        _totals.Text = _review is { IsValid: true } ? string.Join("\n", Session.Days.Select(d => $"{d.Date:MMM d}: {d.Existing.Sum(r => r.Hours):0.00} h existing + {_review.Rows.Where(r => r.Date == d.Date).Sum(r => r.Hours):0.00} h new = {d.Existing.Sum(r => r.Hours) + _review.Rows.Where(r => r.Date == d.Date).Sum(r => r.Hours):0.00} h total")) : "";
        _warnings.Text = _review is null ? "Complete the mappings and enter a valid fill target to preview timecards." : string.Join("\n\n", _review.Errors.Concat(_review.Warnings));
        _approve.IsEnabled = _review is { IsValid: true } && _review.Rows.Count > 0;
        _status.Text = !validTarget ? "Enter a weekday target from 0 to 24 hours, with up to two decimal places."
            : _review is { IsValid: false } ? string.Join("\n", _review.Errors)
            : $"{count} of {_mappings.Count} activities matched. Settings above apply only to this import. " + (Session.Demo ? "Demo: submission is disabled." : _mappings.Any(m => m.HasUnavailableOverride) ? "Billing override access is unavailable. Use default or review in the dashboard." : "Open the preview and confirm to submit directly.");
    }
    private void UpdatePreviewLabel() => _preview.Content = _previewOpen ? "Back to activity table" : _review is { IsValid: true } ? $"Show submission preview · {_review.Rows.Count} timecards · {_review.Rows.Sum(r => r.Hours):0.00} h new" : "Show submission preview";
    private void ShowPreview(bool show)
    {
        _previewOpen = show; _activities.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _previewBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed; UpdatePreviewLabel();
    }
    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EmailMappingRow map } || !map.CanEditNew) return;
        _activities.SelectedItem = map;
        var picker = new AssignmentSearchWindow(this, Session.Reference.Assignments,
            (query, token) => _api is null ? System.Threading.Tasks.Task.FromResult(AssignmentSearchWindow.Filter(Session.Reference.Assignments, query)) : _api.FindAssignmentsAsync(query, token),
            initialQuery: map.Activity.MatterHint);
        if (picker.ShowDialog() == true && picker.SelectedAssignment is AssignmentRecord selected)
        {
            _finding = true; _activities.IsEnabled = false; Update();
            try
            {
                if (_api is not null)
                {
                    var billing = await _api.ReadBillingDefaultsAsync(Session.Reference.Assignments.Select(a => a.ProjectId).Append(selected.ProjectId).Append(Session.Reference.InternalProject!.Id));
                    Session = Session with { Reference = Session.Reference with { Billing = billing } };
                }
                ApplyAssignment(map, selected);
            }
            catch (Exception error) { new TextDialog(this, "Assignment lookup", error.Message).ShowDialog(); }
            finally { _finding = false; _activities.IsEnabled = true; Update(); }
        }
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
        var text = $"{a.Date:dddd, MMMM d, yyyy} · {map.Hours:0.00} h\nImported: {a.Minutes / 60m:0.00} h · {(a.TimeBasis == "measured" ? "Measured time" : "Estimated time")}\n\n{a.Description}\n\nMatter: {a.MatterHint}\nAssignment hint: {a.AssignmentHint}\nTask hint: {a.TaskHint}\nBilling: {map.BillingLabel}\n\n{map.State}: {map.Reason}";
        if (map.Existing is int id)
        {
            var existing = Session.Days.Single(d => d.Date == a.Date).Existing.Single(r => r.RecordId == id);
            text += "\n\nExisting Quickbase record:\n" + map.ExistingChoices.Single(c => c.Id == existing.RecordId).Tooltip;
        }
        if (a.EvidenceSummary.Length > 0) text += "\n\nEvidence reviewed: " + a.EvidenceSummary;
        if (a.ReviewNotes.Length > 0) text += "\n\nNeeds attention: " + a.ReviewNotes;
        new TextDialog(this, "Email activity", text).ShowDialog();
    }
    private void Accept(bool direct)
    {
        if (!_continue.IsEnabled) return;
        if (direct && !_submit.IsEnabled) return;
        if (direct && _approve.IsChecked != true)
        {
            ShowPreview(true); _status.Text = "Review the final timecards and warnings in the preview, check the confirmation, then choose Submit directly to Quickbase."; return;
        }
        Proposal = BuildProposal();
        var result = EmailRules.Validate(Session, Proposal, emailEdits: ReviewEdits);
        if (!result.IsValid) { _status.Text = string.Join("\n", result.Errors); return; }
        SubmitDirectly = direct;
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
            rows.Add(new() { Date = map.Activity.Date, SourceActivityIds = [map.Activity.ActivityId], Project = assignment?.ProjectId ?? Session.Reference.InternalProject!.Id, Assignment = assignment?.Id, Task = task.Id, Category = task.CategoryId, Description = map.Activity.Description, BillableOverride = map.BillableOverride });
        }
        return new() { SessionId = Session.SessionId, EmployeeId = Session.Settings.EmployeeId, Rows = rows, EmailAlreadyRecorded = recorded };
    }
    internal string SmokeSummary()
    {
        if (_mappings.Count == 0 || !_continue.IsEnabled || _mappings.Any(m => m.Existing.HasValue)) throw new InvalidOperationException("Email suggestions did not prefill matching new work.");
        if (_activities.Columns.Count != 7)
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
        if (combos.Count != 4 || _approve.IsChecked == true) throw new InvalidOperationException("The rendered email row does not expose its mapping/billing dropdowns or was approved automatically.");
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
    internal string SmokeEditingSummary()
    {
        var originalSession = Session; var originals = _mappings.Select(m => m.Capture()).ToArray();
        var fill = _fill.IsChecked; var target = _target.Text; var timecards = _timecards.IsChecked;
        var first = _mappings[0];
        try
        {
            var quarter = new EmailMappingRow(first.Activity with { Minutes = 15 }, Session, () => { });
            if (quarter.Hours != .25m || quarter.HoursLabel != $"{.25m:0.00} h") throw new InvalidOperationException("Fifteen minutes did not display as decimal hours.");
            if (VisualDescendants(_activities).OfType<TextBox>().Where(t => t.DataContext is EmailMappingRow).Any(t => !t.IsReadOnly))
                throw new InvalidOperationException("Email hours could be typed into without the edit dialog.");
            _approve.IsChecked = true; first.SetHours(.25m);
            if (_approve.IsChecked == true || ReviewEdits.ActivityHours.Single().Hours != .25m || !first.HoursLabel.EndsWith(" *"))
                throw new InvalidOperationException("Email hours did not update the preview and reset confirmation.");
            var assignment = first.Assignment; var task = first.Task;
            var billing = VisualDescendants(_activities).OfType<ComboBox>().Single(c => c.DataContext == first && AutomationProperties.GetName(c) == "Activity billing status");
            _approve.IsChecked = true; billing.SelectedIndex = 2; billing.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty)!.UpdateSource();
            if (first.BillableOverride != false || first.Assignment != assignment || first.Task != task || _approve.IsChecked == true || !_review!.Rows.Any(r => r.SourceActivityIds.Contains(first.Activity.ActivityId) && r.BillableOverride == false))
                throw new InvalidOperationException("Billing dropdown did not preserve mappings, update rows or reset confirmation.");
            _fill.IsChecked = true; _timecards.IsChecked = true; _target.Text = "8";
            if (_review is not { IsValid: true } || _review.Rows.Sum(r => r.Hours) != 8m || !_review.Rows.Any(r => r.Kind == "misc_internal"))
                throw new InvalidOperationException("Table fill did not recalculate the submission preview.");
            _approve.IsChecked = true; _target.Text = "7";
            if (_review!.Rows.Sum(r => r.Hours) != 7m || _approve.IsChecked == true) throw new InvalidOperationException("Changing the target kept an old preview or approval.");
            _target.Text = "invalid";
            if (_continue.IsEnabled || _submit.IsEnabled || _approve.IsEnabled) throw new InvalidOperationException("An invalid fill target could continue.");
            _target.Text = "8";
            Session = Session with { Demo = false }; Update();
            if (!_submit.IsEnabled) throw new InvalidOperationException("Valid email review did not enable the direct-submit preview action.");
            Accept(true);
            if (!_previewOpen || _approve.IsChecked == true || SubmitDirectly || DialogResult.HasValue)
                throw new InvalidOperationException("Direct submission bypassed final preview confirmation.");
            ShowPreview(false);
            Session = Session with { Reference = Session.Reference with { Billing = new BillingCapabilities { CanOverride = false, ProjectTypes = new() { [100] = "Client" }, TaskBilling = new() { [task!.Value] = false } } } };
            foreach (var row in _mappings) row.RefreshChoices(Session); Update(); _activities.UpdateLayout();
            if (billing.Visibility != Visibility.Collapsed || _submit.IsEnabled || !first.HasUnavailableOverride)
                throw new InvalidOperationException("Restricted billing exposed a dropdown or allowed an override submission.");
            foreach (var row in _mappings) row.BillingIndex = 0;
            if (first.DefaultBillingLabel != "Nonbillable - Default" || !_submit.IsEnabled || _mappings.Any(m => m.CanOverride))
                throw new InvalidOperationException("Clearing a restricted override did not restore its actual default.");
            first.SetHours(null);
            if (ReviewEdits.ActivityHours.Count != 0 || first.EditedHours.HasValue) throw new InvalidOperationException("Use imported did not restore source duration and rounding.");
        }
        finally
        {
            Session = originalSession; _fill.IsChecked = fill; _target.Text = target; _timecards.IsChecked = timecards;
            for (var i = 0; i < _mappings.Count; i++) { _mappings[i].RefreshChoices(Session); _mappings[i].Restore(originals[i]); }
            ShowPreview(false); Update(); Proposal = BuildProposal();
        }
        _activities.UpdateLayout();
        foreach (var combo in VisualDescendants(_activities).OfType<ComboBox>().Where(c => AutomationProperties.GetName(c) == "Activity billing status"))
            if (combo.DataContext is EmailMappingRow row && (combo.SelectedIndex != row.BillingIndex || combo.SelectedItem is not EmailBillingChoice
                || !VisualDescendants(combo).OfType<TextBlock>().Any(t => t.Text == row.BillingLabel)))
                throw new InvalidOperationException("Billing selections disappeared or failed to render their labels after table changes.");
        return "PASS: decimal email hours, protected editors, billing permissions, target recalculation and direct-submit confirmation gates\n";
    }
    internal void PreviewForSmoke()
    {
        _mappings[0].SetHours(.25m); _fill.IsChecked = true; _timecards.IsChecked = true; _target.Text = "8"; ShowPreview(true);
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
