using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class AssignmentSearchWindow : Window
{
    private readonly Func<string, CancellationToken, Task<List<AssignmentRecord>>> _search;
    private readonly Func<string, CancellationToken, Task<List<ProjectRecord>>>? _searchProjects;
    private readonly IReadOnlyCollection<AssignmentRecord> _available;
    private readonly IReadOnlyCollection<ProjectRecord> _projects;
    private readonly Button _assignmentMode = new() { Content = "Assignments", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _projectMode = new() { Content = "Direct to project" };
    private readonly TextBlock _modeHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 9, 0, 14) };
    private readonly bool _forExport;
    private bool _directProject;
    private readonly TextBox _query = new() { MaxLength = 150 };
    private readonly Button _searchButton = new() { Content = "Search", Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _choose = new() { IsEnabled = false };
    private readonly DataGrid _results = new() { SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, IsReadOnly = true, AutoGenerateColumns = false, CanUserResizeColumns = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), FontSize = 12 };
    private readonly TextBlock _selection = new() { Text = "Select an assignment to continue.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
    private readonly TextBlock _empty = new() { Text = "No assignments to show. Search by assignment, project or client.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380, Margin = new Thickness(24), IsHitTestVisible = false };
    private CancellationTokenSource? _request;
    private bool _closed, _searching;
    public AssignmentRecord? SelectedAssignment { get; private set; }
    public ProjectRecord? SelectedProject { get; private set; }

    public AssignmentSearchWindow(Window owner, IReadOnlyCollection<AssignmentRecord> available,
        Func<string, CancellationToken, Task<List<AssignmentRecord>>> search,
        string initialQuery = "", bool forExport = false, bool smoke = false,
        IReadOnlyCollection<ProjectRecord>? availableProjects = null,
        Func<string, CancellationToken, Task<List<ProjectRecord>>>? searchProjects = null)
    {
        if (!smoke) Owner = owner;
        _search = search; _searchProjects = searchProjects; _available = available; _projects = availableProjects ?? []; _forExport = forExport;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Find assignment or project"; Width = 1060; Height = 740; MinWidth = 760; MinHeight = 600;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var outer = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto }); outer.RowDefinitions.Add(new()); outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "QUICKBASE / LOOKUP", Style = (Style)FindResource("SectionLabel") });
        header.Children.Add(new TextBlock { Text = "Find assignment or project", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 30, Margin = new Thickness(0, 8, 0, 12) });
        var modes = new WrapPanel(); modes.Children.Add(_assignmentMode); modes.Children.Add(_projectMode); header.Children.Add(modes);
        _modeHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); header.Children.Add(_modeHint);
        _projectMode.ToolTip = "Put work directly on a case or project, with no assignment. Choose its task and billing in review.";
        _projectMode.IsEnabled = searchProjects is not null;
        _assignmentMode.Click += async (_, _) => { SetMode(false); if (_query.Text.Trim().Length > 0) await SearchAsync(); };
        _projectMode.Click += async (_, _) => { SetMode(true); if (_query.Text.Trim().Length > 0) await SearchAsync(); };
        var searchRow = new Grid(); searchRow.ColumnDefinitions.Add(new()); searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _query.Text = initialQuery; _query.ToolTip = "Assignment, project or client name";
        AutomationProperties.SetName(_query, "Search assignments by assignment, project or client");
        _searchButton.Style = (Style)FindResource("Primary"); searchRow.Children.Add(_query); Grid.SetColumn(_searchButton, 1); searchRow.Children.Add(_searchButton);
        header.Children.Add(searchRow); header.Children.Add(_status); outer.Children.Add(header);
        var cell = new Style(typeof(TextBlock));
        cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cell.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        // Deliberately omit Notes and descriptions, including tooltips and row details.
        AddColumn("Assignment", nameof(AssignmentRecord.Name), 1.2, 120);
        AddColumn("Project", nameof(AssignmentRecord.ProjectName), 1.4, 130);
        AddColumn("Client", nameof(AssignmentRecord.Client), 0.9, 80);
        AddColumn("Status", nameof(AssignmentRecord.Status), 0.65, 80);
        _results.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(AssignmentRecord.Id)), Width = 65, MinWidth = 65, ElementStyle = cell });
        _results.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            SizeColumns(e.NewSize.Width);
        };
        AutomationProperties.SetName(_results, "Assignment search results");
        _results.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        var resultArea = new Grid(); resultArea.Children.Add(_results); resultArea.Children.Add(_empty);
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Child = resultArea, ClipToBounds = true };
        Grid.SetRow(card, 1); outer.Children.Add(card);
        var footer = new StackPanel(); footer.Children.Add(_selection);
        var hint = new TextBlock { Text = forExport
            ? "After adding, you must drag the updated file into Copilot AGAIN and request a NEW proposal. Your previous proposal will no longer work."
            : "Uses the selected assignment or project for this activity. Review its task and billing before continuing.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 14) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(hint);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        _choose.Content = forExport ? "Add assignment to export" : "Use assignment";
        _choose.Style = (Style)FindResource("Primary"); buttons.Children.Add(_choose); footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); outer.Children.Add(footer);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _searchButton.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } };
        _results.SelectionChanged += (_, _) => SelectionChanged();
        _results.MouseDoubleClick += (_, e) => { if (ItemsControl.ContainerFromElement(_results, e.OriginalSource as DependencyObject) is DataGridRow) Choose(); };
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Choose(); } };
        _choose.Click += (_, _) => Choose();
        Closed += (_, _) => { _closed = true; _request?.Cancel(); };
        Loaded += (_, _) => { _query.Focus(); _query.SelectAll(); };
        Content = outer; Appearance.Attach(this);
        SetMode(false);

        void AddColumn(string title, string property, double width, double minimum) => _results.Columns.Add(new DataGridTextColumn
        { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = minimum, ElementStyle = cell });
    }

    private void SizeColumns(double width)
    {
        if (_directProject) { _results.Columns[0].Width = Math.Max(200, width - 110); _results.Columns[4].Width = 90; return; }
        var extra = Math.Max(0, width - 20 - 475);
        double[] minimum = [120,130,80,80,65], weights = [0.32,0.38,0.22,0.08,0];
        for (var i = 0; i < minimum.Length; i++) _results.Columns[i].Width = minimum[i] + extra * weights[i];
    }
    private void SetMode(bool directProject)
    {
        if (_searching || (directProject && _searchProjects is null)) return;
        _directProject = directProject; SelectedAssignment = null; SelectedProject = null;
        _assignmentMode.Style = (Style)FindResource(directProject ? typeof(Button) : "Primary");
        _projectMode.Style = (Style)FindResource(directProject ? "Primary" : typeof(Button));
        _modeHint.Text = directProject ? "Use a case or project directly for admin work. No assignment is required."
            : "Search across employees and statuses, including Active, Case Intake, Proposal and Review.";
        _query.ToolTip = directProject ? "Project or case name, or Quickbase record ID" : "Assignment, project, client or Quickbase record ID";
        AutomationProperties.SetName(_query, directProject ? "Search projects and cases" : "Search assignments by assignment, project or client");
        _results.Columns[0].Header = directProject ? "Project / case" : "Assignment";
        foreach (var i in new[] { 1, 2, 3 }) _results.Columns[i].Visibility = directProject ? Visibility.Collapsed : Visibility.Visible;
        _results.Columns[4].Header = directProject ? "Record ID" : "ID";
        SizeColumns(_results.ActualWidth);
        _choose.Content = directProject ? (_forExport ? "Add project to export" : "Use project directly") : (_forExport ? "Add assignment to export" : "Use assignment");
        _empty.Text = directProject ? "Search Quickbase for the case or project. You do not need to create an assignment." : "No assignments to show. Search by assignment, project or client.";
        if (directProject) Present(_projects, $"{_projects.Count} known projects. Search Quickbase to find more.");
        else Present(_available, $"{_available.Count} available assignments. Search Quickbase to find more.");
    }

    internal static List<AssignmentRecord> Filter(IEnumerable<AssignmentRecord> source, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return source.Where(a => words.All(w => new[] { a.Name, a.ProjectName, a.Client, a.Id.ToString(), a.ProjectId.ToString() }.Any(s => s.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();
    }
    internal static List<ProjectRecord> FilterProjects(IEnumerable<ProjectRecord> source, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return source.Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || p.Id.ToString() == w)).ToList();
    }

    private void Present(IEnumerable<object> found, string status)
    {
        var rows = _directProject ? found.OfType<ProjectRecord>().GroupBy(p => p.Id).Select(g => g.Last()).OrderBy(p => p.Name).Cast<object>().ToList()
            : found.OfType<AssignmentRecord>().GroupBy(a => a.Id).Select(g => g.Last()).OrderBy(a => a.ProjectName).ThenBy(a => a.Name).Cast<object>().ToList();
        _results.ItemsSource = rows; _results.SelectedItem = null;
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = status; SelectionChanged();
    }

    private async Task SearchAsync()
    {
        if (_searching || _closed) return;
        var query = _query.Text.Trim();
        if (query.Length == 0) { Present([], "Enter a name or record ID to search."); _query.Focus(); return; }
        _searching = true; _searchButton.IsEnabled = _query.IsEnabled = _results.IsEnabled = false; _choose.IsEnabled = false;
        _assignmentMode.IsEnabled = _projectMode.IsEnabled = false;
        var kind = _directProject ? "projects" : "assignments";
        Present([], "Searching Quickbase…"); _empty.Text = $"Searching {kind}…";
        using var request = new CancellationTokenSource(); _request = request;
        try
        {
            IReadOnlyCollection<object> found = _directProject ? (await _searchProjects!(query, request.Token)).Cast<object>().ToList() : (await _search(query, request.Token)).Cast<object>().ToList();
            if (_closed) return;
            _empty.Text = _directProject ? "No matching projects. Try fewer words or a case record ID." : "No matching assignments. Try fewer words, or choose Direct to project if the case has no assignment.";
            if (found.Count > 200)
            {
                _empty.Text = "Too many matches. Add a more specific project, client or assignment name.";
                Present([], $"More than 200 {kind} matched. Refine your search.");
            }
            else Present(found, found.Count == 0 ? $"No {kind} found." : $"{found.Count} {kind} found. Select a match below.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) { _empty.Text = "Search could not finish. Check the message above and try again."; Present([], ex.Message); }
        }
        finally
        {
            _request = null; _searching = false;
            if (!_closed) { _searchButton.IsEnabled = _query.IsEnabled = _results.IsEnabled = _assignmentMode.IsEnabled = true; _projectMode.IsEnabled = _searchProjects is not null; SelectionChanged(); }
        }
    }

    private void SelectionChanged()
    {
        _choose.IsEnabled = !_searching && (_directProject ? _results.SelectedItem is ProjectRecord : _results.SelectedItem is AssignmentRecord);
        _selection.Text = _results.SelectedItem switch { AssignmentRecord a => $"Selected: #{a.Id} · {a.Name} · {a.ProjectName}", ProjectRecord p => $"Selected: #{p.Id} · {p.Name} · No assignment", _ => _directProject ? "Select a project to continue." : "Select an assignment to continue." };
        _selection.MaxHeight = 42; _selection.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void Choose()
    {
        if (!_choose.IsEnabled) return;
        SelectedAssignment = _results.SelectedItem as AssignmentRecord; SelectedProject = _results.SelectedItem as ProjectRecord; DialogResult = true;
    }

    internal async Task<string> SmokeSearchAsync()
    {
        if (_choose.IsEnabled) throw new InvalidOperationException("Assignment picker accepted an unselected result.");
        _query.Text = "sample"; await SearchAsync();
        if (_results.Items.Count == 0) throw new InvalidOperationException("Assignment results were not displayed.");
        _results.SelectedIndex = 0;
        if (!_choose.IsEnabled) throw new InvalidOperationException("Assignment selection did not enable the action.");
        if (_results.Columns.OfType<DataGridTextColumn>().Any(c => c.Binding is Binding b && b.Path.Path is "Notes" or "Description")) throw new InvalidOperationException("Assignment descriptions appeared in search results.");
        foreach (var query in new[] { "missing", "fail", "broad" })
        {
            _query.Text = query; await SearchAsync();
            if (_results.Items.Count != 0 || _choose.IsEnabled) throw new InvalidOperationException("An empty or failed search left a stale assignment selectable.");
        }
        _query.Text = "sample"; await SearchAsync(); _results.SelectedIndex = 0;
        return "PASS: assignment picker requires explicit selection, omits descriptions, clears stale results on empty/error/broad searches and allows retry\n";
    }
    internal async Task<string> SmokeProjectSearchAsync()
    {
        SetMode(true);
        if (!_directProject || _choose.IsEnabled || SelectedAssignment is not null || SelectedProject is not null)
            throw new InvalidOperationException("Switching to projects retained an assignment selection.");
        _query.Text = "sample"; await SearchAsync();
        if (_results.Items.Count == 0 || _results.Items[0] is not ProjectRecord) throw new InvalidOperationException("Project results were not displayed.");
        _results.SelectedIndex = 0;
        if (!_choose.IsEnabled || !_selection.Text.Contains("No assignment")) throw new InvalidOperationException("Direct project selection was not explicit.");
        foreach (var query in new[] { "missing", "fail", "broad", "" })
        {
            _query.Text = query; await SearchAsync();
            if (_results.Items.Count != 0 || _choose.IsEnabled) throw new InvalidOperationException("Failed project search left a stale result selectable.");
        }
        SetMode(false);
        if (_choose.IsEnabled || _directProject) throw new InvalidOperationException("Returning to assignments retained a project selection.");
        SetMode(true); _query.Text = "sample"; await SearchAsync(); _results.SelectedIndex = 0;
        return "PASS: direct-project search requires selection, clears stale results on empty/error/broad searches and keeps project/assignment modes separate\n";
    }
}
