using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Timekeeper.App;

internal sealed class AppearanceWindow : Window
{
    private readonly AppearancePreferences _original;
    private readonly Dictionary<string, Button> _themes = [];
    private readonly ComboBox _accent = new(), _scale = new(), _density = new();
    private readonly TextBlock _status = new() { Text = "Changes preview immediately. Save to keep them.", FontSize = 12 };
    private string _theme;
    private bool _ready, _saved;
    internal AppearanceWindow(Window owner, bool smoke = false)
    {
        _original = Appearance.Current; _theme = _original.Theme;
        if (!smoke) Owner = owner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Appearance"; Width = 760; Height = 760; MinWidth = 600; MinHeight = 580;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var outer = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        outer.RowDefinitions.Add(new() { Height = GridLength.Auto }); outer.RowDefinitions.Add(new()); outer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "DISPLAY PREFERENCES", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 0, 0, 8) });
        header.Children.Add(new TextBlock { Text = "Appearance", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 32 });
        header.Children.Add(Hint("Choose a theme and adjust the interface.", new Thickness(0, 8, 0, 0))); outer.Children.Add(header);
        var content = new StackPanel();
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) }; Grid.SetRow(scroll, 1); outer.Children.Add(scroll);
        content.Children.Add(new TextBlock { Text = "Theme", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var themePanel = new WrapPanel();
        foreach (var theme in Appearance.Themes)
        {
            var tile = new StackPanel();
            tile.Children.Add(new TextBlock { Text = theme.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            tile.Children.Add(Hint(theme.Description, new Thickness(0, 4, 0, 10)));
            var swatches = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var color in new[] { theme.Canvas, theme.SurfaceMuted, theme.Accent, theme.Sidebar })
                swatches.Children.Add(new Border { Background = new SolidColorBrush(Appearance.Color(color)), BorderBrush = new SolidColorBrush(Appearance.Color(theme.Line)), BorderThickness = new Thickness(1), Width = 31, Height = 17, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0) });
            tile.Children.Add(swatches);
            var button = new Button { Content = tile, Width = 200, Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(2) };
            button.Click += (_, _) => { _theme = theme.Name; Preview(); };
            System.Windows.Automation.AutomationProperties.SetName(button, theme.Name + " theme");
            _themes.Add(theme.Name, button); themePanel.Children.Add(button);
        }
        content.Children.Add(themePanel);
        var controls = new StackPanel();
        AddControl(controls, "Accent color", "Use the theme color or choose an override.", _accent);
        AddControl(controls, "Interface size", "Scales text and controls together.", _scale);
        AddControl(controls, "Spacing", "Compact uses smaller controls and table rows.", _density);
        content.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 8, 0, 0), Child = controls });
        _accent.ItemsSource = Appearance.Accents; _accent.SelectedItem = _original.Accent;
        _scale.ItemsSource = Appearance.Scales.Select(value => value + "%").ToArray(); _scale.SelectedItem = _original.Scale + "%";
        _density.ItemsSource = Appearance.Densities; _density.SelectedItem = _original.Density;
        _accent.SelectionChanged += (_, _) => Preview(); _scale.SelectionChanged += (_, _) => Preview(); _density.SelectionChanged += (_, _) => Preview();
        var footer = new StackPanel { Margin = new Thickness(0, 18, 0, 0) }; _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(_status);
        var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var reset = new Button { Content = "Reset appearance", Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => Select(new()); actions.Children.Add(reset);
        var buttons = new WrapPanel();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; buttons.Children.Add(cancel);
        var save = new Button { Content = "Save appearance", Style = (Style)FindResource("Primary"), IsDefault = true };
        save.Click += (_, _) => { try { SaveSelection(); DialogResult = true; } catch (Exception ex) { _status.Text = "Could not save appearance: " + ex.Message; _status.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); } }; buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1); actions.Children.Add(buttons); footer.Children.Add(actions); Grid.SetRow(footer, 2); outer.Children.Add(footer);
        Content = outer; _ready = true; UpdateSelection(); Appearance.Attach(this);
        Closing += (_, _) => { if (!_saved) Appearance.Apply(_original); };
    }
    private static TextBlock Hint(string text, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return block;
    }
    private static void AddControl(Panel panel, string label, string description, ComboBox input)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(170) });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        labels.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        labels.Children.Add(Hint(description, new Thickness(0, 4, 0, 0))); row.Children.Add(labels);
        Grid.SetColumn(input, 1); input.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(input); panel.Children.Add(row);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
    }
    private void Select(AppearancePreferences value)
    {
        _ready = false; _theme = value.Theme; _accent.SelectedItem = value.Accent; _scale.SelectedItem = value.Scale + "%"; _density.SelectedItem = value.Density; _ready = true; Preview();
    }
    private AppearancePreferences Selection => new(_theme, (string)_accent.SelectedItem, int.Parse(((string)_scale.SelectedItem).TrimEnd('%')), (string)_density.SelectedItem);
    private void Preview()
    {
        if (!_ready) return;
        Appearance.Apply(Selection); UpdateSelection();
        _status.Text = $"{_theme} selected · Changes preview immediately. Save to keep them."; _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }
    private void UpdateSelection()
    {
        foreach (var (name, button) in _themes)
        {
            button.SetResourceReference(BorderBrushProperty, name == _theme ? "Accent" : "Line");
            button.SetResourceReference(BackgroundProperty, name == _theme ? "AccentSoft" : "Surface");
            ((TextBlock)((StackPanel)button.Content).Children[0]).Text = name + (name == _theme ? "  ✓" : "");
        }
    }
    private void SaveSelection() { Appearance.Save(Selection); _saved = true; }
    internal void SmokePreview(AppearancePreferences value) => Select(value);
    internal void SmokeSave() => SaveSelection();
}
