using System.Windows;
using System.Windows.Controls;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class DeletionRecoveryWindow : Window
{
    private readonly List<(CheckBox Check, string RowId)> _choices = [];
    private readonly Button _confirm;
    public IReadOnlyCollection<string> SelectedRowIds => _choices.Where(c => c.Check.IsChecked == true).Select(c => c.RowId).ToArray();

    public DeletionRecoveryWindow(Window owner, IReadOnlyList<RowOutcome> missing, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Recover deleted entries"; Width = 760; Height = 650; MinWidth = 560; MinHeight = 460;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new());
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "QUICKBASE / RECOVERY", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 0, 0, 9) });
        var heading = new TextBlock { Text = "Recover deleted entries", FontSize = 28, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(heading);
        header.Children.Add(new TextBlock { Text = "These previously created records were not found in Quickbase. Select only the entries you intentionally deleted. A permissions change can also hide a record; confirm the deletion in Quickbase if unsure.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        grid.Children.Add(header);
        var entries = new StackPanel();
        foreach (var outcome in missing)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = $"{outcome.Row.Date:MMM d, yyyy}   ·   {outcome.Row.Hours:0.00} h   ·   Record #{outcome.RecordId}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = outcome.Row.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
            var check = new CheckBox { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
            check.Checked += (_, _) => UpdateSelection(); check.Unchecked += (_, _) => UpdateSelection();
            _choices.Add((check, outcome.Row.RowId));
            entries.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = check });
        }
        var scroll = new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "The original receipt stays in history. After confirmation, read the day again and create a new proposal. Nothing is written to Quickbase by this action.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        _confirm = new Button { Content = "Confirm deletion & allow rewrite", IsEnabled = false, Style = (Style)FindResource("Primary") };
        _confirm.Click += (_, _) => { if (SelectedRowIds.Count > 0) DialogResult = true; };
        buttons.Children.Add(_confirm); footer.Children.Add(buttons); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid; Appearance.Attach(this);
    }

    private void UpdateSelection() => _confirm.IsEnabled = SelectedRowIds.Count > 0;

    internal string SmokeSummary()
    {
        if (_confirm.IsEnabled || SelectedRowIds.Count != 0 || _choices.Count == 0) throw new InvalidOperationException("Recovery must start with no entries selected.");
        _choices[0].Check.IsChecked = true;
        if (!_confirm.IsEnabled || SelectedRowIds.Single() != _choices[0].RowId) throw new InvalidOperationException("Recovery selection did not enable confirmation.");
        _choices[0].Check.IsChecked = false;
        if (_confirm.IsEnabled || SelectedRowIds.Count != 0) throw new InvalidOperationException("Clearing recovery selection did not disable confirmation.");
        return "PASS: deletion recovery requires explicit selection and clears approval when deselected\n";
    }
}
