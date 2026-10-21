using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

public partial class MainWindow
{
    private bool _workspaceReady;
    private bool _settingWorkspaceDates;

    private void SetWorkspaceDates(DateOnly start, DateOnly end)
    {
        _settingWorkspaceDates = true;
        try
        {
            StartDate.SelectedDate = start.ToDateTime(TimeOnly.MinValue);
            EndDate.SelectedDate = end.ToDateTime(TimeOnly.MinValue);
            DateRangeOptions.IsExpanded = end != start;
        }
        finally { _settingWorkspaceDates = false; }
        UpdateWorkspaceStatus();
    }

    private DateOnly[] WorkspaceDates()
    {
        if (StartDate.SelectedDate is not DateTime start || EndDate.SelectedDate is not DateTime end) return [];
        var first = DateOnly.FromDateTime(start); var last = DateOnly.FromDateTime(end);
        var count = last.DayNumber - first.DayNumber + 1;
        return count is > 0 and <= 31 ? Enumerable.Range(0, count).Select(first.AddDays).ToArray() : [];
    }

    private void WorkspaceDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_workspaceReady || _settingWorkspaceDates) return;
        // A normal date change selects one day. An explicitly expanded range keeps its end.
        if (sender == StartDate && StartDate.SelectedDate is DateTime start
            && (DateRangeOptions.IsExpanded != true || EndDate.SelectedDate is null || EndDate.SelectedDate < start))
            SetWorkspaceDates(DateOnly.FromDateTime(start), DateOnly.FromDateTime(start));
        ResetSource();
        StatusText.Text = "Date changed. Read or import data for this date, or add a manual timecard. Saved manual entries are kept.";
        UpdateWorkspaceStatus();
    }

    private void DateRange_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!_workspaceReady || _settingWorkspaceDates || StartDate.SelectedDate is not DateTime start) return;
        if (EndDate.SelectedDate == start) return;
        SetWorkspaceDates(DateOnly.FromDateTime(start), DateOnly.FromDateTime(start));
        ResetSource();
        StatusText.Text = "Single date selected. Read or import data for this date. Saved manual entries are kept.";
    }

    private void UpdateWorkspaceStatus()
    {
        if (TogglLoadedText is null || EmailLoadedText is null) return;
        StartDate.IsEnabled = EndDate.IsEnabled = DateRangeOptions.IsEnabled = !_busy;
        var dates = WorkspaceDates();
        var label = dates.Length switch
        {
            0 => "Choose a date or a range of up to 31 days.",
            1 => dates[0].ToString("dddd, MMMM d, yyyy"),
            _ => $"{dates[0]:MMM d} - {dates[^1]:MMM d, yyyy}"
        };
        SelectedDateSummary.Text = label;
        if (_selectedDates is { Count: > 0 } && _selectedDates.Count != dates.Length)
            SelectedDateSummary.Text += $" · {_selectedDates.Count} dates included in review";
        ReadRangeButton.Content = dates.Length > 1 ? "Read selected dates" : "Read selected date";
        var loadedDates = LoadedWorkspaceDates();
        var covered = dates.Count(loadedDates.Contains);
        var fresh = _session is not null && DateTimeOffset.UtcNow - _session.GeneratedAtUtc < TimeSpan.FromHours(24);
        var active = _session is { ManualOnly: false };
        var isEmail = active && _session!.EmailWorkbook is not null;
        SetLight(TogglLoadedLight, TogglLoadedText, active && !isEmail);
        SetLight(EmailLoadedLight, EmailLoadedText, isEmail);
        TogglWorkflowPanel.Visibility = _session is { ManualOnly: false, EmailWorkbook: null } ? Visibility.Visible : Visibility.Collapsed;

        void SetLight(System.Windows.Shapes.Ellipse light, TextBlock text, bool sourceActive)
        {
            var complete = sourceActive && covered > 0 && covered == dates.Length && fresh;
            var partial = sourceActive && covered > 0 && !complete;
            light.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, complete ? "Success" : partial ? "Warning" : "Muted");
            light.Opacity = complete || partial ? 1 : .4;
            text.Text = complete ? "Loaded for selected date" + (dates.Length > 1 ? "s" : "")
                : partial ? !fresh ? "Read or import again - data expired" : $"Loaded for {covered} of {dates.Length} dates"
                : "No data loaded for selected date" + (dates.Length > 1 ? "s" : "");
            System.Windows.Automation.AutomationProperties.SetName(light, text.Text);
        }
    }

    private HashSet<DateOnly> LoadedWorkspaceDates()
    {
        if (_session is null || _session.ManualOnly) return [];
        // Older saved reads lack provenance. Only count dates supported by actual source data.
        IEnumerable<DateOnly> source = _session.LoadedSourceDates is { Count: > 0 } ? _session.LoadedSourceDates
            : _session.EmailWorkbook is not null ? _session.EmailWorkbook.Activities.Select(a => a.Date)
            : _session.Days.Where(d => d.Entries.Count > 0).Select(d => d.Date);
        return source.Where(d => _session.Days.Any(day => day.Date == d) && (_selectedDates is null || _selectedDates.Contains(d))).ToHashSet();
    }

    private void OpenEmailCopilot_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppSettings.SharedEmailCopilotUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Could not open the email agent: " + ex.Message; }
    }

    private static void CheckEmailWorkspaceDates(EmailWorkbook workbook, IReadOnlyCollection<DateOnly> dates)
    {
        if(workbook.Activities.Count==0) throw new InvalidDataException("This workbook contains no email activities.");
        if(!workbook.Activities.Any(a=>dates.Contains(a.Date)))
            throw new InvalidDataException($"This workbook has no email activities for the selected date{(dates.Count>1?"s":"")}. It contains {workbook.Activities.Min(a=>a.Date):MMM d, yyyy} through {workbook.Activities.Max(a=>a.Date):MMM d, yyyy}. Choose the matching date above, then import again. Workbook dates are never changed automatically.");
    }

    internal string SmokeWorkspaceDates()
    {
        if (!_smoke) throw new InvalidOperationException("Smoke workspace required.");
        var read = DemoData.CreateSession(); var day = read.Days[0].Date;
        read = read with { LoadedSourceDates = [day] };
        PresentSession(read, adoptDates: true);
        if (!TogglLoadedText.Text.StartsWith("Loaded") || EmailLoadedText.Text.StartsWith("Loaded"))
            throw new InvalidOperationException("Source indicators confused Toggl and email.");
        var manual = new ManualTimecard { Date = day, Hours = .25m, Description = "Saved research", Project = read.Settings.InternalProjectId, Task = read.Settings.InternalTaskId };
        _manualDrafts = [manual];
        StartDate.SelectedDate = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (_session is not null || _proposal is not null || WriteButton.IsEnabled || _manualDrafts.Single().Id != manual.Id || EndDate.SelectedDate != StartDate.SelectedDate)
            throw new InvalidOperationException("Date change retained stale data or lost manual drafts.");
        if (TogglLoadedText.Text.StartsWith("Loaded") || EmailLoadedText.Text.StartsWith("Loaded"))
            throw new InvalidOperationException("A new date inherited a loaded indicator.");
        var email = EmailSmoke.Session(); var emailDay = email.Days[0].Date;
        CheckEmailWorkspaceDates(email.EmailWorkbook!,[emailDay]);
        var rejected=false;
        try { CheckEmailWorkspaceDates(email.EmailWorkbook!,[emailDay.AddYears(1)]); }
        catch(InvalidDataException) { rejected=true; }
        if(!rejected || email.EmailWorkbook!.Activities.Any(a=>a.Date.Year==emailDay.Year+1))
            throw new InvalidOperationException("Email import changed workbook dates or accepted an unrelated date.");
        SetWorkspaceDates(emailDay, emailDay);
        PresentSession(email with { LoadedSourceDates = [emailDay] });
        if (!EmailLoadedText.Text.StartsWith("Loaded") || TogglLoadedText.Text.StartsWith("Loaded") || TogglWorkflowPanel.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Email workspace did not select its own loaded indicator.");
        var extra = emailDay.AddDays(1);
        PresentSession(email with { LoadedSourceDates = [emailDay], Days = [.. email.Days, new() { Date = extra }] });
        SetWorkspaceDates(extra, extra);
        if (EmailLoadedText.Text.StartsWith("Loaded")) throw new InvalidOperationException("A carried manual date was marked as imported email.");
        PresentSession(read with { ManualOnly = true, Days = [new() { Date = extra }], LoadedSourceDates = [] });
        if (TogglLoadedText.Text.StartsWith("Loaded") || EmailLoadedText.Text.StartsWith("Loaded")) throw new InvalidOperationException("Manual preload was marked as source import.");
        SetWorkspaceDates(day, day);
        PresentSession(read with { GeneratedAtUtc = DateTimeOffset.UtcNow.AddDays(-2) });
        if (!TogglLoadedText.Text.Contains("expired")) throw new InvalidOperationException("Expired source data was marked current.");
        _busy=true; UpdateWorkspaceStatus();
        if(StartDate.IsEnabled || EndDate.IsEnabled) throw new InvalidOperationException("Busy source operations leave dates editable.");
        _busy=false; UpdateWorkspaceStatus();
        SetWorkspaceDates(day,day.AddDays(2));
        if(!DateRangeOptions.IsExpanded) throw new InvalidOperationException("A multi-day workspace hid its end date.");
        DateRangeOptions.IsExpanded=false;
        if(WorkspaceDates().Length!=1) throw new InvalidOperationException("Collapsing the range retained hidden dates.");
        ResetSource(); _manualDrafts = []; UpdateManualPanel();
        return "PASS: date-first workspace clears stale review, preserves drafts, scopes source lights, and distinguishes manual preload from imports\n";
    }
}
