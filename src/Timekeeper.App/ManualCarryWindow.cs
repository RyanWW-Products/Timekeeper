using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Timekeeper.Core;

namespace Timekeeper.App;

internal enum ManualCarryChoice { Cancel, Append, Delete }

/// <summary>Returns a choice about pending drafts without changing or submitting them.</summary>
internal sealed class ManualCarryWindow : Window
{
    private readonly IReadOnlyList<ManualTimecard> _pending;
    private readonly Button _append = new() { Content = "Append manual entries" };
    private readonly Button _delete = new() { Content = "Delete manual entries", Margin = new Thickness(0, 0, 10, 0) };
    private readonly DataGrid _days = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, MinHeight = 110 };
    public ManualCarryChoice Choice { get; private set; } = ManualCarryChoice.Cancel;

    public ManualCarryWindow(Window owner, IReadOnlyList<ManualTimecard> pending, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        _pending = pending.ToArray();
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Pending manual entries"; Width = 800; Height = 510; MinWidth = 580; MinHeight = 420;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(26, 24, 26, 24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock { Text = "TIMECARDS / PENDING DRAFT", Style = (Style)FindResource("SectionLabel") });
        var heading = new TextBlock { Text = _pending.Count == 1 ? "Keep your manual entry?" : "Keep your manual entries?", FontSize = 29, Margin = new Thickness(0, 7, 0, 12), TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(heading);
        var pronoun = _pending.Count == 1 ? "it" : "them";
        header.Children.Add(new TextBlock { Text = $"You have {_pending.Count} pending manual {(_pending.Count == 1 ? "entry" : "entries")} totaling {_pending.Sum(entry => entry.Hours):0.00} hours. Append {pronoun} to the new review, or delete {pronoun} from your draft.", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(header);
        _days.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new Binding(nameof(PendingDate.Date)) { StringFormat = "ddd, MMM d, yyyy" }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
        _days.Columns.Add(new DataGridTextColumn { Header = "Manual entries", Binding = new Binding(nameof(PendingDate.Count)), Width = 130 });
        _days.Columns.Add(new DataGridTextColumn { Header = "Hours", Binding = new Binding(nameof(PendingDate.Hours)) { StringFormat = "0.00" }, Width = 100 });
        _days.ItemsSource = _pending.GroupBy(entry => entry.Date).OrderBy(group => group.Key).Select(group => new PendingDate(group.Key, group.Count(), group.Sum(entry => entry.Hours))).ToArray();
        AutomationProperties.SetName(_days, "Pending manual entries by date");
        Grid.SetRow(_days, 1); root.Children.Add(_days);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var hint = new TextBlock { Text = "Appending keeps each entry's date, description, hours and billing. Nothing is sent to Quickbase until you review and submit.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(hint);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        _delete.SetResourceReference(Control.ForegroundProperty, "Danger"); _delete.IsEnabled = _pending.Count > 0; _append.IsEnabled = _pending.Count > 0;
        _append.Style = (Style)FindResource("Primary");
        _append.Click += (_, _) => Choose(ManualCarryChoice.Append);
        _delete.Click += (_, _) =>
        {
            var confirmation = new ManualDeleteConfirmationWindow(this, _pending.Count, _pending.Sum(entry => entry.Hours));
            if (confirmation.ShowDialog() == true) Choose(ManualCarryChoice.Delete, confirmed: true);
        };
        buttons.Children.Add(_delete); buttons.Children.Add(_append); footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root; Appearance.Attach(this);
    }

    private bool Choose(ManualCarryChoice choice, bool confirmed = false, bool close = true)
    {
        if (_pending.Count == 0 || choice is ManualCarryChoice.Cancel || choice == ManualCarryChoice.Delete && !confirmed) return false;
        Choice = choice; if (close) DialogResult = true; return true;
    }
    internal string SmokeSummary()
    {
        var original = Choice;
        try
        {
            if (Choice != ManualCarryChoice.Cancel || Choose(ManualCarryChoice.Delete, close: false) || Choice != ManualCarryChoice.Cancel)
                throw new InvalidOperationException("Deleting pending manual entries must require a separate confirmation.");
            if (_pending.Count > 0)
            {
                if (!Choose(ManualCarryChoice.Append, close: false) || Choice != ManualCarryChoice.Append)
                    throw new InvalidOperationException("Appending manual entries did not return the explicit choice.");
                if (!Choose(ManualCarryChoice.Delete, confirmed: true, close: false) || Choice != ManualCarryChoice.Delete)
                    throw new InvalidOperationException("Confirmed manual deletion did not return the explicit choice.");
                var summary = _days.ItemsSource.Cast<PendingDate>().ToArray();
                if (summary.Sum(day => day.Count) != _pending.Count || summary.Sum(day => day.Hours) != _pending.Sum(entry => entry.Hours))
                    throw new InvalidOperationException("Pending manual entry dates or totals were not represented accurately.");
            }
            return "PASS: pending manual entry counts/dates/totals, explicit append choice, and separate deletion confirmation\n";
        }
        finally { Choice = original; }
    }
    internal Window DeleteConfirmationForSmoke() => new ManualDeleteConfirmationWindow(this, _pending.Count, _pending.Sum(entry => entry.Hours), smoke: true);
    private sealed record PendingDate(DateOnly Date, int Count, decimal Hours);
}

internal sealed class ManualDeleteConfirmationWindow : Window
{
    public static bool Confirm(Window owner, IReadOnlyCollection<ManualTimecard> entries) => entries.Count > 0 &&
        new ManualDeleteConfirmationWindow(owner, entries.Count, entries.Sum(entry => entry.Hours)).ShowDialog() == true;

    public ManualDeleteConfirmationWindow(Window owner, int count, decimal hours, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        var noun = count == 1 ? "manual entry" : "manual entries";
        Title = $"Timekeeper · Delete {noun}"; Width = 590; Height = 340; MinWidth = 490; MinHeight = 310;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var body = new StackPanel();
        var heading = new TextBlock { Text = $"Delete {noun}?", FontSize = 27, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = $"Remove {count} pending manual {(count == 1 ? "entry" : "entries")} ({hours:0.00} hours) from your draft? You would need to enter this work again to include it.", TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = "This only clears the pending manual entries. Existing Quickbase records and imported time are not deleted.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(hint);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = $"Keep {noun}", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        var delete = new Button { Content = $"Delete {noun}" };
        delete.SetResourceReference(Control.BackgroundProperty, "DangerSoft"); delete.SetResourceReference(Control.ForegroundProperty, "Danger"); delete.SetResourceReference(Control.BorderBrushProperty, "Danger");
        delete.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(delete); Grid.SetRow(buttons, 1); root.Children.Add(buttons); Content = root; Appearance.Attach(this);
    }
}
