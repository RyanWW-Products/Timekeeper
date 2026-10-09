using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class DurationEditWindow : Window
{
    public decimal Hours { get; private set; }
    public DurationEditWindow(Window owner, VerifiedRow row, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Title = "Timekeeper · Edit hours"; Width = 450; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Edit hours", FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), FontSize = 26, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = $"{row.Date:MMM d} · {row.TaskName}\n{row.Description}", TextWrapping = TextWrapping.Wrap, MaxHeight = 120 });
        var input = new TextBox { Text = row.Hours.ToString("0.00", CultureInfo.CurrentCulture), Margin = new Thickness(0, 16, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(input, "Duration in decimal hours"); panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = "Decimal hours, for example 0.25 = 15 minutes. Other timecards and Misc internal stay unchanged. Review the daily total after saving.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "Save hours", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) =>
        {
            if (!decimal.TryParse(input.Text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var value) || value is <= 0 or > 24 || decimal.Round(value, 2) != value)
            { error.Text = "Enter more than 0 and at most 24 hours, with up to two decimal places."; input.Focus(); return; }
            Hours = value; DialogResult = true;
        };
        buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel; Appearance.Attach(this);
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}