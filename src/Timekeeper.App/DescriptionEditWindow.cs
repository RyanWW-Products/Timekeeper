using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class DescriptionEditWindow : Window
{
    private readonly TextBox _input;
    private readonly TextBlock _feedback;
    private readonly Button _save;
    public string Description { get; private set; }

    public DescriptionEditWindow(Window owner, string description, string? originalDescription = null, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Description = description;
        Title = "Timekeeper · Edit description"; Width = 680; Height = 470; MinWidth = 480; MinHeight = 360;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var title = new TextBlock { Text = "Edit activity description", FontSize = 27, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = "This is the description that will be submitted to Quickbase.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(header);
        _input = new TextBox { Text = description, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12) };
        System.Windows.Automation.AutomationProperties.SetName(_input, "Activity description");
        Grid.SetRow(_input, 1); root.Children.Add(_input);
        _feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 16) };
        Grid.SetRow(_feedback, 2); root.Children.Add(_feedback);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (originalDescription is not null)
        {
            var reset = new Button { Content = "Use original", Margin = new Thickness(0, 0, 8, 0) };
            reset.Click += (_, _) => { _input.Text = originalDescription; _input.Focus(); };
            buttons.Children.Add(reset);
        }
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        _save = new Button { Content = "Save description", Style = (Style)FindResource("Primary") };
        _save.Click += (_, _) => Save(); buttons.Children.Add(_save);
        Grid.SetRow(buttons, 3); root.Children.Add(buttons);
        _input.TextChanged += (_, _) => Validate();
        _input.PreviewKeyDown += (_, e) => { if(e.Key==Key.Enter && Keyboard.Modifiers==ModifierKeys.Control) { e.Handled=true; Save(); } };
        Content = root; Appearance.Attach(this); Validate();
        Loaded += (_, _) => { _input.Focus(); _input.CaretIndex = _input.Text.Length; };
    }

    private void Validate()
    {
        var value = _input.Text.Trim();
        _save.IsEnabled = value.Length is > 0 and <= 4000 && !value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
        _feedback.Text = value.Length==0 ? "Enter a description before saving."
            : !_save.IsEnabled ? "Use at most 4,000 characters, without unsupported control characters."
            : $"{value.Length:N0} / 4,000 characters · Ctrl+Enter to save";
        _feedback.SetResourceReference(TextBlock.ForegroundProperty, _save.IsEnabled ? "Muted" : "Danger");
    }

    private void Save()
    {
        Validate(); if (!_save.IsEnabled) return;
        Description = _input.Text.Trim(); DialogResult = true;
    }

    internal string SmokeSummary()
    {
        var original=_input.Text;
        foreach(var invalid in new[]{"  ",new string('a',4001),"Text\u0001"})
        { _input.Text=invalid; if(_save.IsEnabled) throw new InvalidOperationException("Invalid description could be saved."); }
        _input.Text="Reviewed case notes.\nPrepared follow-up research.";
        if(!_save.IsEnabled || Description!=original) throw new InvalidOperationException("Description editor rejected valid multiline text or saved before confirmation.");
        _input.Text=original;
        return "PASS: description editor accepts multiline text and requires nonblank, valid text within 4,000 characters before saving\n";
    }
}
