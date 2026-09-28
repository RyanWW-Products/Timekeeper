using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace Timekeeper.App;

internal sealed record AppearancePreferences(string Theme = "Ivory", string Accent = "Theme color", int Scale = 100, string Density = "Comfortable");
internal sealed record ThemeDefinition(string Name, string Description, bool Dark, string Canvas, string Surface, string SurfaceMuted, string Ink, string Muted, string Line, string Accent, string AccentDeep, string AccentSoft, string Sidebar, string SidebarInk, string SidebarMuted, string Warm);

/// <summary>UI-only preferences. They never participate in account identity or a read session.</summary>
internal static class Appearance
{
    internal static readonly ThemeDefinition[] Themes =
    [
        new("Ivory", "Warm light", false, "#F4F1E9", "#FFFEFA", "#EEEEE6", "#203735", "#5E6D66", "#CDD6CA", "#23665D", "#194E47", "#E7EFE8", "#1C3934", "#FFFEFA", "#B4C5BA", "#D1B183"),
        new("Dark", "Neutral dark", true, "#14191F", "#1D242C", "#27313C", "#EDF1F5", "#B2BFCC", "#465463", "#88C9DD", "#B0E0EA", "#273F4A", "#10151A", "#EDF1F5", "#ACBDCA", "#D1B183"),
        new("Slate", "Cool light", false, "#EDF1F5", "#FCFDFE", "#E6ECF2", "#243445", "#546579", "#C6D1DD", "#345F91", "#254970", "#DFE9F5", "#243445", "#FCFDFE", "#C1CDDA", "#98B8DE"),
        new("Forest", "Deep green", true, "#12211D", "#1B3028", "#284035", "#ECF3E9", "#B1C8B7", "#4A6A58", "#A6D5AA", "#CCE8C9", "#314E3D", "#101C18", "#ECF3E9", "#ACC6B3", "#D7BD87"),
        new("Sand", "Warm neutral", false, "#F2EAE0", "#FFFBF5", "#EBE1D4", "#3D342F", "#716056", "#D3C4B2", "#885033", "#6C3D26", "#F1DFCE", "#3D342F", "#FFFBF5", "#D5C3AE", "#D8AC74")
    ];
    internal static readonly string[] Accents = ["Theme color", "Teal", "Blue", "Violet", "Amber"];
    internal static readonly int[] Scales = [100, 110, 120];
    internal static readonly string[] Densities = ["Comfortable", "Compact"];
    private static readonly Dictionary<string, ColorSource> Sources = [];
    private static readonly List<WeakReference<Window>> Windows = [];
    private static string _path = "";
    internal static AppearancePreferences Current { get; private set; } = new();
    internal static bool IsDark => Themes.Single(t => t.Name == Current.Theme).Dark;

    internal static void Initialize(string root, bool smoke)
    {
        _path = Path.Combine(root, "appearance.json");
        Apply(smoke ? new() : Load(_path));
    }
    internal static AppearancePreferences Normalize(AppearancePreferences value) => new(
        Themes.Any(t => t.Name == value.Theme) ? value.Theme : "Ivory",
        Accents.Contains(value.Accent) ? value.Accent : "Theme color",
        Scales.Contains(value.Scale) ? value.Scale : 100,
        Densities.Contains(value.Density) ? value.Density : "Comfortable");
    internal static AppearancePreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return new();
            return Normalize(JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(path)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static void Save(AppearancePreferences preferences)
    {
        var normalized = Normalize(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(normalized)); File.Move(temp, _path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Apply(normalized);
    }
    internal static void Apply(AppearancePreferences preferences)
    {
        Current = Normalize(preferences);
        foreach (var pair in Palette(Current))
        {
            if (!Sources.TryGetValue(pair.Key, out var source))
            {
                source = new ColorSource(); Sources.Add(pair.Key, source);
                // A binding keeps the shared brush unfrozen, including in existing code-built dialogs.
                var brush = new SolidColorBrush();
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(ColorSource.Color)) { Source = source });
                Application.Current.Resources[pair.Key] = brush;
            }
            source.Color = pair.Value;
        }
        bool compact = Current.Density == "Compact";
        var resources = Application.Current.Resources;
        resources["ControlPadding"] = compact ? new Thickness(12, 6, 12, 6) : new Thickness(16, 10, 16, 10);
        resources["FieldPadding"] = compact ? new Thickness(9, 6, 9, 6) : new Thickness(11, 9, 11, 9);
        resources["ControlHeight"] = compact ? 32d : 38d;
        resources["DateHeight"] = compact ? 34d : 40d;
        resources["TableRowHeight"] = compact ? 36d : 46d;
        resources["CellPadding"] = compact ? new Thickness(10, 5, 10, 5) : new Thickness(12, 8, 12, 8);
        resources["NavigationPadding"] = compact ? new Thickness(15, 9, 15, 9) : new Thickness(15, 13, 15, 13);
        resources["ContentMargin"] = compact ? new Thickness(22, 22, 22, 14) : new Thickness(28, 28, 28, 18);
        resources["SectionGap"] = new Thickness(0, 0, 0, compact ? 10 : 16);
        Windows.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var reference in Windows) if (reference.TryGetTarget(out var window)) RefreshWindow(window);
    }
    internal static Dictionary<string, Color> Palette(AppearancePreferences value)
    {
        value = Normalize(value);
        var t = Themes.Single(t => t.Name == value.Theme);
        var colors = new Dictionary<string, Color>
        {
            ["Canvas"] = Color(t.Canvas), ["Surface"] = Color(t.Surface), ["SurfaceMuted"] = Color(t.SurfaceMuted),
            ["Ink"] = Color(t.Ink), ["Muted"] = Color(t.Muted), ["Line"] = Color(t.Line),
            ["Accent"] = Color(t.Accent), ["AccentDeep"] = Color(t.AccentDeep), ["AccentSoft"] = Color(t.AccentSoft),
            ["Sidebar"] = Color(t.Sidebar), ["SidebarInk"] = Color(t.SidebarInk), ["SidebarMuted"] = Color(t.SidebarMuted), ["Warm"] = Color(t.Warm),
            ["OnAccent"] = Color(t.Dark ? "#122127" : "#FFFFFF"),
            ["Success"] = Color(t.Dark ? "#B1DEBC" : "#286147"), ["SuccessSoft"] = Color(t.Dark ? "#284236" : "#E7F0E5"),
            ["Warning"] = Color(t.Dark ? "#F0D1A0" : "#785729"), ["WarningSoft"] = Color(t.Dark ? "#443827" : "#F6ECD7"),
            ["Danger"] = Color(t.Dark ? "#F4B8AC" : "#9C4232"), ["DangerSoft"] = Color(t.Dark ? "#462D2D" : "#F8EAE3")
        };
        string[]? custom = value.Accent switch
        {
            "Teal" => t.Dark ? ["#87D6C5", "#B1EADC"] : ["#23665D", "#194E47"],
            "Blue" => t.Dark ? ["#9CC8FF", "#C5DFFF"] : ["#345F91", "#254970"],
            "Violet" => t.Dark ? ["#C9B8F5", "#E1D5FF"] : ["#6D488D", "#53346D"],
            "Amber" => t.Dark ? ["#E7C17D", "#F5DCAF"] : ["#80551D", "#604017"],
            _ => null
        };
        if (custom is not null)
        {
            colors["Accent"] = Color(custom[0]); colors["AccentDeep"] = Color(custom[1]);
            colors["AccentSoft"] = Mix(colors["Surface"], colors["Accent"], t.Dark ? .18 : .08);
        }
        colors["SidebarLine"] = Mix(colors["Sidebar"], colors["SidebarInk"], .18);
        colors["NavHover"] = Mix(colors["Sidebar"], colors["SidebarInk"], .09);
        colors["NavActive"] = Mix(colors["Sidebar"], colors["SidebarInk"], .15);
        colors["InputHover"] = Mix(colors["Surface"], colors["Ink"], .5);
        colors["ScrollThumb"] = Mix(colors["Surface"], colors["Muted"], .65);
        colors["GridLine"] = Mix(colors["Surface"], colors["Line"], .7);
        colors["RowAlt"] = Mix(colors["Surface"], colors["SurfaceMuted"], .45);
        colors["RowHover"] = colors["AccentSoft"];
        colors["ExportLine"] = Mix(colors["Surface"], colors["Accent"], .4);
        colors["ExportHover"] = Mix(colors["AccentSoft"], colors["Surface"], .25);
        colors["VerificationLine"] = Mix(colors["SuccessSoft"], colors["Success"], .3);
        colors["ToolTipSurface"] = colors["Ink"]; colors["ToolTipInk"] = colors["Canvas"];
        return colors;
    }
    internal static void Attach(Window window)
    {
        if (Windows.Any(w => w.TryGetTarget(out var existing) && existing == window)) return;
        Windows.Add(new(window));
        window.SourceInitialized += (_, _) => RefreshWindow(window);
        window.Closed += (_, _) => Windows.RemoveAll(w => !w.TryGetTarget(out var existing) || existing == window);
        RefreshWindow(window);
    }
    private static void RefreshWindow(Window window)
    {
        if (window.Content is FrameworkElement content) content.LayoutTransform = new ScaleTransform(Current.Scale / 100d, Current.Scale / 100d);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int background = NativeColor(((SolidColorBrush)Application.Current.Resources["Canvas"]).Color);
        int foreground = NativeColor(((SolidColorBrush)Application.Current.Resources["Ink"]).Color);
        _ = DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
    }
    private static int NativeColor(Color c) => c.R | c.G << 8 | c.B << 16;
    internal static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color Mix(Color a, Color b, double amount) => System.Windows.Media.Color.FromRgb((byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private sealed class ColorSource : DependencyObject
    {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(ColorSource));
        public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    }
}
