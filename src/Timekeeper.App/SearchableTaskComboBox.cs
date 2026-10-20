using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Timekeeper.App;

/// <summary>
/// A task picker whose search text and highlighted result are independent of the
/// committed task. Filtering, keyboard navigation and dismissing the popup never
/// change the bound selection. Only choosing a result commits a task.
/// </summary>
internal sealed class SearchableTaskComboBox : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SearchableTaskComboBox),
        new PropertyMetadata(null, ItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(EmailMappingChoice), typeof(SearchableTaskComboBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (owner, _) => ((SearchableTaskComboBox)owner).UpdateSelectionLabel()));

    private readonly Button _button = new()
    {
        MinHeight = 34, Padding = new Thickness(8, 6, 8, 6), FontSize = 12,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup _popup = new()
    {
        Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
    };
    private readonly TextBox _search = new() { MinHeight = 34, Padding = new Thickness(8, 6, 8, 6), FontSize = 12 };
    private readonly Button _clear = new()
    {
        Content = "\u00d7", ToolTip = "Clear task search", Width = 32, MinHeight = 32,
        Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), FontSize = 18,
        IsTabStop = false,
    };
    private readonly TextBlock _placeholder = new()
    {
        Text = "Search task name or ID", Margin = new Thickness(9, 0, 6, 0),
        FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
    };
    private readonly ListBox _results = new()
    {
        BorderThickness = new Thickness(0), Padding = new Thickness(0),
        MaxHeight = 250, MinHeight = 36, FontSize = 12,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _resultCount = new()
    {
        FontSize = 11, Margin = new Thickness(2, 7, 2, 5), TextWrapping = TextWrapping.Wrap,
    };
    private readonly Border _popupBorder = new()
    {
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        Padding = new Thickness(9), Margin = new Thickness(0, 4, 0, 0),
    };
    private bool _ignoreNextButtonClick;
    private bool _navigatedResults;
    private List<EmailMappingChoice> _filtered = [];

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
    public EmailMappingChoice? SelectedItem
    {
        get => (EmailMappingChoice?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public SearchableTaskComboBox()
    {
        Focusable = false;
        var selected = new Grid();
        selected.ColumnDefinitions.Add(new());
        selected.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        selected.Children.Add(_label);
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 3,3 L 7,7 L 11,3"), StrokeThickness = 1.5,
            Width = 14, Height = 10, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Muted");
        Grid.SetColumn(arrow, 1); selected.Children.Add(arrow); _button.Content = selected;

        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new());
        searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var searchField = new Grid(); searchField.Children.Add(_search); searchField.Children.Add(_placeholder);
        searchRow.Children.Add(searchField); Grid.SetColumn(_clear, 1); searchRow.Children.Add(_clear);
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _resultCount.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _popupBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
        _popupBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        var body = new StackPanel(); body.Children.Add(searchRow); body.Children.Add(_resultCount); body.Children.Add(_results);
        _popupBorder.Child = body; _popup.Child = _popupBorder; _popup.PlacementTarget = _button;
        var root = new Grid(); root.Children.Add(_button); root.Children.Add(_popup); Content = root;

        var itemLabel = new FrameworkElementFactory(typeof(TextBlock));
        itemLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(EmailMappingChoice.Label)));
        itemLabel.SetBinding(ToolTipProperty, new Binding(nameof(EmailMappingChoice.Tooltip)));
        itemLabel.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        _results.ItemTemplate = new DataTemplate { VisualTree = itemLabel };
        _results.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _results.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(_search, "Search tasks by name or ID");
        AutomationProperties.SetName(_clear, "Clear task search");
        AutomationProperties.SetName(_results, "Matching tasks");
        AutomationProperties.SetLiveSetting(_resultCount, AutomationLiveSetting.Polite);

        _button.Click += (_, _) =>
        {
            if (_ignoreNextButtonClick) { _ignoreNextButtonClick = false; return; }
            if (_popup.IsOpen) ClosePopup(); else OpenPopup();
        };
        _button.PreviewKeyDown += Button_KeyDown;
        _button.PreviewTextInput += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Text) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
            OpenPopup(e.Text); e.Handled = true;
        };
        _search.TextChanged += (_, _) => RefreshResults();
        _search.PreviewKeyDown += Search_KeyDown;
        _results.PreviewKeyDown += Results_KeyDown;
        _results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(_results, source) is ListBoxItem item &&
                item.DataContext is EmailMappingChoice choice)
            {
                Commit(choice); e.Handled = true;
            }
        };
        _clear.Click += (_, _) => { _search.Clear(); _search.Focus(); };
        _popup.Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_popup.IsOpen) return;
            _search.Focus(); _search.CaretIndex = _search.Text.Length;
        }));
        _popup.Closed += (_, _) =>
        {
            // A click on the anchor dismisses a captured popup before Click fires.
            // Do not immediately reopen it when that same click reaches the button.
            var point = Mouse.GetPosition(_button);
            _ignoreNextButtonClick = Mouse.LeftButton == MouseButtonState.Pressed &&
                point.X >= 0 && point.Y >= 0 && point.X <= _button.ActualWidth && point.Y <= _button.ActualHeight;
            _search.Clear();
        };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) _popup.IsOpen = false; };
        Unloaded += (_, _) => _popup.IsOpen = false;
        Loaded += (_, _) => UpdateSelectionLabel();
        UpdateSelectionLabel(); RefreshResults();
    }

    private static void ItemsSourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var control = (SearchableTaskComboBox)owner;
        if (e.OldValue is INotifyCollectionChanged oldItems) oldItems.CollectionChanged -= control.ItemsChanged;
        if (e.NewValue is INotifyCollectionChanged newItems) newItems.CollectionChanged += control.ItemsChanged;
        control.RefreshResults();
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshResults();
    private IEnumerable<EmailMappingChoice> Choices => ItemsSource?.OfType<EmailMappingChoice>().Where(choice => choice.Id > 0) ?? [];

    private void UpdateSelectionLabel()
    {
        _label.Text = SelectedItem?.Label ?? "Choose task";
        _button.ToolTip = SelectedItem?.Tooltip ?? "Search and choose a task";
        var name = AutomationProperties.GetName(this);
        AutomationProperties.SetName(_button, $"{(string.IsNullOrWhiteSpace(name) ? "Task" : name)}: {_label.Text}. Search and choose a task.");
    }

    private void RefreshResults()
    {
        var highlighted = (_results.SelectedItem as EmailMappingChoice)?.Id ?? SelectedItem?.Id;
        _filtered = Filter(Choices, _search.Text);
        _results.ItemsSource = _filtered;
        _results.SelectedItem = _filtered.FirstOrDefault(choice => choice.Id == highlighted) ?? _filtered.FirstOrDefault();
        _navigatedResults = false;
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled = _search.Text.Length > 0;
        _resultCount.Text = _filtered.Count == 0
            ? (Choices.Any() ? "No matching tasks. Try another name or task ID." : "No tasks are available. Refresh Quickbase data and try again.")
            : $"{_filtered.Count} task{(_filtered.Count == 1 ? "" : "s")} \u00b7 Choose a result or press Enter";
        _results.Visibility = _filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static List<EmailMappingChoice> Filter(IEnumerable<EmailMappingChoice> choices, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return choices.Where(choice => words.All(word => choice.Label.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            (choice.Id?.ToString(CultureInfo.InvariantCulture).Contains(word, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
    }

    private void OpenPopup(string query = "")
    {
        if (!IsEnabled) return;
        _ignoreNextButtonClick = false;
        ConfigurePopupLayout();
        _results.SelectedItem = _filtered.FirstOrDefault(choice => choice.Id == SelectedItem?.Id);
        _search.Text = query; RefreshResults(); _popup.IsOpen = true;
    }
    private void ConfigurePopupLayout()
    {
        // A Popup uses a separate presentation source, so it does not inherit
        // the appearance LayoutTransform applied to the owning window.
        var scale = Appearance.Current.Scale / 100d;
        var availableWidth = Math.Max(180, (SystemParameters.WorkArea.Width - 24) / scale);
        var availableHeight = Math.Max(150, (SystemParameters.WorkArea.Height - 48) / scale);
        _popupBorder.LayoutTransform = new ScaleTransform(scale, scale);
        _popupBorder.Width = Math.Min(Math.Max(ActualWidth, 350), availableWidth);
        _popupBorder.MaxHeight = availableHeight;
        _results.MaxHeight = Math.Clamp(availableHeight - 110, 36, 250);
    }
    private void ClosePopup()
    {
        _popup.IsOpen = false; _button.Focus();
    }
    private void Commit(EmailMappingChoice choice)
    {
        var current = Choices.FirstOrDefault(item => item.Id == choice.Id);
        if (current is null) return;
        SetCurrentValue(SelectedItemProperty, current);
        ClosePopup();
    }
    private bool DismissOrCommit(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape || key == Key.F4 || key == Key.Up && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            ClosePopup();
        else if (key == Key.Enter)
        {
            if (_results.SelectedItem is EmailMappingChoice choice) Commit(choice);
        }
        else if (key == Key.Tab)
        {
            ClosePopup();
            _button.MoveFocus(new TraversalRequest(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        else return false;
        e.Handled = true; return true;
    }
    private void Button_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.F4 or Key.Down or Key.Up)) return;
        if (_popup.IsOpen) ClosePopup(); else OpenPopup();
        e.Handled = true;
    }
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (DismissOrCommit(e)) return;
        if (e.Key is not (Key.Down or Key.Up) || _filtered.Count == 0) return;
        // The first Down enters the already-highlighted first/current result;
        // it should not silently skip it when the user starts navigating.
        _results.SelectedIndex = !_navigatedResults && e.Key == Key.Down
            ? Math.Max(0, _results.SelectedIndex)
            : Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _filtered.Count - 1);
        _navigatedResults = true;
        _results.ScrollIntoView(_results.SelectedItem); e.Handled = true;
    }
    private void Results_KeyDown(object sender, KeyEventArgs e) => DismissOrCommit(e);

    internal string SmokeSummary()
    {
        var selected = SelectedItem;
        var query = _search.Text;
        try
        {
            var samples = new[]
            {
                new EmailMappingChoice(44, "Project Administration (#44)"),
                new EmailMappingChoice(119, "Case Intake Meeting (#119)"),
                new EmailMappingChoice(120, "Design Meeting (#120)"),
            };
            if (Filter(samples, "ADMIN").Single().Id != 44 || Filter(samples, "meeting intake").Single().Id != 119 ||
                Filter(samples, "119").Single().Id != 119 || Filter(samples, "missing").Count != 0 || Filter(samples, "  ").Count != 3)
                throw new InvalidOperationException("Task search must match case-insensitive words and task IDs.");
            var state = new SmokeSelection { Task = samples[0] };
            var probe = new SearchableTaskComboBox { ItemsSource = samples };
            probe.SetBinding(SelectedItemProperty, new Binding(nameof(SmokeSelection.Task))
            {
                Source = state, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            probe._search.Text = "meeting"; probe._results.SelectedIndex = 1;
            if (state.Task?.Id != 44 || probe.SelectedItem?.Id != 44)
                throw new InvalidOperationException("Highlighting a task must not change the selected task.");
            probe.Commit(samples[1]);
            if (state.Task?.Id != 119 || !BindingOperations.IsDataBound(probe, SelectedItemProperty))
                throw new InvalidOperationException("Choosing a task must update the model and preserve the selection binding.");
            probe.Commit(new EmailMappingChoice(999999, "Unknown task"));
            probe.ClosePopup();
            if (state.Task?.Id != 119)
                throw new InvalidOperationException("Unknown tasks and dismissing the picker must not change the selection.");
            _search.Text = "unlikely task search 9fd2";
            if (_filtered.Count != 0 || !Equals(SelectedItem, selected))
                throw new InvalidOperationException("Searching for an unavailable task must preserve the selected task.");
            _search.Clear();
            if (_filtered.Count != Choices.Count() || !Equals(SelectedItem, selected))
                throw new InvalidOperationException("Clearing task search must restore all choices without changing the selected task.");
            if (BindingOperations.IsDataBound(this, SelectedItemProperty) && GetBindingExpression(SelectedItemProperty)?.ParentBinding.Mode != BindingMode.TwoWay)
                throw new InvalidOperationException("The task picker must use a two-way selection binding.");
            return "Task picker searches names/IDs and unordered words, preserves selection while filtering/highlighting/dismissing, and commits only valid tasks without removing the binding.";
        }
        finally { _search.Text = query; }
    }

    internal FrameworkElement SearchPreviewForSmoke(string query)
    {
        var preview = new SearchableTaskComboBox { ItemsSource = Choices.ToArray(), SelectedItem = SelectedItem };
        preview._search.Text = query; preview.RefreshResults(); preview.ConfigurePopupLayout();
        preview._popup.Child = null;
        return preview._popupBorder;
    }
    private sealed class SmokeSelection { public EmailMappingChoice? Task { get; set; } }
}
