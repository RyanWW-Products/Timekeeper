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

    public AssignmentSearchWindow(Window owner, IReadOnlyCollection<AssignmentRecord> available,
        Func<string, CancellationToken, Task<List<AssignmentRecord>>> search,
        string initialQuery = "", bool forExport = false, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        _search = search;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Find assignment"; Width = 1060; Height = 690; MinWidth = 760; MinHeight = 520;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var outer = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto }); outer.RowDefinitions.Add(new()); outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "QUICKBASE / LOOKUP", Style = (Style)FindResource("SectionLabel") });
        header.Children.Add(new TextBlock { Text = "Find assignment", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 30, Margin = new Thickness(0, 8, 0, 8) });
        header.Children.Add(new TextBlock { Text = "Search by assignment, project or client, then select a match.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
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
            // Keep all columns visible as the dialog or interface scale changes.
            var extra = Math.Max(0, e.NewSize.Width - 20 - 475);
            double[] minimum = [120,130,80,80,65], weights = [0.32,0.38,0.22,0.08,0];
            for (var i = 0; i < minimum.Length; i++) _results.Columns[i].Width = minimum[i] + extra * weights[i];
        };
        AutomationProperties.SetName(_results, "Assignment search results");
        _results.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        var resultArea = new Grid(); resultArea.Children.Add(_results); resultArea.Children.Add(_empty);
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Child = resultArea, ClipToBounds = true };
        Grid.SetRow(card, 1); outer.Children.Add(card);
        var footer = new StackPanel(); footer.Children.Add(_selection);
        var hint = new TextBlock { Text = forExport
            ? "After adding, you must drag the updated file into Copilot AGAIN and request a NEW proposal. Your previous proposal will no longer work."
            : "Uses this assignment for the selected activity. Review its task before continuing.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 14) };
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
        Present(available, $"{available.Count} available assignments. Search Quickbase to find more.");

        void AddColumn(string title, string property, double width, double minimum) => _results.Columns.Add(new DataGridTextColumn
        { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = minimum, ElementStyle = cell });
    }

    internal static List<AssignmentRecord> Filter(IEnumerable<AssignmentRecord> source, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return source.Where(a => words.All(w => new[] { a.Name, a.ProjectName, a.Client }.Any(s => s.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    private void Present(IEnumerable<AssignmentRecord> found, string status)
    {
        var rows = found.GroupBy(a => a.Id).Select(g => g.Last()).OrderBy(a => a.ProjectName).ThenBy(a => a.Name).ToList();
        _results.ItemsSource = rows; _results.SelectedItem = null;
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = status; SelectionChanged();
    }

    private async Task SearchAsync()
    {
        if (_searching || _closed) return;
        var query = _query.Text.Trim();
        if (query.Length == 0) { _status.Text = "Enter an assignment, project or client name."; _query.Focus(); return; }
        _searching = true; _searchButton.IsEnabled = _query.IsEnabled = _results.IsEnabled = false; _choose.IsEnabled = false;
        Present([], "Searching Quickbase…"); _empty.Text = "Searching assignments…";
        using var request = new CancellationTokenSource(); _request = request;
        try
        {
            var found = await _search(query, request.Token);
            if (_closed) return;
            _empty.Text = "No matching assignments. Try fewer words or a different project or client name.";
            if (found.Count > 200)
            {
                _empty.Text = "Too many matches. Add a more specific project, client or assignment name.";
                Present([], "More than 200 assignments matched. Refine your search.");
            }
            else Present(found, found.Count == 0 ? "No assignments found." : $"{found.Count} assignment{(found.Count == 1 ? "" : "s")} found. Select a match below.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) { _empty.Text = "Search could not finish. Check the message above and try again."; Present([], ex.Message); }
        }
        finally
        {
            _request = null; _searching = false;
            if (!_closed) { _searchButton.IsEnabled = _query.IsEnabled = _results.IsEnabled = true; SelectionChanged(); }
        }
    }

    private void SelectionChanged()
    {
        _choose.IsEnabled = !_searching && _results.SelectedItem is AssignmentRecord;
        _selection.Text = _results.SelectedItem is AssignmentRecord a ? $"Selected: #{a.Id} · {a.Name} · {a.ProjectName}" : "Select an assignment to continue.";
        _selection.MaxHeight = 42; _selection.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void Choose()
    {
        if (!_choose.IsEnabled || _results.SelectedItem is not AssignmentRecord selected) return;
        SelectedAssignment = selected; DialogResult = true;
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
}
