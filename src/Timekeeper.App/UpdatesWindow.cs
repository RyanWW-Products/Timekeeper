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
    private readonly TextBlock _status = new() { Text = "Check for a newer Windows version.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) };
    private readonly TextBox _notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 110, MaxHeight = 220 };
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
        Title = "Timekeeper · Updates"; Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Keep Timekeeper up to date", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Installed version {VersionLabel}", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 5) });
        panel.Children.Add(_status); panel.Children.Add(_notes); panel.Children.Add(_progress);
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 14) };
        _check.Margin = new Thickness(0, 0, 8, 0); _check.Click += Check_Click; buttons.Children.Add(_check);
        _install.Style = (Style)FindResource("Primary"); _install.Click += Install_Click; buttons.Children.Add(_install);
        _cancel.Margin = new Thickness(8, 0, 0, 0); _cancel.Click += (_, _) => _cancellation?.Cancel(); buttons.Children.Add(_cancel); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "The installer replaces the app and keeps your settings, tokens and history. Timekeeper closes when the installer starts. Updates run only when you request them.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.SlateGray });
        var page = new Button { Content = "Open releases page ↗", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 12) };
        page.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(UpdateClient.ReleasesPage) { UseShellExecute = true }); } catch (Exception ex) { _status.Text = ex.Message; } }; panel.Children.Add(page);
        var access = new StackPanel();
        access.Children.Add(new TextBlock { Text = $"Private releases require a GitHub token here, even when you are signed into GitHub in your browser. Use a fine-grained token with Contents: read access to {UpdateClient.Repository}; your organization may require approval. This is separate from your Quickbase and Toggl tokens. Leave blank for public releases. Saved in Windows Credential Manager.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 8) });
        access.Children.Add(_token); _saveToken.Margin = new Thickness(0, 10, 0, 0); _saveToken.HorizontalAlignment = HorizontalAlignment.Left;
        _saveToken.Click += (_, _) => { try { CredentialVault.SaveUpdateToken(_token.Password.Trim()); _release = null; _install.IsEnabled = false; _status.Text = "Update access saved. Check for updates to use it."; } catch (Exception ex) { _status.Text = ex.Message; } };
        access.Children.Add(_saveToken); _access.Content = access; panel.Children.Add(_access);
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (!smoke) try { _token.Password = CredentialVault.LoadUpdateToken(); } catch (Exception ex) { _status.Text = ex.Message; }
        Closing += (_, e) => { if (_busy) { _cancellation?.Cancel(); e.Cancel = true; _status.Text = "Cancelling the update request…"; } };
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
        _release = null; _notes.Text = ""; _status.Text = "Checking GitHub releases…";
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true);
        try
        {
            using var client = new UpdateClient(CredentialVault.LoadUpdateToken());
            _release = await client.CheckAsync(InstalledVersion, cancellation.Token);
            _status.Text = _release is null ? "You're up to date." : $"Version {_release.Version} is ready ({_release.Size / 1024d / 1024d:0.0} MB).";
            _notes.Text = _release?.Notes ?? "You have the latest available stable version.";
        }
        catch (OperationCanceledException) { _status.Text = "Update check cancelled or timed out."; }
        catch (Exception ex) { ShowError(ex); }
        finally { _cancellation = null; Busy(false); }
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null || _busy) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; Busy(true, downloading: true); _progress.Value = 0;
        _status.Text = "Downloading the installer…";
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
        catch (OperationCanceledException) { _status.Text = "Update download cancelled or timed out. Your installed app is unchanged."; }
        catch (Exception ex) { ShowError(ex); }
        finally { _cancellation = null; Busy(false); }
        if (started) Application.Current.Shutdown();
    }
    private void ShowError(Exception error)
    {
        _status.Text = error.Message;
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
