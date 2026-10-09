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
    private List<ReviewRow> _reviewRows = [];
    private bool _refreshingBilling;
    private List<ReviewDuration> _durationEdits = [];
    private IReadOnlyCollection<DateOnly>? _selectedDates;
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
        if(string.IsNullOrWhiteSpace(credentials.QuickbaseToken)) throw new InvalidOperationException("Open Settings to save and test your Quickbase token.");
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
        ImportButton.IsEnabled=EmailImportButton.IsEnabled=!_busy;
        PasteButton.IsEnabled=FindButton.IsEnabled=!_busy&&_session is { EmailWorkbook:null };
        ReviewGrid.IsEnabled=!_busy;
        SaveExportButton.IsEnabled=!_busy&&_exportPath!=null;
        RowDetailsButton.IsEnabled=!_busy&&_validation is { IsValid:true }&&_validation.Rows.Count>0;
        ChangeProposalDatesButton.IsEnabled=!_busy&&_proposal is not null;
        ExportCard.Cursor=!_busy&&_exportPath!=null?Cursors.Hand:Cursors.Arrow;
        ReconcileButton.IsEnabled=ExportReceiptButton.IsEnabled=!_busy&&HistoryGrid.SelectedItem is SubmissionReceipt;
        RecoverDeletedButton.IsEnabled=!_busy&&!_smoke&&HistoryGrid.SelectedItem is SubmissionReceipt selected
            &&!selected.Rows.Any(r=>r.Status is "pending" or "unknown")
            &&selected.Rows.Any(r=>r.Status=="created"&&r.RecordId>0&&r.Row.Kind=="work"&&(r.Row.SourceEntryIds.Count>0||r.Row.EmailEvidenceKeys.Count>0)&&r.DeletionConfirmedAtUtc is null);
        WriteButton.IsEnabled=!_busy&&_session is { Demo:false }&&_proposal!=null&&_validation is { IsValid:true }&&_validation.Rows.Count>0&&(_validation.Warnings.Count==0||AcknowledgeWarnings.IsChecked==true)&&_reviewRows.All(r=>r.Row.BillableOverride is null||r.CanOverride);
    }
    private void ResetReview()
    {
        _selectedDates=null; ProposalDatesPanel.Visibility=Visibility.Collapsed;
        if(_session is not null) UpdateMetrics(_session);
        _proposal=null; _validation=null; _reviewRows=[]; _durationEdits=[]; ReviewGrid.ItemsSource=null; ReviewGrid.Visibility=VerifiedBadge.Visibility=WarningsText.Visibility=AcknowledgeWarnings.Visibility=BillingNotice.Visibility=Visibility.Collapsed;
        AcknowledgeWarnings.IsChecked=false; ReviewTitle.Text="Waiting for your proposal"; ValidationMessage.Text="The app checks the returned file and calculates your time before you submit."; ValidationMessage.Foreground=(Brush)FindResource("Muted"); DailyTotals.Text=""; ReadyMetric.Text="—"; WriteHint.Text="Nothing is sent until you click Write to Quickbase."; UpdateEnabled();
    }
    private void ResetSource()
    {
        CopilotRefreshNotice.Visibility=Visibility.Collapsed;
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
        CopilotRefreshNotice.Visibility=Visibility.Collapsed;
        _session=session; ResetReview();
        var start=session.Days.Min(d=>d.Date); var end=session.Days.Max(d=>d.Date);
        StartDate.SelectedDate=start.ToDateTime(TimeOnly.MinValue); EndDate.SelectedDate=end.ToDateTime(TimeOnly.MinValue);
        if (session.EmailWorkbook is null)
        {
            _exportPath=_store.GetExportPath(session);
            WriteExport(_exportPath,ProposalExchange.Export(session));
            ExportTitle.Text="Drag this file to Copilot";
            ExportDetail.Text=$"{start:MMM d}{(start==end?"":$" – {end:MMM d}")} · {session.Days.Sum(d=>d.Entries.Count)} entries · JSON file\nOr save it and attach it in your agent.";
        }
        else
        {
            _exportPath=null; ExportTitle.Text="Email workbook imported";
            ExportDetail.Text=$"{start:MMM d}{(start==end?"":$" – {end:MMM d}")} · {session.EmailWorkbook.Activities.Count} activities\nReview the matched timecards below.";
        }
        UpdateMetrics(session);
        ExistingMetric.Text=$"{session.Days.Sum(d=>d.Existing.Sum(e=>e.Hours)):0.00} h";
        DemoBanner.Visibility=session.Demo?Visibility.Visible:Visibility.Collapsed;
        var source=new StringBuilder();
        foreach(var day in session.Days)
        {
            source.AppendLine($"{day.Date:dddd, MMMM d}");
            if(session.EmailWorkbook is not null)
                foreach(var activity in session.EmailWorkbook.Activities.Where(a=>a.Date==day.Date)) source.AppendLine($"  {activity.ActivityId}   {activity.Minutes} confirmed minutes   {activity.TimeBasis}\n  {activity.Description}");
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
        e.Effects=!_busy&&(e.Data.GetDataPresent(DataFormats.FileDrop)||(_session is {EmailWorkbook:null}&&e.Data.GetDataPresent(DataFormats.UnicodeText)))?DragDropEffects.Copy:DragDropEffects.None; e.Handled=true;
        ImportCard.Background=(Brush)FindResource(e.Effects==DragDropEffects.Copy?"AccentSoft":"SurfaceMuted");
        ImportCard.BorderBrush=(Brush)FindResource(e.Effects==DragDropEffects.Copy?"Accent":"Line");
    }
    private void Import_DragLeave(object sender,DragEventArgs e)
    {
        ImportCard.Background=(Brush)FindResource("SurfaceMuted"); ImportCard.BorderBrush=(Brush)FindResource("Line");
    }
    private void Import_Drop(object sender,DragEventArgs e)
    {
        Import_DragLeave(sender,e); e.Handled=true; if(_busy) return;
        if(e.Data.GetData(DataFormats.FileDrop) is string[] files)
        { if(files.Length==1) ImportFile(files[0]); else StatusText.Text="Drop one proposal file at a time."; }
        else if(e.Data.GetData(DataFormats.UnicodeText) is string text) AcceptProposal(text);
    }
    private void Import_Click(object sender,RoutedEventArgs e)
    {
        if(_busy) return;
        var dialog=new OpenFileDialog { Title="Open Copilot's file",Filter="Timekeeper files|*.xlsx;*.json;*.txt|Email workbook|*.xlsx|Toggl proposal|*.json;*.txt",Multiselect=false };
        if(dialog.ShowDialog(this)==true) ImportFile(dialog.FileName);
    }
    private void ImportFile(string path)
    {
        if(Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase)) { _=ImportEmailAsync(path); return; }
        ResetReview();
        try
        {
            if(_session is null) throw new FormatException("Read your Toggl time first, or import an email .xlsx workbook.");
            if(_session.EmailWorkbook is not null) throw new FormatException("Import the email workbook again to change its activities or mappings.");
            if(!new[]{".json",".txt"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)) throw new FormatException("Download Copilot's JSON file first, then drop the saved file here.");
            if(new FileInfo(path).Length>2_000_000) throw new FormatException("The proposal is too large. The limit is 2 MB.");
            AcceptProposal(File.ReadAllText(path));
        }
        catch(Exception ex) { ImportError(ex.Message); }
    }
    private static bool TryGetEmailDrop(IDataObject data,out string path)
    {
        path="";
        if(!data.GetDataPresent(DataFormats.FileDrop)||data.GetData(DataFormats.FileDrop) is not string[] files||files.Length!=1) return false;
        if(!Path.GetExtension(files[0]).Equals(".xlsx",StringComparison.OrdinalIgnoreCase)||!File.Exists(files[0])) return false;
        path=files[0]; return true;
    }
    private void EmailImport_DragOver(object sender,DragEventArgs e)
    {
        var accepted=!_busy&&(e.AllowedEffects&DragDropEffects.Copy)!=0&&TryGetEmailDrop(e.Data,out _);
        e.Effects=accepted?DragDropEffects.Copy:DragDropEffects.None; e.Handled=true;
        EmailImportButton.Tag=accepted?"DropReady":null;
    }
    private void EmailImport_DragLeave(object sender,DragEventArgs e)
    {
        EmailImportButton.Tag=null; e.Handled=true;
    }
    private void EmailImport_Drop(object sender,DragEventArgs e)
    {
        EmailImportButton.Tag=null; e.Effects=DragDropEffects.None; e.Handled=true;
        if(_busy||(e.AllowedEffects&DragDropEffects.Copy)==0) return;
        if(!TryGetEmailDrop(e.Data,out var path)) { StatusText.Text="Drop one email Excel workbook (.xlsx) here."; return; }
        e.Effects=DragDropEffects.Copy;
        _=ImportEmailAsync(path);
    }
    private void EmailImport_Click(object sender,RoutedEventArgs e)
    {
        if(_busy) return;
        var dialog=new OpenFileDialog { Title="Import email activities", Filter="Email activity workbook|*.xlsx", Multiselect=false };
        if(dialog.ShowDialog(this)==true) _=ImportEmailAsync(dialog.FileName);
    }
    private async Task ImportEmailAsync(string path)
    {
        if(_busy||!Configured()) return;
        ShowDay_Click(this,new RoutedEventArgs()); ResetSource();
        await RunAsync(async ct=>
        {
            StatusText.Text="Checking the email workbook…";
            var workbook=EmailWorkbookReader.Read(path);
            if(workbook.IsReviewWorkbook)
            {
                var errors=EmailWorkbookReader.Validate(workbook,_settings,allowReviewDraft:true);
                if(errors.Count>0) throw new InvalidDataException(string.Join("\n",errors));
                var confirmation=new ReviewWorkbookWindow(this,workbook);
                if(confirmation.ShowDialog()!=true) { StatusText.Text="Review workbook cancelled. Nothing was submitted."; return; }
                workbook=EmailWorkbookReader.ConfirmReview(workbook);
            }
            using var api=MakeApi();
            var session=await api.ReadEmailBaselineAsync(workbook,new Progress<string>(text=>StatusText.Text=text),ct);
            var fullSession=session;
            IReadOnlyCollection<DateOnly>? importDates=null;
            if(session.Days.Count>1)
            {
                var outline=new ProposalEnvelope { SessionId=session.SessionId,EmployeeId=session.Settings.EmployeeId,Rows=workbook.Activities.Select(a=>new ProposalRow { Date=a.Date }).ToList() };
                var dates=new ProposalDatesWindow(this,session,outline);
                if(dates.ShowDialog()!=true) { StatusText.Text="Email import cancelled. Nothing was submitted."; return; }
                importDates=dates.SelectedDates;
                session=session with { Days=session.Days.Where(d=>dates.SelectedDates.Contains(d.Date)).ToList() };
            }
            var dialog=new EmailImportWindow(this,session,api);
            if(dialog.ShowDialog()!=true) { StatusText.Text="Email import cancelled. Nothing was submitted."; return; }
            fullSession=fullSession with { Reference=dialog.Session.Reference };
            _store.SaveSession(fullSession); PresentSession(fullSession);
            AcceptProposal("",(_,_)=>importDates,forceDateChoice:importDates is not null,emailProposal:dialog.Proposal);
        },true);
    }
    private void BillingChanged()
    {
        if(_refreshingBilling||_session is null||_proposal is null) return;
        _refreshingBilling=true;
        try
        {
            var choices=_reviewRows.ToList();
            var dates=_selectedDates?.ToArray();
            var updated=_proposal with { Rows=_proposal.Rows.Select(p=>
            {
                var choice=choices.FirstOrDefault(r=>r.Row.Kind=="work"&&(p.SourceEntryIds.Any(r.Row.SourceEntryIds.Contains)||p.SourceActivityIds.Any(r.Row.SourceActivityIds.Contains)));
                return choice is null?p:p with { BillableOverride=choice.Row.BillableOverride,Hours=null };
            }).ToList() };
            // Billing defines an email rounding group. Recalculate after changes so two groups
            // that now share billing cannot retain inflated separately-rounded hours.
            AcceptProposal(JsonSerializer.Serialize(updated,JsonDefaults.Options),(_,_)=>dates??_session.Days.Select(d=>d.Date).ToArray(),dates is not null,emailProposal:_session.EmailWorkbook is null?null:updated);
            foreach(var row in _reviewRows.Where(r=>r.Row.Kind!="work"))
            {
                var previous=choices.FirstOrDefault(r=>r.Row.Kind==row.Row.Kind&&r.Row.Date==row.Row.Date);
                if(previous is not null) row.BillingIndex=previous.BillingIndex;
            }
            AcknowledgeWarnings.IsChecked=false;
            StatusText.Text="Billing updated and hours recalculated. Any manual duration edits were reset. Review the rows and totals before writing.";
        }
        finally { _refreshingBilling=false; UpdateEnabled(); }
    }
    private void Paste_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_session is null) return;
        var dialog=new TextDialog(this,"Paste Copilot's proposal","",true);
        if(dialog.ShowDialog()==true) AcceptProposal(dialog.Value);
    }
    private void AcceptProposal(string json, Func<ReadSession,ProposalEnvelope,IReadOnlyCollection<DateOnly>?>? chooseDates=null, bool forceDateChoice=false, ProposalEnvelope? emailProposal=null, IReadOnlyList<ReviewDuration>? durationEdits=null)
    {
        ResetReview(); if(_session is null) return;
        try
        {
            if(emailProposal is not null && _session.EmailWorkbook is null) throw new InvalidOperationException("Email mappings require an imported workbook.");
            _proposal=emailProposal??ProposalExchange.Parse(json);
            if(forceDateChoice||ProposalScope.NeedsChoice(_session,_proposal))
            {
                _selectedDates=(chooseDates??ChooseProposalDates)(_session,_proposal);
                if(_selectedDates is null) { ResetReview(); StatusText.Text="Date selection cancelled. Nothing was submitted."; return; }
            }
            var scope=ProposalScope.Select(_session,_proposal,_selectedDates);
            _durationEdits=durationEdits?.ToList()??[];
            _validation=ReviewDurations.Apply(scope.Session,Rules.Validate(scope.Session,scope.Proposal),_durationEdits);
            if(!_validation.IsValid) { ImportError(string.Join("\n",_validation.Errors)); return; }
            if(CopilotRefreshNotice.Visibility==Visibility.Visible)
            {
                CopilotRefreshNotice.Visibility=Visibility.Collapsed;
                ExportTitle.Text="Updated proposal received";
                ExportDetail.Text="Review your new timecards below.";
            }
            UpdateMetrics(scope.Session);
            if(_session.Days.Count>1)
            {
                var included=scope.Session.Days.Select(d=>d.Date).ToHashSet();
                var excluded=_session.Days.Where(d=>!included.Contains(d.Date)).Select(d=>d.Date).ToList();
                ProposalDatesText.Text="Reviewing: "+DateList(included)+(excluded.Count>0?". Excluded: "+DateList(excluded)+".":". All dates from this read are included.");
                ProposalDatesPanel.Visibility=Visibility.Visible;
            }
            _reviewRows=_validation.Rows.Select(r=>new ReviewRow(r,_session.Reference.Billing?.CanOverride==true,BillingChanged,_session.Reference.Billing)).ToList();
            ReviewGrid.ItemsSource=_reviewRows; ReviewGrid.SelectedIndex=_validation.Rows.Count>0?0:-1; ReviewGrid.Visibility=Visibility.Visible;
            BillingNotice.Text=_session.Reference.Billing?.CanOverride==true ? "Billing changes keep the same project, assignment and task. Green = billable; dim red = non-billable; gray = default unavailable because Quickbase did not expose the required billing fields." : (_session.Reference.Billing?.Message??"Billing override access has not been confirmed. Read or import again to check permissions.")+" Billing is read-only. Unavailable defaults mean Quickbase did not expose the required billing fields. Clear any requested override before writing.";
            BillingNotice.Visibility=Visibility.Visible;
            ReviewTitle.Text=_validation.Rows.Count==0?"Everything is already accounted for":$"{_validation.Rows.Count} timecards ready for your review";
            VerifiedBadge.Visibility=Visibility.Visible;
            ValidationMessage.Text="Source entries, relationships and hours checked. Double-click an hours box to edit, or focus it and press F2.";
            ReadyMetric.Text=$"{_validation.Rows.Sum(r=>r.Hours):0.00} h";
            DailyTotals.Text=string.Join("\n",scope.Session.Days.Select(day=>$"{day.Date:MMM d}:  {day.Existing.Sum(c=>c.Hours):0.00} h existing + {_validation.Rows.Where(r=>r.Date==day.Date).Sum(r=>r.Hours):0.00} h new = {day.Existing.Sum(c=>c.Hours)+_validation.Rows.Where(r=>r.Date==day.Date).Sum(r=>r.Hours):0.00} h total"));
            if(_validation.Warnings.Count>0)
            {
                WarningsText.Text=string.Join("\n\n",_validation.Warnings); WarningsText.Visibility=AcknowledgeWarnings.Visibility=Visibility.Visible;
                VerifiedBadge.Visibility=Visibility.Collapsed;
                ValidationMessage.Text="Checks passed with items requiring your confirmation below. Review them before writing.";
            }
            WriteHint.Text=_session.Demo?"Demo only · Quickbase writing is disabled.":$"Submit as {_session.Settings.Email}\nDates: {DateList(scope.Session.Days.Select(d=>d.Date))}";
            StatusText.Text=_validation.Rows.Count==0?"No new rows to write.":"Proposal checked. Review the rows before writing.";
        }
        catch(Exception ex) { ImportError(ex.Message); }
        UpdateEnabled();
    }
    private static string DateList(IEnumerable<DateOnly> dates)=>string.Join(", ",dates.Order().Select(d=>d.ToString("MMM d, yyyy")));
    private void UpdateMetrics(ReadSession session)
    {
        TrackedLabel.Text=session.EmailWorkbook is null?"Tracked in Toggl":"Confirmed email time";
        var hours=session.EmailWorkbook is null?session.Days.Sum(d=>d.Entries.Where(e=>!e.Running).Sum(e=>e.DurationSeconds/3600m)):session.EmailWorkbook.Activities.Where(a=>session.Days.Any(d=>d.Date==a.Date)).Sum(a=>a.Minutes/60m);
        TrackedMetric.Text=$"{hours:0.00} h";
        TargetMetric.Text=session.EmailWorkbook is not null && (!session.Settings.EmailApplyDailyDefaults||!session.Settings.FillWeekdays)?"Off":$"{session.Settings.TargetHours:0.00} h";
        ExistingMetric.Text=$"{session.Days.Sum(d=>d.Existing.Sum(e=>e.Hours)):0.00} h";
    }
    private IReadOnlyCollection<DateOnly>? ChooseProposalDates(ReadSession session,ProposalEnvelope proposal)
    {
        var dialog=new ProposalDatesWindow(this,session,proposal);
        return dialog.ShowDialog()==true?dialog.SelectedDates:null;
    }
    private void ChangeProposalDates_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||_session is null||_proposal is null) return;
        var previous=_selectedDates;
        var billing=_reviewRows.ToDictionary(r=>ReviewIdentity(r.Row),r=>r.BillingIndex);
        AcceptProposal(JsonSerializer.Serialize(_proposal,JsonDefaults.Options),(session,proposal)=>
        {
            var dialog=new ProposalDatesWindow(this,session,proposal,previous);
            return dialog.ShowDialog()==true?dialog.SelectedDates:null;
        },true,emailProposal:_session.EmailWorkbook is null?null:_proposal);
        RestoreBillingChoices(billing);
        UpdateEnabled();
    }
    private void RestoreBillingChoices(IReadOnlyDictionary<string,int> billing)
    {
        _refreshingBilling=true;
        try
        {
            foreach(var row in _reviewRows)
                if(billing.TryGetValue(ReviewIdentity(row.Row),out var index)) row.BillingIndex=index;
        }
        finally { _refreshingBilling=false; }
    }
    private static string ReviewIdentity(VerifiedRow row)=>JsonSerializer.Serialize(row with { RowId="",BillableOverride=null },JsonDefaults.Options);
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
        var session=_session; var proposal=_proposal; var rows=_reviewRows.Select(r=>r.Row).ToList(); var warningsAcknowledged=AcknowledgeWarnings.IsChecked==true; var selectedDates=_selectedDates?.ToArray();
        await RunAsync(async ct=>
        {
            _writing=true; StatusText.Text="Rechecking current data, then writing the reviewed rows…";
            using var api=MakeApi();
            var result=await new SubmissionService(api,_store).SubmitAsync(session,proposal,rows,ct,warningsAcknowledged: warningsAcknowledged,selectedDates:selectedDates,durationEdits:_durationEdits.ToList());
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
        await RunAsync(async ct=>
        {
            using var api=_session.Demo?null:MakeApi();
            var picker=new AssignmentSearchWindow(this,_session.Reference.Assignments,
                (query,token)=>api is null?Task.FromResult(AssignmentSearchWindow.Filter(_session.Reference.Assignments,query)):api.FindAssignmentsAsync(query,token),forExport:true);
            if(picker.ShowDialog()!=true||picker.SelectedAssignment is not AssignmentRecord selected) return;
            var merged=_session.Reference.Assignments.Where(a=>a.Id!=selected.Id).Append(selected).ToList();
            var billing=api is null?_session.Reference.Billing:await api.ReadBillingDefaultsAsync(merged.Select(a=>a.ProjectId).Append(_session.Reference.InternalProject!.Id),ct);
            _session=_session with { SessionId=Guid.NewGuid().ToString("N"),Reference=_session.Reference with { Assignments=merged,Billing=billing } };
            _store.SaveSession(_session); PresentSession(_session);
            ShowAssignmentExportReminder();
            StatusText.Text=$"Assignment #{selected.Id} added. Drag the updated file into Copilot AGAIN and request a NEW proposal.";
            return;
        });
    }
    internal void ShowAssignmentExportReminder()
    {
        if(_session is null || _session.EmailWorkbook is not null || _exportPath is null) return;
        ResetReview();
        CopilotRefreshNotice.Visibility=Visibility.Visible;
        ExportTitle.Text="Drag the updated file again";
        ExportDetail.Text="Drop this file into Copilot again.\nAsk for a new proposal using the added assignment.";
        ReviewTitle.Text="Waiting for a new Copilot proposal";
        ValidationMessage.Text="Drag the updated file into Copilot again, then bring back its new proposal. The previous proposal cannot be used.";
        WorkScroll.UpdateLayout(); CopilotRefreshNotice.BringIntoView();
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
    private void ClearBilling_Click(object sender,RoutedEventArgs e)
    {
        if(!_busy && sender is FrameworkElement { DataContext: ReviewRow row }) row.BillingIndex=0;
        e.Handled=true;
    }
    private void Duration_DoubleClick(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;
        if(sender is FrameworkElement { DataContext: ReviewRow row }) EditDuration(row);
    }
    private void Duration_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.F2) return;
        e.Handled=true;
        if(sender is FrameworkElement { DataContext: ReviewRow row }) EditDuration(row);
    }
    private void EditDuration(ReviewRow row)
    {
        if(_busy||_session is null||_proposal is null||_validation is not { IsValid:true }) return;
        var dialog=new DurationEditWindow(this,row.Row);
        if(dialog.ShowDialog()==true) ApplyDuration(row,dialog.Hours);
    }
    private void ApplyDuration(ReviewRow row,decimal hours)
    {
        if(_session is null||_proposal is null) return;
        var key=ReviewDurations.Key(row.Row);
        var edits=_durationEdits.Where(x=>x.RowKey!=key).ToList();
        var original=row.Row.OriginalHours??row.Row.Hours;
        if(hours!=original) edits.Add(new(key,original,hours));
        var scope=ProposalScope.Select(_session,_proposal,_selectedDates);
        var check=ReviewDurations.Apply(scope.Session,Rules.Validate(scope.Session,scope.Proposal),edits);
        if(!check.IsValid) { new TextDialog(this,"Check edited hours",string.Join("\n",check.Errors)).ShowDialog(); return; }
        var billing=_reviewRows.ToDictionary(r=>ReviewDurations.Key(r.Row),r=>r.BillingIndex);
        var dates=_selectedDates?.ToArray(); var proposal=_proposal;
        AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options),(_,_)=>dates??_session.Days.Select(d=>d.Date).ToArray(),dates is not null,
            emailProposal:_session.EmailWorkbook is null?null:proposal,durationEdits:edits);
        _refreshingBilling=true;
        try { foreach(var r in _reviewRows) if(billing.TryGetValue(ReviewDurations.Key(r.Row),out var value)) r.BillingIndex=value; }
        finally { _refreshingBilling=false; }
        StatusText.Text="Hours updated. Review the daily totals and confirm the duration warning before writing.";
        UpdateEnabled();
    }
    private void RowDetails_Click(object sender,RoutedEventArgs e)
    {
        if(ReviewGrid.SelectedItem is not ReviewRow review) { StatusText.Text="Select a row in the review table first."; return; }
        var row=review.Row;
        var sources=row.SourceActivityIds.Count>0?$"Email activity IDs: {string.Join(", ",row.SourceActivityIds)}":$"Toggl source IDs: {string.Join(", ",row.SourceEntryIds)}";
        new TextDialog(this,"Timecard details",$"Date: {row.Date:yyyy-MM-dd}\nHours: {row.Hours:0.00}\nType: {row.Kind}\nBilling requested: {review.BillingLabel}\n\nProject: {row.ProjectName} (ID {row.Project})\nAssignment: {row.AssignmentName} (ID {row.Assignment?.ToString()??"none"})\nTask: {row.TaskName} (ID {row.Task})\nCategory: {row.CategoryName} (ID {row.Category})\n\nDescription:\n{row.Description}\n\n{sources}").ShowDialog();
    }
    private void ShowDay_Click(object sender,RoutedEventArgs e)
    {
        if(HistoryPanel.Visibility==Visibility.Visible) WorkScroll.ScrollToTop();
        WorkScroll.Visibility=Visibility.Visible; HistoryPanel.Visibility=Visibility.Collapsed; PageEyebrow.Text="TOGGL + EMAIL / QUICKBASE"; PageTitle.Text="Timecards"; PageSubtitle.Text="Read Toggl time or import an email workbook, review, and submit to Quickbase.";
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
        var text=new StringBuilder($"{receipt.Status.ToUpperInvariant()} · {receipt.StartedAtUtc.LocalDateTime:g}\n{receipt.DisplayMessage}\nSubmission: {receipt.SubmissionId}\n\n");
        if(receipt.SelectedDates is not null) text.AppendLine($"Selected dates: {DateList(receipt.SelectedDates)}\nOnly these dates were included in this submission.\n");
        if(receipt.Status=="reopened") text.AppendLine($"Original submission result: {receipt.Message}\n");
        if(receipt.ReviewedWarnings.Count>0) text.AppendLine($"Warnings acknowledged: {receipt.WarningsAcknowledgedAtUtc:g}\n{string.Join("\n",receipt.ReviewedWarnings)}\n");
        foreach(var result in receipt.Rows) text.AppendLine($"{result.Status.ToUpperInvariant()}  {result.Row.Date:yyyy-MM-dd}  {result.Row.Hours:0.00} h  {result.Row.Description}\n{(result.RecordId.HasValue?$"Quickbase record #{result.RecordId} · ":"")}{result.Message}\nBilling requested: {(result.Row.BillableOverride is null?"Quickbase default":result.Row.BillableOverride.Value?"Billable":"Non-billable")}; confirmed: {(result.ActualBillable is null?"unavailable":result.ActualBillable.Value?"Billable":"Non-billable")}\n");
        foreach(var result in receipt.Rows.Where(r=>r.Row.OriginalHours.HasValue))
            text.AppendLine($"DURATION EDIT  {result.Row.Date:yyyy-MM-dd}  {result.Row.Description}\nOriginal: {result.Row.OriginalHours:0.00} h; submitted: {result.Row.Hours:0.00} h\n");
        foreach(var result in receipt.Rows.Where(r=>r.DeletionConfirmedAtUtc.HasValue))
            text.AppendLine($"DELETION CONFIRMED  Record #{result.RecordId} · {result.DeletionConfirmedAtUtc!.Value.LocalDateTime:g}\nThe user confirmed this record was intentionally deleted; Quickbase did not return its ID. Its sources may be included in a fresh proposal. The original creation result above is retained.\n");
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
            var history=_store.LoadReceipts(_settings.ProfileKey); HistoryGrid.ItemsSource=history;
            HistoryGrid.SelectedItem=history.Single(r=>r.SubmissionId==result.SubmissionId); StatusText.Text=result.DisplayMessage;
        });
    }
    private async void RecoverDeleted_Click(object sender,RoutedEventArgs e)
    {
        if(!RecoverDeletedButton.IsEnabled||HistoryGrid.SelectedItem is not SubmissionReceipt receipt) return;
        await RunAsync(async ct=>
        {
            var session=_store.LoadSession(receipt.SessionId)??throw new InvalidOperationException("The original source session is missing.");
            using var api=MakeApi(session.Settings);
            var service=new SubmissionService(api,_store);
            StatusText.Text="Checking the original Quickbase record IDs…";
            var missing=await service.FindMissingCreatedEntriesAsync(session,receipt,ct);
            if(missing.Count==0)
            {
                new TextDialog(this,"No deleted entries found","The eligible work records in this receipt still exist in Quickbase. Read the day again and have Copilot mark matching sources as already recorded. A record with a changed date or employee still counts as an existing record.").ShowDialog();
                StatusText.Text="No entries were released for rewriting."; return;
            }
            var dialog=new DeletionRecoveryWindow(this,missing);
            if(dialog.ShowDialog()!=true) { StatusText.Text="Recovery cancelled. The receipt is unchanged."; return; }
            StatusText.Text="Rechecking selected record IDs and saving your deletion confirmation…";
            var result=await service.ConfirmDeletedEntriesAsync(session,receipt,dialog.SelectedRowIds,true,ct);
            ResetSource();
            var history=_store.LoadReceipts(_settings.ProfileKey); HistoryGrid.ItemsSource=history;
            HistoryGrid.SelectedItem=history.Single(r=>r.SubmissionId==result.SubmissionId);
            StartDate.SelectedDate=session.Days.Min(d=>d.Date).ToDateTime(TimeOnly.MinValue);
            EndDate.SelectedDate=session.Days.Max(d=>d.Date).ToDateTime(TimeOnly.MinValue);
            StatusText.Text=session.EmailWorkbook is null?"Deletion confirmed. Open Timecards, choose Read dates, and send the new export to Copilot to prepare replacement entries.":"Deletion confirmed. Open Timecards and import the email workbook again to prepare replacement entries with fresh Quickbase data.";
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
    internal string SmokeDateSelection(ReadSession session,ProposalEnvelope proposal,DateOnly selected)
    {
        if(!_smoke) throw new InvalidOperationException("Synthetic date checks require smoke mode.");
        PresentSession(session); ShowDay_Click(this,new RoutedEventArgs());
        bool asked=false;
        AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options),(_,_)=>{ asked=true; return new[]{selected}; });
        if(!asked||_validation is not { IsValid:true }||_validation.Rows.Any(r=>r.Date!=selected)||_selectedDates?.Single()!=selected||ProposalDatesPanel.Visibility!=Visibility.Visible||WriteButton.IsEnabled)
            throw new InvalidOperationException("Date choice did not restrict the reviewed day or preserve demo protection.");
        AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options),(_,_)=>session.Days.Select(d=>d.Date).ToArray());
        if(_validation is not null||WriteButton.IsEnabled||VerifiedBadge.Visibility==Visibility.Visible)
            throw new InvalidOperationException("Keeping all dates incorrectly accepted an incomplete proposal.");
        AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options),(_,_)=>null);
        if(_proposal is not null||_selectedDates is not null||WriteButton.IsEnabled||ProposalDatesPanel.Visibility!=Visibility.Collapsed)
            throw new InvalidOperationException("Cancelling date selection retained an approval.");
        AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options),(_,_)=>new[]{selected});
        return "PASS: partial-date import prompts, limits review/totals to the selected day, rejects incomplete full-range coverage and clears approval on cancel\n";
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
    internal string SmokeReviewControls()
    {
        if(!_smoke||_session is null||_proposal is null) throw new InvalidOperationException("Synthetic review checks require smoke mode.");
        ShowDay_Click(this,new RoutedEventArgs());
        var original=_session;
        var row=_reviewRows.First(r=>r.Row.Kind=="work");
        var originalHours=row.Hours;
        AcknowledgeWarnings.IsChecked=true;
        ApplyDuration(row,originalHours+0.25m);
        if(_reviewRows.First(r=>r.Row.Kind=="work").Hours!=originalHours+0.25m||_durationEdits.Count!=1||AcknowledgeWarnings.IsChecked==true||VerifiedBadge.Visibility==Visibility.Visible||WriteButton.IsEnabled)
            throw new InvalidOperationException("Duration edit did not update review or require acknowledgement.");
        var restricted=original with { Reference=original.Reference with { Billing=new BillingCapabilities { CanOverride=false,
            ProjectTypes=original.Reference.Assignments.Select(a=>a.ProjectId).Distinct().ToDictionary(id=>id,_=>"Client"),
            TaskBilling=original.Reference.Tasks.ToDictionary(t=>t.Id,_=>true) } } };
        restricted.Reference.Billing!.ProjectTypes[restricted.Reference.InternalProject!.Id]="Internal";
        PresentSession(restricted); AcceptProposal(JsonSerializer.Serialize(DemoData.CreateProposal(restricted),JsonDefaults.Options));
        if(_durationEdits.Count!=0||_reviewRows.Any(r=>r.CanOverride)) throw new InvalidOperationException("Fresh proposal retained edits or billing permission.");
        foreach(var review in _reviewRows)
        {
            var expected=review.Row.Project==restricted.Reference.InternalProject.Id?"Nonbillable - Default":"Billable - Default";
            if(review.DefaultBillingLabel!=expected) throw new InvalidOperationException("Review did not display the resolved default.");
            review.BillingIndex=1;
            if(review.Row.BillableOverride.HasValue) throw new InvalidOperationException("A restricted review accepted a billing override.");
        }
        return "PASS: duration edits require confirmation and reset on new proposals; billing default labels resolve correctly\n";
    }
    internal string SmokeRenderedReviewControls()
    {
        IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i); yield return child; foreach(var descendant in Descendants(child)) yield return descendant; }
        }
        if(!Descendants(ReviewGrid).OfType<ComboBox>().Any()||Descendants(ReviewGrid).OfType<ComboBox>().Any(c=>c.Visibility!=Visibility.Collapsed)) throw new InvalidOperationException("A billing dropdown is visible without Modify access.");
        if(!Descendants(ReviewGrid).OfType<TextBox>().Any()||Descendants(ReviewGrid).OfType<TextBox>().Any(t=>!t.IsReadOnly)) throw new InvalidOperationException("Hours were editable without a deliberate edit action.");
        return "PASS: duration edits require confirmation and reset on new proposals; restricted billing is read-only with resolved default labels\n";
    }
    internal string SmokeEmailReview(ReadSession session,ProposalEnvelope mappedProposal)
    {
        if(!_smoke||!session.Demo) throw new InvalidOperationException("Email smoke requires synthetic data.");
        PresentSession(session); ShowDay_Click(this,new RoutedEventArgs());
        AcceptProposal("",emailProposal:mappedProposal);
        if(_validation is not {IsValid:true}||_reviewRows.Count!=2||WriteButton.IsEnabled||_exportPath is not null||TargetMetric.Text!="Off")
            throw new InvalidOperationException("Email mappings did not reach review independently of the Toggl JSON parser.");
        var originalHours=_reviewRows.Sum(r=>r.Hours);
        _reviewRows[1].BillingIndex=1;
        if(_reviewRows.Count!=1||_reviewRows[0].Row.SourceActivityIds.Count!=2||_reviewRows[0].Hours!=0.08m||_reviewRows[0].Hours>=originalHours||_reviewRows[0].Row.Assignment!=501||_reviewRows[0].Row.Task!=60)
            throw new InvalidOperationException("Billing changes did not preserve mappings and recompute grouped email hours.");
        _reviewRows[0].BillingIndex=2;
        if(_reviewRows[0].Row.BillableOverride!=false||_proposal!.Rows.Any(r=>r.BillableOverride!=false)||WriteButton.IsEnabled)
            throw new InvalidOperationException("Non-billable review selection was lost or enabled a demo write.");
        var automaticRows=Enumerable.Range(0,2).Select(i=>new VerifiedRow { Date=session.Days[0].Date.AddDays(i),Kind="timecards",Hours=0.17m,Description="Timecards" }).ToList();
        _reviewRows=automaticRows.Select(r=>new ReviewRow(r,true,BillingChanged)).ToList();
        RestoreBillingChoices(automaticRows.ToDictionary(ReviewIdentity,_=>2));
        if(_reviewRows.Count!=2||_reviewRows.Any(r=>r.Row.BillableOverride!=false))
            throw new InvalidOperationException("Restoring date selections lost automatic-row billing overrides.");
        // Leave both indicator states visible in the screenshot.
        PresentSession(session); AcceptProposal("",emailProposal:EmailSmoke.Proposal(session));
        return "PASS: email mappings reach review without Toggl; no automatic fill by default\nPASS: billing edits keep assignment/task and regroup confirmed minutes; demo writes stay disabled\n";
    }
}
