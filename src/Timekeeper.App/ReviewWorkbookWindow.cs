using System.Windows;
using System.Windows.Controls;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class ReviewWorkbookWindow : Window
{
    private readonly CheckBox _confirm = new() { Margin = new Thickness(0, 12, 0, 8) };
    private readonly CheckBox _duplicates = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _next = new() { Content = "Confirm and match assignments" };
    public ReviewWorkbookWindow(Window owner, EmailWorkbook workbook, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Confirm review workbook"; Width = 860; Height = 790; MinWidth = 660; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(28) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock { Text = "EMAIL / DRAFT REVIEW", Style = (Style)FindResource("SectionLabel") });
        header.Children.Add(new TextBlock { Text = "Confirm the proposed work", FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), FontSize = 28, Margin = new Thickness(0, 8, 0, 10) });
        header.Children.Add(new TextBlock { Text = $"{workbook.Metadata.EmployeeEmail} · {workbook.Metadata.PeriodStart:MMM d, yyyy} to {workbook.Metadata.PeriodEnd:MMM d, yyyy}\n{workbook.Activities.Count} activities · {workbook.Activities.Sum(a => a.Minutes) / 60m:0.00} hours", TextWrapping = TextWrapping.Wrap }); grid.Children.Add(header);
        var content = new StackPanel();
        void Text(string value, bool warning = false)
        {
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 14) };
            if (warning) text.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            content.Children.Add(text);
        }
        Text(EmailWorkbookReader.ReviewLimitations, true);
        Text("Coverage: " + workbook.Metadata.RetrievalLimitations, true);
        foreach (var note in workbook.ReviewNotes) Text(note, true);
        foreach (var row in workbook.Activities)
        {
            Text($"{row.Date:MMM d} · {row.Minutes} minutes · {row.MatterHint}\n{row.AssignmentHint}\n{row.Description}\nEvidence reviewed: {row.EvidenceSummary}");
            if (row.ReviewNotes.Length > 0) Text("Needs attention: " + row.ReviewNotes, true);
        }
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        _confirm.Content = new TextBlock { Text = "These activities are mine. I reviewed the estimates, policy allocations and notes, and confirm the proposed time.", TextWrapping = TextWrapping.Wrap };
        _duplicates.Content = new TextBlock { Text = "I understand duplicate detection is limited and will check existing Quickbase time before submitting.", TextWrapping = TextWrapping.Wrap };
        var footer = new StackPanel(); footer.Children.Add(_confirm); footer.Children.Add(_duplicates);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        _next.Style = (Style)FindResource("Primary"); _next.IsEnabled = false; _next.Click += (_, _) => DialogResult = true; buttons.Children.Add(_next); footer.Children.Add(buttons);
        void Update(object sender, RoutedEventArgs e) => _next.IsEnabled = _confirm.IsChecked == true && _duplicates.IsChecked == true;
        _confirm.Checked += Update; _confirm.Unchecked += Update; _duplicates.Checked += Update; _duplicates.Unchecked += Update;
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid; Appearance.Attach(this);
    }
    internal string SmokeSummary()
    {
        if (_next.IsEnabled) throw new InvalidOperationException("Draft accepted without confirmation.");
        _confirm.IsChecked = true;
        if (_next.IsEnabled) throw new InvalidOperationException("Draft accepted without duplicate warning confirmation.");
        _duplicates.IsChecked = true;
        if (!_next.IsEnabled) throw new InvalidOperationException("Confirmed draft could not continue.");
        _confirm.IsChecked = _duplicates.IsChecked = false;
        return "PASS: review workbook requires activity confirmation and limited duplicate-check acknowledgement\n";
    }
}