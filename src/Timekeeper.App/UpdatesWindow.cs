using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class UpdatesWindow : Window
{
    internal static Version InstalledVersion => typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
    internal static string VersionLabel => InstalledVersion.ToString(3);
    private readonly TextBlock _status = new() { Text = "Check for a newer Windows version.", TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 7, 0, 0), LineHeight = 21 };
    private readonly TextBlock _statusLabel = new() { Text = "READY TO CHECK" };
    private readonly Border _statusCard = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBox _notes = new() { Text = "Release notes will appear here after you check for updates.", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 42, MaxHeight = 180, FontSize = 12, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
    private readonly Button _check = new() { Content = "Check for updates" }, _install = new() { Content = "Download and install", IsEnabled = false }, _cancel = new() { Content = "Cancel download", Visibility = Visibility.Collapsed };
    private readonly PasswordBox _token = new();
    private readonly Button _saveToken = new() { Content = "Save update access" };
    private readonly Expander _access = new() { Header = "GitHub access" };
    private readonly ProgressBar _progress = new() { Height = 5, Margin = new Thickness(0, 10, 0, 10), Visibility = Visibility.Collapsed };
    private AppRelease? _release;
    private CancellationTokenSource? _cancellation;
    private bool _busy;

    public UpdatesWindow(Window owner, bool smoke = false)
    {
        if (!smoke) Owner = owner;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Timekeeper · Updates"; Width = 660; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = "TIMEKEEPER / WINDOWS", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 0, 0, 10) });
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 10) }; heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "Updates", FontFamily = Font("DisplayFont"), FontSize = 32 });
        var version = new Border { Background = Brush("AccentSoft"), CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 7, 11, 7), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = $"INSTALLED  {VersionLabel}", FontFamily = Font("MonoFont"), FontSize = 11, Foreground = Brush("AccentDeep") } };
        Grid.SetColumn(version, 1); heading.Children.Add(version); panel.Children.Add(heading);
        panel.Children.Add(Hint("Check for and install Timekeeper updates.", new Thickness(0, 0, 0, 22)));
        var releasePanel = new StackPanel();
        var statusPanel = new StackPanel(); _statusLabel.Style = (Style)FindResource("SectionLabel"); statusPanel.Children.Add(_statusLabel); statusPanel.Children.Add(_status); _statusCard.Child = statusPanel; _statusCard.Background = Brush("SurfaceMuted"); releasePanel.Children.Add(_statusCard);
        releasePanel.Children.Add(new TextBlock { Text = "Release notes", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        _notes.Background = Brush("Surface"); _notes.Foreground = Brush("Muted"); releasePanel.Children.Add(_notes); releasePanel.Children.Add(_progress);
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        _check.Margin = new Thickness(0, 0, 8, 0); _check.FontSize = 12; _check.Padding = new Thickness(12, 10, 12, 10); _check.Click += Check_Click; buttons.Children.Add(_check);
        _install.Style = (Style)FindResource("Primary"); _install.FontSize = 12; _install.Padding = new Thickness(12, 10, 12, 10); _install.Click += Install_Click; buttons.Children.Add(_install);
        _cancel.FontSize = 12; _cancel.Style = (Style)FindResource("Quiet"); _cancel.Margin = new Thickness(8, 0, 0, 0); _cancel.Click += (_, _) => _cancellation?.Cancel(); buttons.Children.Add(_cancel); releasePanel.Children.Add(buttons);
        panel.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(20), Child = releasePanel });
        panel.Children.Add(Hint("Your settings, tokens and history stay in place. Timekeeper closes when the installer starts. Updates run only when you request them.", new Thickness(2, 14, 2, 6)));
        var page = new Button { Content = "Browse releases on GitHub ↗", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
        page.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(UpdateClient.ReleasesPage) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex); } }; panel.Children.Add(page);
        var access = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        access.Children.Add(Hint("Private releases need a GitHub token here, even when you are signed into GitHub in your browser.", new Thickness(0, 0, 0, 10)));
        access.Children.Add(Hint($"Use a fine-grained token with Contents: read access to {UpdateClient.Repository}. Your organization may require approval.", new Thickness(0, 0, 0, 12)));
        access.Children.Add(new TextBlock { Text = "GitHub access token", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var tokenRow = new Grid(); tokenRow.ColumnDefinitions.Add(new ColumnDefinition()); tokenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); tokenRow.Children.Add(_token);
        _saveToken.FontSize = 12; _saveToken.Padding = new Thickness(12, 10, 12, 10); _saveToken.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(_saveToken, 1); tokenRow.Children.Add(_saveToken); access.Children.Add(tokenRow);
        access.Children.Add(Hint("Separate from your Quickbase and Toggl tokens. Saved in Windows Credential Manager. Leave blank for public releases.", new Thickness(0, 7, 0, 0)));
        _saveToken.Click += (_, _) => { try { CredentialVault.SaveUpdateToken(_token.Password.Trim()); _release = null; _install.IsEnabled = false; SetStatus("ACCESS SAVED", "Update access saved. Check for updates to use it.", "AccentSoft", "AccentDeep"); } catch (Exception ex) { ShowError(ex); } };
        _access.Content = access; panel.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 18, 14), Child = _access });
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (!smoke) try { _token.Password = CredentialVault.LoadUpdateToken(); } catch (Exception ex) { _status.Text = ex.Message; }
        Closing += (_, e) => { if (_busy) { _cancellation?.Cancel(); e.Cancel = true; _status.Text = "Cancelling the update request…"; } };
        Appearance.Attach(this);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);
    private static TextBlock Hint(string text, Thickness margin) => new() { Text = text, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 18, Margin = margin };
    private void SetStatus(string label, string message, string background = "SurfaceMuted", string foreground = "Ink")
    {
        _statusLabel.Text = label; _status.Text = message; _statusCard.Background = Brush(background); _status.Foreground = Brush(foreground); _statusLabel.Foreground = Brush(foreground);
    }

    private void Busy(bool busy, bool downloading = false)
    {
        _busy = busy; _check.IsEnabled = _token.IsEnabled = _saveToken.IsEnabled = !busy;
        _install.IsEnabled = !busy && _release is not null;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = downloading ? "Cancel download" : "Cancel check";
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = !downloading;
    }
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _release = null; _notes.Text = ""; SetStatus("CHECKING RELEASES", "Checking GitHub releases…");
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true);
        try
        {
            using var client = new UpdateClient(CredentialVault.LoadUpdateToken());
            _release = await client.CheckAsync(InstalledVersion, cancellation.Token);
            SetStatus(_release is null ? "ALL UP TO DATE" : "UPDATE AVAILABLE", _release is null ? "You're up to date." : $"Version {_release.Version} is ready ({_release.Size / 1024d / 1024d:0.0} MB).", "SuccessSoft", "Success");
            _notes.Text = _release?.Notes ?? "You have the latest available stable version.";
        }
        catch (OperationCanceledException) { SetStatus("CHECK STOPPED", "Update check cancelled or timed out."); }
        catch (Exception ex) { ShowError(ex); }
        finally { _cancellation = null; Busy(false); }
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null || _busy) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true, downloading: true); _progress.Value = 0;
        SetStatus("PREPARING YOUR UPDATE", "Downloading the installer…", "AccentSoft", "AccentDeep");
        bool started = false;
        try
        {
            using var client = new UpdateClient(CredentialVault.LoadUpdateToken());
            string path = await client.DownloadAsync(_release, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Timekeeper", "Updates"),
                new Progress<int>(value => { _progress.Value = value; _status.Text = $"Downloading and verifying… {value}%"; }), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            if (process is null) throw new InvalidOperationException("Windows did not start the installer. Try the releases page.");
            process.Dispose(); started = true;
        }
        catch (OperationCanceledException) { SetStatus("DOWNLOAD STOPPED", "Update download cancelled or timed out. Your installed app is unchanged."); }
        catch (Exception ex) { ShowError(ex); }
        finally { _cancellation = null; Busy(false); }
        if (started) Application.Current.Shutdown();
    }
    private void ShowError(Exception error)
    {
        bool accessNeeded=error is UpdateAccessException;
        SetStatus(accessNeeded ? "GITHUB ACCESS NEEDED" : "COULD NOT COMPLETE UPDATE", error.Message, accessNeeded ? "WarningSoft" : "DangerSoft", accessNeeded ? "Warning" : "Danger");
        if (_release is null) _notes.Text = "Release notes will be available after a successful update check.";
        if (error is UpdateAccessException) _access.IsExpanded = true;
    }
    internal string SmokeSummary()
    {
        if (_install.IsEnabled || _busy || _release is not null) throw new InvalidOperationException("Updates allowed installation before checking a release.");
        Busy(true, true);
        if (_check.IsEnabled || _install.IsEnabled || _token.IsEnabled || _cancel.Visibility != Visibility.Visible) throw new InvalidOperationException("Update controls were not gated during download.");
        Busy(false);
        ShowError(new UpdateAccessException("GitHub could not return the release. Private releases require a GitHub token with Contents: read access. Timekeeper cannot use your browser's GitHub login."));
        if (!_access.IsExpanded || _install.IsEnabled) throw new InvalidOperationException("Update access help was not shown after an access failure.");
        return "PASS: updates require a checked release, disable competing actions during download, and show access help after an authentication failure\n";
    }
}
