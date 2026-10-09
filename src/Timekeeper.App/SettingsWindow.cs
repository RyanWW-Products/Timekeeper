using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly SessionStore _store;
    private readonly StackPanel _inputPanel = new();
    private readonly Dictionary<string,TextBox> _fields=[];
    private readonly PasswordBox _toggl = new(), _quickbase = new();
    private readonly CheckBox _emailDefaults = new() { Content="Apply my Timecards and weekday fill settings to email imports" };
    private readonly ComboBox _emailRounding = new() { ItemsSource=new[]{"Use confirmed minutes", "Round grouped time up to 5 minutes", "Round grouped time up to 15 minutes"} };
    private readonly CheckBox _add = new() { Content="Add a Timecards entry on weekdays" }, _fill = new() { Content="Fill remaining weekday hours with Misc internal" }, _identity = new() { Content="The verified Toggl and Quickbase accounts shown below are mine." };
    private readonly TextBox _status = new() { Text="Test connections to confirm your identity before saving.", IsReadOnly=true, TextWrapping=TextWrapping.Wrap, BorderThickness=new Thickness(0), Background=Brushes.Transparent, Padding=new Thickness(0), MaxHeight=180, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, FontSize=12, MinHeight=20 };
    private readonly Border _statusCard = new() { CornerRadius=new CornerRadius(9), Padding=new Thickness(14,12,14,12), Margin=new Thickness(0,16,0,12) };
    private readonly Button _test = new() { Content="Test connections" }, _save=new() { Content="Save settings" };
    private readonly Button _copyError = new() { Content="Copy error details", Visibility=Visibility.Collapsed }, _openRecord = new() { Content="Open Quickbase record ↗", Visibility=Visibility.Collapsed };
    private string? _recordUrl;
    private string? _testedConfiguration;
    private string _detectedEmployeeId;
    private string? _testedToggl, _testedQuickbase;
    public AppSettings? SavedSettings { get; private set; }

    public SettingsWindow(Window owner, AppSettings settings, SessionStore store, bool readCredentials=true)
    {
        if(readCredentials) Owner=owner;
        Style=(Style)Application.Current.FindResource(typeof(Window));
        _original=settings; _store=store; _detectedEmployeeId=settings.EmployeeId; Title="Timekeeper · Settings"; Width=760; Height=870; MinWidth=600; MinHeight=600; MaxHeight=SystemParameters.WorkArea.Height; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var outer=new Grid { Margin=new Thickness(28,24,28,24) };
        outer.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition()); outer.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new StackPanel { Margin=new Thickness(0,0,0,20) };
        header.Children.Add(new TextBlock { Text="CONFIGURATION",Style=(Style)FindResource("SectionLabel"),Margin=new Thickness(0,0,0,8) });
        header.Children.Add(new TextBlock { Text="Settings",FontFamily=Font("DisplayFont"),FontSize=32 });
        header.Children.Add(Hint("Accounts, time policy and Quickbase configuration.",new Thickness(0,8,0,0)));
        var appearanceLink=new Button { Content="Appearance settings",Style=(Style)FindResource("Quiet"),FontSize=12,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(0,6,0,6),Margin=new Thickness(0,6,0,0) };
        appearanceLink.Click+=(_,_)=>new AppearanceWindow(this).ShowDialog(); header.Children.Add(appearanceLink);
        outer.Children.Add(header);
        var panel=_inputPanel; var scroll=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(0,0,12,0) }; Grid.SetRow(scroll,1); outer.Children.Add(scroll);
        var accounts=Section(panel,"01 / CONNECTIONS","Accounts");
        var accountFields=Columns(accounts);
        AddField(accountFields.Left,"realm","Quickbase realm",settings.Realm);
        AddField(accountFields.Right,"email","Quickbase sign-in email",settings.Email);
        accounts.Children.Add(new Border { Background=Brush("AccentSoft"),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,12,0,8),Child=Hint("Email-only setup: leave the Toggl token blank. Test connections will verify Quickbase only. A Toggl account is needed only to read Toggl time.") });
        var tokenFields=Columns(accounts);
        tokenFields.Left.Children.Add(Label("Toggl API token (optional for email imports)")); tokenFields.Left.Children.Add(_toggl);
        tokenFields.Right.Children.Add(Label("Quickbase user token")); tokenFields.Right.Children.Add(_quickbase);
        var tokenHelp=new Button { Content="Get a Quickbase token ↗",Style=(Style)FindResource("Quiet"),FontSize=12,Padding=new Thickness(0,7,0,7),Margin=new Thickness(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left };
        tokenHelp.Click+=(_,_)=> { try { Process.Start(new ProcessStartInfo("https://help.quickbase.com/docs/create-and-use-user-tokens") { UseShellExecute=true }); } catch(Exception ex) { ShowError(ex); } };
        accounts.Children.Add(tokenHelp);
        accounts.Children.Add(Hint("Saved in Windows Credential Manager. Tokens are never included in your Copilot file.",new Thickness(0,2,0,12)));
        _identity.Content=new TextBlock { Text="The verified accounts are mine.",TextWrapping=TextWrapping.Wrap,FontSize=12 }; _identity.Margin=new Thickness(0,4,0,0); accounts.Children.Add(_identity);
        var copilot=Section(panel,"02 / ASSISTANT","Shared Copilot agent");
        AddField(copilot,"copilot","Agent link",settings.CopilotUrl,"Use your team's shared link. Your agent is already set up for you.");
        if(readCredentials) try { var credentials=CredentialVault.Load(settings); _toggl.Password=credentials.TogglToken; _quickbase.Password=credentials.QuickbaseToken; } catch (Exception ex) { _status.Text=ex.Message; }
        var preferences=Section(panel,"03 / DAILY DEFAULTS","Time policy");
        _add.IsChecked=settings.AddTimecards; _fill.IsChecked=settings.FillWeekdays;
        AddPreference(preferences,_add,"Add a Timecards entry on weekdays","allowance","Timecards hours",settings.TimecardsHours);
        AddPreference(preferences,_fill,"Fill remaining weekday hours with Misc internal","target","Fill target (hours)",settings.TargetHours);
        preferences.Children.Add(new Border { Background=Brush("AccentSoft"),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,14,0,4),Child=Hint("Worked hours are always kept. 9 hours + 0.17 Timecards = 9.17 hours. Weekends contain actual work only.") });
        AddField(preferences,"zone","Time zone",settings.TimeZoneId,"Example: Eastern Standard Time. Sets calendar dates, not working hours.");
        preferences.Children.Add(Label("Email imports"));
        _emailDefaults.IsChecked=settings.EmailApplyDailyDefaults; _emailDefaults.Margin=new Thickness(0,4,0,12);
        _emailDefaults.Content=new TextBlock { Text="Apply my Timecards and weekday fill settings to email imports",TextWrapping=TextWrapping.Wrap };
        preferences.Children.Add(_emailDefaults);
        _emailRounding.SelectedIndex=settings.EmailRoundingMinutes==0?0:settings.EmailRoundingMinutes==15?2:1;
        preferences.Children.Add(_emailRounding);
        preferences.Children.Add(Hint("Email imports use confirmed minutes. Rounding applies once after related activity is grouped. Automatic additions are off by default.",new Thickness(0,8,0,0)));
        var internalProject=Section(panel,"04 / QUICKBASE","Internal time");
        AddField(internalProject,"projectSearch","Internal project name",settings.InternalProjectSearch);
        AddField(internalProject,"project","Internal project ID",settings.InternalProjectId==0?"":settings.InternalProjectId.ToString(),"Enter an exact ID, or leave blank to look up the name above.");
        var advanced=new StackPanel { Margin=new Thickness(0,12,0,0) };
        AddField(advanced,"internalTask","Task ID for automatic internal rows",settings.InternalTaskId.ToString());
        AddField(advanced,"timecardsTable","Timecards table ID",settings.TimecardsTable); AddField(advanced,"tasksTable","Tasks table ID",settings.TasksTable); AddField(advanced,"projectsTable","Projects table ID",settings.ProjectsTable); AddField(advanced,"assignmentsTable","Assignments table ID",settings.AssignmentsTable); AddField(advanced,"categoriesTable","Categories table ID",settings.CategoriesTable);
        internalProject.Children.Add(new Expander { Header="Advanced table configuration",Content=advanced,Margin=new Thickness(0,20,0,0) });
        var footer=new StackPanel(); _statusCard.Background=Brush("SurfaceMuted"); _status.Foreground=Brush("Muted"); _statusCard.Child=_status; footer.Children.Add(_statusCard);
        var errorActions=new WrapPanel();
        _copyError.FontSize=12; _copyError.Padding=new Thickness(12,8,12,8); _copyError.Margin=new Thickness(0,0,8,12); _copyError.Click+=(_,_)=> { try { Clipboard.SetText(_status.Text); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Could not copy error"); } };
        _openRecord.FontSize=12; _openRecord.Padding=new Thickness(12,8,12,8); _openRecord.Margin=new Thickness(0,0,0,12); _openRecord.Click+=(_,_)=> { if(_recordUrl is null) return; try { Process.Start(new ProcessStartInfo(_recordUrl) { UseShellExecute=true }); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Could not open record"); } };
        errorActions.Children.Add(_copyError); errorActions.Children.Add(_openRecord); footer.Children.Add(errorActions);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        _test.Margin=new Thickness(0,0,10,0); _test.Click+=Test_Click; _save.Style=(Style)FindResource("Primary"); _save.Click+=Save_Click; buttons.Children.Add(_test); buttons.Children.Add(_save); footer.Children.Add(buttons); Grid.SetRow(footer,2); outer.Children.Add(footer); Content=outer;
        _fields["email"].TextChanged+=(_,_)=>ClearDetectedIdentity();
        _fields["realm"].TextChanged+=(_,_)=>ClearDetectedIdentity();
        _quickbase.PasswordChanged+=(_,_)=>ClearDetectedIdentity();
        _toggl.PasswordChanged+=(_,_)=> { _testedConfiguration=null; _identity.IsChecked=false; ClearErrorActions(); _status.Text="Test connections to verify your accounts."; };
        Appearance.Attach(this);
    }
    private static Brush Brush(string key)=>(Brush)Application.Current.FindResource(key);
    private static FontFamily Font(string key)=>(FontFamily)Application.Current.FindResource(key);
    private static TextBlock Label(string value)=>new() { Text=value,FontWeight=FontWeights.SemiBold,FontSize=12,Margin=new Thickness(0,14,0,6) };
    private static TextBlock Hint(string value,Thickness margin=default)=>new() { Text=value,FontSize=12,Foreground=Brush("Muted"),Margin=margin,TextWrapping=TextWrapping.Wrap,LineHeight=18 };
    private static StackPanel Section(Panel parent,string marker,string title)
    {
        var panel=new StackPanel();
        panel.Children.Add(new TextBlock { Text=marker,Style=(Style)Application.Current.FindResource("SectionLabel"),Margin=new Thickness(0,0,0,7) });
        panel.Children.Add(new TextBlock { Text=title,FontFamily=Font("DisplayFont"),FontSize=21 });
        parent.Children.Add(new Border { Style=(Style)Application.Current.FindResource("Card"),Padding=new Thickness(20),Margin=new Thickness(0,0,0,14),Child=panel });
        return panel;
    }
    private static (StackPanel Left,StackPanel Right) Columns(Panel parent)
    {
        var grid=new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(16) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left=new StackPanel(); var right=new StackPanel(); Grid.SetColumn(right,2); grid.Children.Add(left); grid.Children.Add(right); parent.Children.Add(grid); return (left,right);
    }
    private void AddPreference(Panel parent,CheckBox toggle,string title,string key,string label,decimal value)
    {
        var grid=new Grid { Margin=new Thickness(0,10,0,0) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(136) });
        toggle.Content=new TextBlock { Text=title,TextWrapping=TextWrapping.Wrap,FontSize=13 }; toggle.Margin=new Thickness(0,15,18,0); toggle.VerticalAlignment=VerticalAlignment.Center; grid.Children.Add(toggle);
        var field=new StackPanel(); AddField(field,key,label,value.ToString("0.00",CultureInfo.InvariantCulture)); _fields[key].FontFamily=Font("MonoFont"); Grid.SetColumn(field,1); grid.Children.Add(field); parent.Children.Add(grid);
    }
    private void AddField(Panel panel,string key,string label,string value,string? help=null)
    {
        panel.Children.Add(Label(label)); var field=new TextBox { Text=value }; _fields[key]=field; panel.Children.Add(field);
        if(help!=null) panel.Children.Add(Hint(help,new Thickness(0,6,0,0)));
    }
    private void ClearDetectedIdentity()
    {
        ClearErrorActions();
        _detectedEmployeeId=""; _testedConfiguration=null; _identity.IsChecked=false;
        _status.Text="Test connections to verify your accounts.";
    }
    private void ClearErrorActions()
    {
        _recordUrl=null; _copyError.Visibility=_openRecord.Visibility=Visibility.Collapsed;
        _statusCard.Background=Brush("SurfaceMuted"); _status.Foreground=Brush("Muted");
    }
    private void ShowError(Exception error)
    {
        _status.Text=error.Message; _copyError.Visibility=Visibility.Visible;
        _statusCard.Background=Brush("DangerSoft"); _status.Foreground=Brush("Danger");
        _recordUrl=(error as QuickbaseDataException)?.RecordUrl;
        _openRecord.Visibility=_recordUrl is null?Visibility.Collapsed:Visibility.Visible;
    }
    private AppSettings Collect(bool allowMissingEmployeeId=false)
    {
        string Get(string key)=>_fields[key].Text.Trim();
        decimal Number(string key)=>decimal.TryParse(Get(key),NumberStyles.Number,CultureInfo.InvariantCulture,out var value)?value:throw new ArgumentException($"Enter a valid number for {key}.");
        int Id(string key,bool optional=false)=> optional&&Get(key)==""?0:int.TryParse(Get(key),out var value)?value:throw new ArgumentException($"Enter a valid ID for {key}.");
        var result=_original with { Realm=Get("realm").ToLowerInvariant(),Email=Get("email"),EmployeeId=_detectedEmployeeId,TimeZoneId=Get("zone"),InternalProjectId=Id("project",true),InternalProjectSearch=Get("projectSearch"),InternalTaskId=Id("internalTask"),TimecardsHours=Number("allowance"),TargetHours=Number("target"),AddTimecards=_add.IsChecked==true,FillWeekdays=_fill.IsChecked==true,CopilotUrl=Get("copilot"),TimecardsTable=Get("timecardsTable"),TasksTable=Get("tasksTable"),ProjectsTable=Get("projectsTable"),AssignmentsTable=Get("assignmentsTable"),CategoriesTable=Get("categoriesTable") };
        result=result with { EmailApplyDailyDefaults=_emailDefaults.IsChecked==true,EmailRoundingMinutes=_emailRounding.SelectedIndex switch { 0=>0,2=>15,_=>5 } };
        var errors=Rules.ValidateSettings(result,allowMissingEmployeeId); if(errors.Count>0) throw new ArgumentException(string.Join("\n",errors));
        if(string.IsNullOrWhiteSpace(_quickbase.Password)) throw new ArgumentException("Enter your Quickbase user token. Toggl is optional for email imports.");
        return result;
    }
    private async void Test_Click(object sender,RoutedEventArgs e)
    {
        ClearErrorActions();
        _test.IsEnabled=false; _save.IsEnabled=false; _inputPanel.IsEnabled=false; _testedConfiguration=null;
        try
        {
            var settings=Collect(allowMissingEmployeeId:true); var credentials=new Credentials(_toggl.Password.Trim(),_quickbase.Password.Trim());
            _status.Text="Verifying your Quickbase account…";
            var detected=await TimecardApi.DiscoverQuickbaseIdentityAsync(settings,credentials);
            _detectedEmployeeId=detected.EmployeeId;
            settings=Collect();
            _status.Text="Checking your accounts and internal project…";
            using var api=new TimecardApi(settings,credentials);
            var identity=string.IsNullOrWhiteSpace(credentials.TogglToken)?await api.TestQuickbaseAsync():await api.TestAsync();
            var reference=await api.ReadReferenceAsync(default);
            if(reference.InternalProject is null) throw new InvalidOperationException("No internal project was found. Enter an exact project ID.");
            _fields["project"].Text=reference.InternalProject.Id.ToString();
            settings=Collect();
            _testedConfiguration=System.Text.Json.JsonSerializer.Serialize(settings,JsonDefaults.Options); _testedToggl=credentials.TogglToken; _testedQuickbase=credentials.QuickbaseToken;
            _status.Text=$"✓ {identity}\nInternal project: {reference.InternalProject.Name} ({reference.InternalProject.Id}). Confirm your identity and save.";
            _statusCard.Background=Brush("SuccessSoft"); _status.Foreground=Brush("Success");
        }
        catch(Exception ex) { ShowError(ex); }
        finally { _test.IsEnabled=true; _save.IsEnabled=true; _inputPanel.IsEnabled=true; }
    }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        ClearErrorActions();
        try
        {
            var settings=Collect();
            if(_identity.IsChecked!=true) throw new ArgumentException("Confirm that the verified accounts belong to you.");
            if(_testedConfiguration!=System.Text.Json.JsonSerializer.Serialize(settings,JsonDefaults.Options)||_testedToggl!=_toggl.Password.Trim()||_testedQuickbase!=_quickbase.Password.Trim()) throw new ArgumentException("Test connections with these settings before saving.");
            CredentialVault.Save(settings,new Credentials(_toggl.Password.Trim(),_quickbase.Password.Trim())); _store.SaveSettings(settings); SavedSettings=settings; DialogResult=true; Close();
        }
        catch(Exception ex) { ShowError(ex); }
    }
    internal string SmokeSummary()
    {
        if(_fields.ContainsKey("employee")) throw new InvalidOperationException("Quickbase ID must not appear in the settings form.");
        _fields["email"].Text="smoke@example.com"; _toggl.Password="synthetic-token"; _quickbase.Password="synthetic-token";
        var pending=Collect(allowMissingEmployeeId:true);
        if(pending.EmployeeId!="") throw new InvalidOperationException("Setup did not start with an empty identity.");
        bool blocked=false;
        try { _=Collect(); } catch(ArgumentException) { blocked=true; }
        if(!blocked) throw new InvalidOperationException("Unverified setup can be saved.");
        _toggl.Password=""; _detectedEmployeeId="123.test"; _=Collect();
        _testedConfiguration="synthetic-tested-state"; _identity.IsChecked=true;
        _quickbase.Password="changed-synthetic-token";
        if(_detectedEmployeeId!=""||_testedConfiguration!=null||_identity.IsChecked==true) throw new InvalidOperationException("Changing the token retained a stale identity.");
        _fields["email"].Text=""; _toggl.Password=""; _quickbase.Password="";
        ShowSampleError();
        if(_copyError.Visibility!=Visibility.Visible||_openRecord.Visibility!=Visibility.Visible||!_status.IsReadOnly) throw new InvalidOperationException("Record errors must be selectable, copyable and linked.");
        ShowError(new InvalidOperationException("Synthetic generic error"));
        if(_recordUrl!=null||_openRecord.Visibility!=Visibility.Collapsed) throw new InvalidOperationException("An unrelated error retained a stale record link.");
        ClearErrorActions(); _status.Text="Test connections to verify your accounts.";
        return "PASS: setup accepts a blank Toggl token for email-only use\nPASS: setup accepts blank ID for discovery only\nPASS: detected user ID is kept out of the settings form\nPASS: token changes clear detected ID and verification\nPASS: detailed errors are copyable and record links cannot go stale\n";
    }
    internal void ShowSampleError() => ShowError(new QuickbaseDataException("Quickbase could not read Category.\nTable: Tasks (example123)\nRecord: #44 — Sample internal task\nField: Category (field 17)\nReceived: null (blank)\nExpected: a positive whole-number record ID.\nCheck this record and the Quickbase table/field mapping.","https://demo.quickbase.com/db/example123?a=dr&rid=44"));
}
