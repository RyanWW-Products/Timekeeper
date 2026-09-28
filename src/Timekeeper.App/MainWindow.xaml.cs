using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Timekeeper.Core;
using ValidationResult = Timekeeper.Core.ValidationResult;

namespace Timekeeper.App;

public partial class MainWindow : Window
{
    private readonly SessionStore _store;
    private AppSettings _settings;
    private ReadSession? _session;
    private ProposalEnvelope? _proposal;
    private ValidationResult? _validation;
    private string? _exportPath;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _writing;
    private readonly bool _smoke;
    private Point _dragStart;

    public MainWindow(SessionStore store, bool demo=false)
    {
        _store=store; _smoke=demo; _settings=demo ? new AppSettings() : store.LoadSettings();
        InitializeComponent();
        VersionText.Text=$"Timekeeper {UpdatesWindow.VersionLabel} · Windows";
        DateOnly today;
        try { today=_settings.Today(); } catch { today=DateOnly.FromDateTime(DateTime.Today); }
        StartDate.SelectedDate=EndDate.SelectedDate=today.ToDateTime(TimeOnly.MinValue);
        RefreshProfile();
        Appearance.Attach(this);
        Closing+=OnClosing;
        if(demo) LoadDemo();
        else
        {
            var previous=_store.LoadLatestSession(_settings.ProfileKey);
            if(previous is not null && !previous.Demo && JsonSerializer.Serialize(previous.Settings,JsonDefaults.Options)==JsonSerializer.Serialize(_settings,JsonDefaults.Options))
            {
                PresentSession(previous);
                StatusText.Text="Previous read restored. Read again if your time has changed; files expire after 24 hours.";
            }
        }
    }
    private void RefreshProfile()
    {
        ProfileLabel.Text=string.IsNullOrEmpty(_settings.Email)?"Set up your account":_settings.Email;
        TargetMetric.Text=$"{_settings.TargetHours:0.00} h";
    }
    private void Updates_Click(object sender,RoutedEventArgs e)
    {
        if(_busy) { StatusText.Text="Finish the current read or write before updating Timekeeper."; return; }
        new UpdatesWindow(this).ShowDialog();
    }
    private void Appearance_Click(object sender,RoutedEventArgs e)
    {
        if(_busy) return;
        new AppearanceWindow(this).ShowDialog();
    }
    private bool Configured()
    {
        if(Rules.ValidateSettings(_settings).Count==0) return true;
        Settings_Click(this,new RoutedEventArgs());
        return Rules.ValidateSettings(_settings).Count==0;
    }
    private TimecardApi MakeApi(AppSettings? settings=null)
    {
        var profile=settings??_settings;
        var credentials=CredentialVault.Load(profile);
        if(string.IsNullOrWhiteSpace(credentials.TogglToken)||string.IsNullOrWhiteSpace(credentials.QuickbaseToken)) throw new InvalidOperationException("Open Settings to save and test your API tokens.");
        return new TimecardApi(profile,credentials);
    }
    private async Task RunAsync(Func<CancellationToken,Task> action, bool cancellable=false)
    {
        if(_busy) return;
        _busy=true; _cancellation=new(); BusyBar.Visibility=Visibility.Visible; CancelButton.Visibility=cancellable?Visibility.Visible:Visibility.Collapsed; UpdateEnabled();
        try { await action(_cancellation.Token); }
        catch(OperationCanceledException) { StatusText.Text="Read cancelled. No new data is ready to use."; }
        catch(Exception ex)
        {
            StatusText.Text=ex.Message;
            new TextDialog(this,"Action needs attention",ex.Message).ShowDialog();
        }
        finally { _busy=false; _writing=false; _cancellation.Dispose(); _cancellation=null; BusyBar.Visibility=CancelButton.Visibility=Visibility.Collapsed; UpdateEnabled(); }
    }
    private void UpdateEnabled()
    {
        ReadTodayButton.IsEnabled=ReadRangeButton.IsEnabled=!_busy;
        ImportButton.IsEnabled=PasteButton.IsEnabled=FindButton.IsEnabled=!_busy&&_session!=null;
        SaveExportButton.IsEnabled=!_busy&&_exportPath!=null;
        RowDetailsButton.IsEnabled=!_busy&&_validation is { IsValid:true }&&_validation.Rows.Count>0;
        ExportCard.Cursor=!_busy&&_exportPath!=null?Cursors.Hand:Cursors.Arrow;
        ReconcileButton.IsEnabled=ExportReceiptButton.IsEnabled=!_busy&&HistoryGrid.SelectedItem is SubmissionReceipt;
        WriteButton.IsEnabled=!_busy&&_session is { Demo:false }&&_proposal!=null&&_validation is { IsValid:true }&&_validation.Rows.Count>0&&(_validation.Warnings.Count==0||AcknowledgeWarnings.IsChecked==true);
    }
    private void ResetReview()
    {
        _proposal=null; _validation=null; ReviewGrid.ItemsSource=null; ReviewGrid.Visibility=VerifiedBadge.Visibility=WarningsText.Visibility=AcknowledgeWarnings.Visibility=Visibility.Collapsed;
        AcknowledgeWarnings.IsChecked=false; ReviewTitle.Text="Waiting for your proposal"; ValidationMessage.Text="The app checks the returned file and calculates your time before you submit."; ValidationMessage.Foreground=(Brush)FindResource("Muted"); DailyTotals.Text=""; ReadyMetric.Text="—"; WriteHint.Text="Nothing is sent until you click Write to Quickbase."; UpdateEnabled();
    }
    private void ResetSource()
    {
        _session=null; _exportPath=null; ResetReview(); TrackedMetric.Text=ExistingMetric.Text="—"; SourceDetails.Text="";
        ExportTitle.Text="Your file will appear here"; ExportDetail.Text="Start by reading your time above."; DemoBanner.Visibility=Visibility.Collapsed;
    }
    private async void ReadToday_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||!Configured()) return;
        var today=_settings.Today(); StartDate.SelectedDate=EndDate.SelectedDate=today.ToDateTime(TimeOnly.MinValue);
        await ReadAsync(today,today);
    }
    private async void ReadRange_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||!Configured()) return;
        if(StartDate.SelectedDate is not DateTime start||EndDate.SelectedDate is not DateTime end) { StatusText.Text="Choose both a start and end date."; return; }
        await ReadAsync(DateOnly.FromDateTime(start),DateOnly.FromDateTime(end));
    }
    private async Task ReadAsync(DateOnly start,DateOnly end)
    {
        if(end<start||end.DayNumber-start.DayNumber>=31) { StatusText.Text="Choose a date range of 1 to 31 days."; return; }
        ShowDay_Click(this,new RoutedEventArgs()); ResetSource();
        await RunAsync(async ct=>
        {
            using var api=MakeApi();
            var progress=new Progress<string>(message=>StatusText.Text=message);
            var session=await api.ReadAsync(start,end,progress,ct);
            _store.SaveSession(session); PresentSession(session);
            StatusText.Text=$"Read complete · {session.Days.Sum(d=>d.Entries.Count)} entries. Drag your file into your Copilot agent.";
        },true);
    }
    private void PresentSession(ReadSession session)
    {
        _session=session; ResetReview();
        var start=session.Days.Min(d=>d.Date); var end=session.Days.Max(d=>d.Date);
        StartDate.SelectedDate=start.ToDateTime(TimeOnly.MinValue); EndDate.SelectedDate=end.ToDateTime(TimeOnly.MinValue);
        _exportPath=_store.GetExportPath(session);
        WriteExport(_exportPath,ProposalExchange.Export(session));
        ExportTitle.Text="Drag this file to Copilot";
        ExportDetail.Text=$"{start:MMM d}{(start==end?"":$" – {end:MMM d}")} · {session.Days.Sum(d=>d.Entries.Count)} entries · JSON file\nOr save it and attach it in your agent.";
        TrackedMetric.Text=$"{session.Days.Sum(d=>d.Entries.Where(e=>!e.Running).Sum(e=>e.DurationSeconds/3600m)):0.00} h";
        ExistingMetric.Text=$"{session.Days.Sum(d=>d.Existing.Sum(e=>e.Hours)):0.00} h";
        TargetMetric.Text=$"{session.Settings.TargetHours:0.00} h";
        DemoBanner.Visibility=session.Demo?Visibility.Visible:Visibility.Collapsed;
        var source=new StringBuilder();
        foreach(var day in session.Days)
        {
            source.AppendLine($"{day.Date:dddd, MMMM d} · {day.Entries.Count} Toggl entries");
            foreach(var entry in day.Entries) source.AppendLine($"  #{entry.Id}   {(entry.Running?"RUNNING":$"{entry.DurationSeconds/3600m:0.00} h")}   {entry.Description}");
            source.AppendLine($"Already in Quickbase: {day.Existing.Sum(r=>r.Hours):0.00} h");
            foreach(var card in day.Existing) source.AppendLine($"  Record #{card.RecordId}   {card.Hours:0.00} h   {card.Description}   [project {card.Project}, task {card.Task}]");
            source.AppendLine();
        }
        SourceDetails.Text=source.ToString(); UpdateEnabled();
    }
    private static void WriteExport(string path,string data)
    {
        var temp=path+".tmp";
        File.WriteAllText(temp,data,new UTF8Encoding(false)); File.Move(temp,path,true);
    }
    private void LoadDemo()
    {
        if(_busy) return;
        ShowDay_Click(this,new RoutedEventArgs()); var demo=DemoData.CreateSession(); PresentSession(demo);
        AcceptProposal(JsonSerializer.Serialize(DemoData.CreateProposal(demo),JsonDefaults.Options));
        StatusText.Text="Sample day loaded · Try exporting or importing a proposal. Live writes are disabled.";
    }
    private void Demo_Click(object sender,RoutedEventArgs e)=>LoadDemo();
    private void Export_MouseDown(object sender,MouseButtonEventArgs e)=>_dragStart=e.GetPosition(this);
    private void Export_MouseMove(object sender,MouseEventArgs e)
    {
        if(_busy||_exportPath is null||e.LeftButton!=MouseButtonState.Pressed) return;
        var current=e.GetPosition(this);
        if(Math.Abs(current.X-_dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(current.Y-_dragStart.Y)<SystemParameters.MinimumVerticalDragDistance) return;
        var data=new DataObject(DataFormats.FileDrop,new[]{_exportPath});
        DragDrop.DoDragDrop(ExportCard,data,DragDropEffects.Copy);
    }
    private void SaveExport_Click(object sender,RoutedEventArgs e)
    {
        if(_exportPath is null) return;
        var dialog=new SaveFileDialog { Title="Save your Copilot file",Filter="JSON file|*.json",FileName=$"timekeeper-{_session!.Days[0].Date:yyyy-MM-dd}.json" };
        if(dialog.ShowDialog(this)==true) try { File.Copy(_exportPath,dialog.FileName,true); StatusText.Text="Export saved. Attach it to your Copilot agent."; } catch(Exception ex) { StatusText.Text=ex.Message; }
    }
    private void OpenCopilot_Click(object sender,RoutedEventArgs e)
    {
        if(!Uri.TryCreate(_settings.CopilotUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)) { StatusText.Text="Enter an HTTPS Copilot agent link in Settings."; return; }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute=true }); } catch(Exception ex) { StatusText.Text="Could not open Copilot: "+ex.Message; }
    }
    private void Import_DragOver(object sender,DragEventArgs e)
    {
        e.Effects=!_busy&&_session!=null&&(e.Data.GetDataPresent(DataFormats.FileDrop)||e.Data.GetDataPresent(DataFormats.UnicodeText))?DragDropEffects.Copy:DragDropEffects.None; e.Handled=true;
        ImportCard.Background=(Brush)FindResource(e.Effects==DragDropEffects.Copy?"AccentSoft":"SurfaceMuted");
        ImportCard.BorderBrush=(Brush)FindResource(e.Effects==DragDropEffects.Copy?"Accent":"Line");
    }
    private void Import_DragLeave(object sender,DragEventArgs e)
    {
        ImportCard.Background=(Brush)FindResource("SurfaceMuted"); ImportCard.BorderBrush=(Brush)FindResource("Line");
    }
    private void Import_Drop(object sender,DragEventArgs e)
    {
        Import_DragLeave(sender,e); e.Handled=true; if(_busy||_session is null) return;
        if(e.Data.GetData(DataFormats.FileDrop) is string[] files)
        { if(files.Length==1) ImportFile(files[0]); else StatusText.Text="Drop one proposal file at a time."; }
        else if(e.Data.GetData(DataFormats.UnicodeText) is string text) AcceptProposal(text);
    }
    private void Import_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_session is null) return;
        var dialog=new OpenFileDialog { Title="Open Copilot's returned proposal",Filter="Copilot proposal|*.json;*.txt",Multiselect=false };
        if(dialog.ShowDialog(this)==true) ImportFile(dialog.FileName);
    }
    private void ImportFile(string path)
    {
        ResetReview();
        try
        {
            if(!new[]{".json",".txt"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)) throw new FormatException("Download Copilot's JSON file first, then drop the saved file here.");
            if(new FileInfo(path).Length>2_000_000) throw new FormatException("The proposal is too large. The limit is 2 MB.");
            AcceptProposal(File.ReadAllText(path));
        }
        catch(Exception ex) { ImportError(ex.Message); }
    }
    private void Paste_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_session is null) return;
        var dialog=new TextDialog(this,"Paste Copilot's proposal","",true);
        if(dialog.ShowDialog()==true) AcceptProposal(dialog.Value);
    }
    private void AcceptProposal(string json)
    {
        ResetReview(); if(_session is null) return;
        try
        {
            _proposal=ProposalExchange.Parse(json); _validation=Rules.Validate(_session,_proposal);
            if(!_validation.IsValid) { ImportError(string.Join("\n",_validation.Errors)); return; }
            ReviewGrid.ItemsSource=_validation.Rows; ReviewGrid.SelectedIndex=_validation.Rows.Count>0?0:-1; ReviewGrid.Visibility=Visibility.Visible;
            ReviewTitle.Text=_validation.Rows.Count==0?"Everything is already accounted for":$"{_validation.Rows.Count} timecards ready for your review";
            VerifiedBadge.Visibility=Visibility.Visible;
            ValidationMessage.Text="Source entries, relationships and calculated hours checked. Review the assignment choices and descriptions below.";
            ReadyMetric.Text=$"{_validation.Rows.Sum(r=>r.Hours):0.00} h";
            DailyTotals.Text=string.Join("\n",_session.Days.Select(day=>$"{day.Date:MMM d}:  {day.Existing.Sum(c=>c.Hours):0.00} h existing + {_validation.Rows.Where(r=>r.Date==day.Date).Sum(r=>r.Hours):0.00} h new = {day.Existing.Sum(c=>c.Hours)+_validation.Rows.Where(r=>r.Date==day.Date).Sum(r=>r.Hours):0.00} h total"));
            if(_validation.Warnings.Count>0)
            {
                WarningsText.Text=string.Join("\n\n",_validation.Warnings); WarningsText.Visibility=AcknowledgeWarnings.Visibility=Visibility.Visible;
                VerifiedBadge.Visibility=Visibility.Collapsed;
                ValidationMessage.Text="Checks passed with items requiring your confirmation below. Review them before writing.";
            }
            WriteHint.Text=_session.Demo?"Demo only · Quickbase writing is disabled.":$"Submit as {_session.Settings.Email}";
            StatusText.Text=_validation.Rows.Count==0?"No new rows to write.":"Proposal checked. Review the rows before writing.";
        }
        catch(Exception ex) { ImportError(ex.Message); }
        UpdateEnabled();
    }
    private void ImportError(string message)
    {
        _validation=null; _proposal=null; VerifiedBadge.Visibility=ReviewGrid.Visibility=Visibility.Collapsed;
        ReviewTitle.Text="This proposal needs a correction"; ValidationMessage.Text=message; ValidationMessage.Foreground=(Brush)FindResource("Danger"); StatusText.Text="Nothing was submitted. Correct the proposal and import it again."; UpdateEnabled();
    }
    private void Approval_Changed(object sender,RoutedEventArgs e)
    {
        if(_validation is { IsValid:true }) VerifiedBadge.Visibility=_validation.Warnings.Count==0||AcknowledgeWarnings.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        UpdateEnabled();
    }
    private async void Write_Click(object sender,RoutedEventArgs e)
    {
        if(!WriteButton.IsEnabled||_session is null||_proposal is null||_validation is null) return;
        var session=_session; var proposal=_proposal; var rows=_validation.Rows.ToList(); var warningsAcknowledged=AcknowledgeWarnings.IsChecked==true;
        await RunAsync(async ct=>
        {
            _writing=true; StatusText.Text="Rechecking current data, then writing the reviewed rows…";
            using var api=MakeApi();
            var result=await new SubmissionService(api,_store).SubmitAsync(session,proposal,rows,ct,warningsAcknowledged: warningsAcknowledged);
            ResetReview(); ReviewTitle.Text=result.Status=="complete"?"Your timecards are submitted":"Review the submission result"; ValidationMessage.Text=result.Message;
            StatusText.Text=result.Message;
            new TextDialog(this,"Quickbase submission receipt",ReceiptText(result)).ShowDialog();
        });
        // An exception may follow a successful server write. History is authoritative before any further attempt.
        if(_store.LoadReceipts(_settings.ProfileKey).Any(r=>r.SessionId==session.SessionId))
        { _validation=null; _proposal=null; UpdateEnabled(); }
    }
    private async void Find_Click(object sender,RoutedEventArgs e)
    {
        if(_session is null||_busy) return;
        if(_session.Demo) { new TextDialog(this,"Demo assignments",JsonSerializer.Serialize(_session.Reference.Assignments,JsonDefaults.Options)).ShowDialog(); return; }
        string? query=TextDialog.Ask(this,"Find an assignment","Enter a few words from the assignment, project, or client name.");
        if(string.IsNullOrWhiteSpace(query)) return;
        await RunAsync(async ct=>
        {
            using var api=MakeApi(); StatusText.Text="Searching Quickbase assignments…";
            var matches=await api.FindAssignmentsAsync(query,ct);
            if(matches.Count==0) { StatusText.Text="No assignments matched. Try fewer words."; return; }
            if(matches.Count>200) throw new InvalidOperationException("More than 200 assignments matched. Use more specific search words.");
            var merged=_session.Reference.Assignments.Concat(matches).GroupBy(a=>a.Id).Select(g=>g.Last()).ToList();
            _session=_session with { SessionId=Guid.NewGuid().ToString("N"),Reference=_session.Reference with { Assignments=merged } };
            _store.SaveSession(_session); PresentSession(_session);
            new TextDialog(this,"Assignments added to a new export",$"Found {matches.Count} assignments. Upload the updated export to Copilot and request a new proposal; the previous proposal will no longer match.\n\n"+JsonSerializer.Serialize(matches,JsonDefaults.Options)).ShowDialog();
            StatusText.Text="Search results added. Drag the updated file to Copilot.";
        },true);
    }
    private void Settings_Click(object sender,RoutedEventArgs e)
    {
        if(_busy) return;
        var dialog=new SettingsWindow(this,_settings,_store);
        if(dialog.ShowDialog()==true&&dialog.SavedSettings is AppSettings saved)
        { _settings=saved; ResetSource(); RefreshProfile(); StatusText.Text="Settings saved. Read today's data to begin."; }
    }
    private void Help_Click(object sender,RoutedEventArgs e)
    {
        var help=Path.Combine(AppContext.BaseDirectory,"Help");
        var setup=Path.Combine(help,"GETTING_STARTED.md");
        string text=File.Exists(setup)?File.ReadAllText(setup):"Save your own API tokens and your team's shared Copilot agent link in Settings. Test connections and confirm your accounts. Read your day, drag the export to the shared agent, and return its proposal for review. You do not need to create an agent.";
        new TextDialog(this,"Getting started",text).ShowDialog();
    }
    private void RowDetails_Click(object sender,RoutedEventArgs e)
    {
        if(ReviewGrid.SelectedItem is not VerifiedRow row) { StatusText.Text="Select a row in the review table first."; return; }
        new TextDialog(this,"Timecard details",$"Date: {row.Date:yyyy-MM-dd}\nHours: {row.Hours:0.00}\nType: {row.Kind}\n\nProject: {row.ProjectName} (ID {row.Project})\nAssignment: {row.AssignmentName} (ID {row.Assignment?.ToString()??"none"})\nTask: {row.TaskName} (ID {row.Task})\nCategory: {row.CategoryName} (ID {row.Category})\n\nDescription:\n{row.Description}\n\nToggl source IDs: {string.Join(", ",row.SourceEntryIds)}").ShowDialog();
    }
    private void ShowDay_Click(object sender,RoutedEventArgs e)
    {
        WorkScroll.Visibility=Visibility.Visible; HistoryPanel.Visibility=Visibility.Collapsed; PageEyebrow.Text="TOGGL / QUICKBASE"; PageTitle.Text="Timecards"; PageSubtitle.Text="Read Toggl entries, review the proposal, and submit to Quickbase.";
        DayNav.Tag="Active"; HistoryNav.Tag=null;
        if(!_busy) StatusText.Text=_validation is { IsValid:true }?"Proposal checked. Review the rows before writing.":_session is not null?"Your time is ready. Send the file to Copilot to continue.":"Ready · Set up your accounts, or explore a sample day.";
    }
    private void History_Click(object sender,RoutedEventArgs e)
    {
        if(_busy) return;
        try
        {
            HistoryGrid.ItemsSource=_store.LoadReceipts(_settings.ProfileKey); WorkScroll.Visibility=Visibility.Collapsed; HistoryPanel.Visibility=Visibility.Visible;
            HistoryEmpty.Visibility=HistoryGrid.Items.Count==0?Visibility.Visible:Visibility.Collapsed;
            PageEyebrow.Text="QUICKBASE / HISTORY"; PageTitle.Text="Submission history"; PageSubtitle.Text="Submission results and saved receipts.";
            DayNav.Tag=null; HistoryNav.Tag="Active"; UpdateEnabled();
            StatusText.Text=HistoryGrid.Items.Count==0?"No submissions yet. Your receipts will be saved here.":$"{HistoryGrid.Items.Count} saved submissions · Check uncertain results before retrying.";
        }
        catch(Exception ex) { StatusText.Text=ex.Message; }
    }
    private void History_SelectionChanged(object sender,SelectionChangedEventArgs e)
    { ReceiptDetails.Text=HistoryGrid.SelectedItem is SubmissionReceipt receipt?ReceiptText(receipt):"Select a receipt to see its record IDs and row results."; UpdateEnabled(); }
    private static string ReceiptText(SubmissionReceipt receipt)
    {
        var text=new StringBuilder($"{receipt.Status.ToUpperInvariant()} · {receipt.StartedAtUtc.LocalDateTime:g}\n{receipt.Message}\nSubmission: {receipt.SubmissionId}\n\n");
        if(receipt.ReviewedWarnings.Count>0) text.AppendLine($"Warnings acknowledged: {receipt.WarningsAcknowledgedAtUtc:g}\n{string.Join("\n",receipt.ReviewedWarnings)}\n");
        foreach(var result in receipt.Rows) text.AppendLine($"{result.Status.ToUpperInvariant()}  {result.Row.Date:yyyy-MM-dd}  {result.Row.Hours:0.00} h  {result.Row.Description}\n{(result.RecordId.HasValue?$"Quickbase record #{result.RecordId} · ":"")}{result.Message}\n");
        return text.ToString();
    }
    private async void Reconcile_Click(object sender,RoutedEventArgs e)
    {
        if(HistoryGrid.SelectedItem is not SubmissionReceipt receipt||_busy) return;
        await RunAsync(async ct=>
        {
            var session=_store.LoadSession(receipt.SessionId)??throw new InvalidOperationException("The original source session is missing.");
            using var api=MakeApi(session.Settings); StatusText.Text="Checking Quickbase against the original submission…";
            var result=await new SubmissionService(api,_store).ReconcileAsync(session,receipt,ct);
            HistoryGrid.ItemsSource=_store.LoadReceipts(_settings.ProfileKey); ReceiptDetails.Text=ReceiptText(result); StatusText.Text=result.Message;
        });
    }
    private void ExportReceipt_Click(object sender,RoutedEventArgs e)
    {
        if(HistoryGrid.SelectedItem is not SubmissionReceipt receipt) return;
        var dialog=new SaveFileDialog { Filter="JSON receipt|*.json",FileName=$"timekeeper-receipt-{receipt.StartedAtUtc:yyyyMMdd-HHmm}.json" };
        if(dialog.ShowDialog(this)==true) try { File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(receipt,JsonDefaults.Options)); } catch(Exception ex) { StatusText.Text=ex.Message; }
    }
    private void Cancel_Click(object sender,RoutedEventArgs e)=>_cancellation?.Cancel();
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_busy) { e.Cancel=true; StatusText.Text=_writing?"Wait for the submission result before closing. Its outcome is being saved.":"Wait for the current action, or cancel the read before closing."; }
    }
    internal void ShowSmokeEmptyState()
    {
        if(!_smoke) throw new InvalidOperationException("Synthetic UI states are only available in smoke mode.");
        ResetSource(); RefreshProfile(); ShowDay_Click(this,new RoutedEventArgs());
        StartDate.SelectedDate=EndDate.SelectedDate=_settings.Today().ToDateTime(TimeOnly.MinValue);
        StatusText.Text="Ready · Set up your accounts, or explore a sample day.";
    }
    internal void ShowSmokeHistory()
    {
        if(!_smoke) throw new InvalidOperationException("Synthetic UI states are only available in smoke mode.");
        History_Click(this,new RoutedEventArgs());
    }
    public string SmokeSummary()
    {
        if(_session is not { Demo:true }||_validation is not { IsValid:true }||WriteButton.IsEnabled||ReviewGrid.Items.Count==0||_exportPath is null||!File.Exists(_exportPath)) throw new InvalidOperationException("UI smoke checks failed.");
        var original=_session;
        var summary=$"PASS: demo session loaded\nPASS: source export created\nPASS: proposal parsed and validated\nPASS: {_validation.Rows.Count} rows displayed\nPASS: Quickbase writes disabled in demo\nTotal new hours: {_validation.Rows.Sum(r=>r.Hours):0.00}\n";
        AcceptProposal("{\"rows\": []}");
        if(_validation!=null||_proposal!=null||WriteButton.IsEnabled||VerifiedBadge.Visibility!=Visibility.Collapsed) throw new InvalidOperationException("An invalid import kept an old approval.");
        summary+="PASS: invalid import clears previous verification\n";
        var returnFile=Path.Combine(_store.RootPath,"smoke-proposal.json");
        File.WriteAllText(returnFile,JsonSerializer.Serialize(DemoData.CreateProposal(original),JsonDefaults.Options));
        ImportFile(returnFile);
        if(_validation is not { IsValid:true }) throw new InvalidOperationException("Real file import failed.");
        summary+="PASS: returned proposal imports from a real file\n";
        var warning=original with { Settings=original.Settings with { TargetHours=12m } };
        // Use a weekday fixture even when the test is run on a weekend.
        if(warning.Days[0].Date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
        {
            PresentSession(warning); AcceptProposal(JsonSerializer.Serialize(DemoData.CreateProposal(warning),JsonDefaults.Options));
            if(_validation is not { IsValid:true }||_validation.Warnings.Count==0||VerifiedBadge.Visibility!=Visibility.Collapsed||WriteButton.IsEnabled) throw new InvalidOperationException("Warning acknowledgment gate failed.");
            AcknowledgeWarnings.IsChecked=true;
            if(VerifiedBadge.Visibility!=Visibility.Visible||WriteButton.IsEnabled) throw new InvalidOperationException("Warning acknowledgment enabled a demo write.");
            summary+="PASS: warnings require acknowledgment; demo still cannot write\n";
        }
        PresentSession(original); AcceptProposal(JsonSerializer.Serialize(DemoData.CreateProposal(original),JsonDefaults.Options));
        return summary;
    }
}
