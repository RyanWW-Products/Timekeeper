using System.Windows;
using System.Windows.Controls;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class ProposalDatesWindow : Window
{
    private readonly List<(DateOnly Date, CheckBox Check)> _choices = [];
    private readonly Button _continue;
    private readonly TextBlock _summary;
    public IReadOnlyCollection<DateOnly> SelectedDates => _choices.Where(c => c.Check.IsChecked == true).Select(c => c.Date).ToArray();

    public ProposalDatesWindow(Window owner, ReadSession session, ProposalEnvelope proposal, IReadOnlyCollection<DateOnly>? selection = null, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Which dates do you want to submit?"; Width = 760; Height = 670; MinWidth = 580; MinHeight = 500;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var proposedDates = ProposalScope.ProposedDates(session, proposal);
        var selected = selection ?? proposedDates;
        var grid = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "PROPOSAL / DATES", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 0, 0, 9) });
        var title = new TextBlock { Text = "Which dates do you want to submit?", FontSize = 28, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = "Your read contains more than one date. Choose the dates to review and submit. Each selected day must have a complete proposal; dates you leave unchecked will be excluded.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        grid.Children.Add(header);
        var dates = new StackPanel();
        foreach (var day in session.Days.OrderBy(d => d.Date))
        {
            var covered = proposal.Rows.Where(r => r.Date == day.Date).SelectMany(r => r.SourceEntryIds)
                .Concat(proposal.AlreadyRecorded.Select(r => r.SourceEntryId)).ToHashSet();
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = day.Date.ToString("dddd, MMMM d, yyyy"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"{day.Entries.Count(e => covered.Contains(e.Id))} of {day.Entries.Count} Toggl entries in this proposal · {day.Existing.Sum(r => r.Hours):0.00} h already in Quickbase", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
            var check = new CheckBox { Content = content, IsChecked = selected.Contains(day.Date), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
            _choices.Add((day.Date, check));
            dates.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = check });
        }
        var scroll = new ScrollViewer { Content = dates, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) }; footer.Children.Add(_summary);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var all = new Button { Content = "Select all dates", Margin = new Thickness(0, 0, 10, 8) };
        all.Click += (_, _) => { foreach (var choice in _choices) choice.Check.IsChecked = true; }; buttons.Children.Add(all);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 8) });
        _continue = new Button { Content = "Continue with selected dates", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 0, 8) };
        _continue.Click += (_, _) => { if (SelectedDates.Count > 0) DialogResult = true; }; buttons.Children.Add(_continue);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        foreach (var choice in _choices) { choice.Check.Checked += (_, _) => UpdateSelection(); choice.Check.Unchecked += (_, _) => UpdateSelection(); }
        UpdateSelection(); Content = grid; Appearance.Attach(this);
    }

    private void UpdateSelection()
    {
        _continue.IsEnabled = SelectedDates.Count > 0;
        _summary.Text = SelectedDates.Count == 0 ? "Select at least one date to continue."
            : $"{SelectedDates.Count} {(SelectedDates.Count == 1 ? "date" : "dates")} selected; {_choices.Count - SelectedDates.Count} excluded. Timekeeper will check the selected days and recalculate their totals before you review them. Nothing is written by continuing.";
    }

    internal string SmokeSummary()
    {
        var original = SelectedDates.ToHashSet();
        foreach (var choice in _choices) choice.Check.IsChecked = false;
        if (_continue.IsEnabled) throw new InvalidOperationException("Empty date selection enabled Continue.");
        _choices[0].Check.IsChecked = true;
        if (!_continue.IsEnabled || SelectedDates.Single() != _choices[0].Date) throw new InvalidOperationException("Date selection did not enable Continue.");
        foreach (var choice in _choices) choice.Check.IsChecked = original.Contains(choice.Date);
        return "PASS: proposal date choices require a nonempty explicit selection\n";
    }
}
