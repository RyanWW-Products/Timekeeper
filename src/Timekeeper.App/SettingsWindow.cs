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
    private readonly CheckBox _add = new() { Content="Add a Timecards entry on weekdays" }, _fill = new() { Content="Fill remaining weekday hours with Misc internal" }, _identity = new() { Content="The verified Toggl and Quickbase accounts shown below are mine." };
    private readonly TextBlock _status = new() { Text="Test both connections to confirm your identity before saving.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,14,0,12) };
    private readonly Button _test = new() { Content="Test connections" }, _save=new() { Content="Save settings" };
    private string? _testedConfiguration;
    private string _detectedEmployeeId;
    private string? _testedToggl, _testedQuickbase;
    public AppSettings? SavedSettings { get; private set; }

    public SettingsWindow(Window owner, AppSettings settings, SessionStore store, bool readCredentials=true)
    {
        if(readCredentials) Owner=owner;
        Style=(Style)Application.Current.FindResource(typeof(Window));
        _original=settings; _store=store; _detectedEmployeeId=settings.EmployeeId; Title="Timekeeper · Settings"; Width=720; Height=830; MinWidth=600; MinHeight=600; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var outer=new Grid { Margin=new Thickness(28) };
        outer.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition()); outer.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        outer.Children.Add(new TextBlock { Text="Set up your workspace", FontSize=26, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,18) });
        var panel=_inputPanel; var scroll=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(0,0,14,0) }; Grid.SetRow(scroll,1); outer.Children.Add(scroll);
        Heading(panel,"1. Your accounts");
        AddField(panel,"realm","Quickbase realm",settings.Realm);
        AddField(panel,"email","Quickbase sign-in email",settings.Email);
        panel.Children.Add(Label("Toggl API token")); panel.Children.Add(_toggl);
        panel.Children.Add(Label("Quickbase user token")); panel.Children.Add(_quickbase);
        var tokenHelp=new Button { Content="How to get a Quickbase token ↗",FontSize=12,Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Left };
        tokenHelp.Click+=(_,_)=> { try { Process.Start(new ProcessStartInfo("https://help.quickbase.com/docs/create-and-use-user-tokens") { UseShellExecute=true }); } catch(Exception ex) { _status.Text=ex.Message; } };
        panel.Children.Add(tokenHelp);
        panel.Children.Add(new TextBlock { Text="Tokens are saved in Windows Credential Manager. They are never included in the Copilot file.",FontSize=12,Foreground=Brushes.SlateGray,Margin=new Thickness(0,7,0,8),TextWrapping=TextWrapping.Wrap });
        _identity.Margin=new Thickness(0,8,0,10); panel.Children.Add(_identity);
        AddField(panel,"copilot","Shared Copilot agent link",settings.CopilotUrl,"Use the link supplied by your team. You do not need to create an agent.");
        if(readCredentials) try { var credentials=CredentialVault.Load(settings); _toggl.Password=credentials.TogglToken; _quickbase.Password=credentials.QuickbaseToken; } catch (Exception ex) { _status.Text=ex.Message; }
        Heading(panel,"2. Daily preferences");
        _add.IsChecked=settings.AddTimecards; _fill.IsChecked=settings.FillWeekdays; _add.Margin=new Thickness(0,4,0,8); _fill.Margin=new Thickness(0,12,0,8);
        panel.Children.Add(_add); AddField(panel,"allowance","Timecards hours",settings.TimecardsHours.ToString("0.00",CultureInfo.InvariantCulture));
        panel.Children.Add(_fill); AddField(panel,"target","Weekday fill target (hours)",settings.TargetHours.ToString("0.00",CultureInfo.InvariantCulture));
        panel.Children.Add(new TextBlock { Text="Worked hours are never reduced. 9 hours + 0.17 Timecards = 9.17 hours. Weekends contain actual work only.",FontSize=12,Foreground=Brushes.SlateGray,Margin=new Thickness(0,8,0,8),TextWrapping=TextWrapping.Wrap });
        AddField(panel,"zone","Time zone",settings.TimeZoneId,"Example: Eastern Standard Time. This sets calendar dates, not working hours.");
        AddField(panel,"project","Internal Quickbase project ID",settings.InternalProjectId==0?"":settings.InternalProjectId.ToString(),"Enter an exact ID, or leave blank to look up the name below.");
        AddField(panel,"projectSearch","Internal project name",settings.InternalProjectSearch);
        var advanced=new StackPanel();
        AddField(advanced,"internalTask","Task ID for automatic internal rows",settings.InternalTaskId.ToString());
        AddField(advanced,"timecardsTable","Timecards table ID",settings.TimecardsTable); AddField(advanced,"tasksTable","Tasks table ID",settings.TasksTable); AddField(advanced,"projectsTable","Projects table ID",settings.ProjectsTable); AddField(advanced,"assignmentsTable","Assignments table ID",settings.AssignmentsTable); AddField(advanced,"categoriesTable","Categories table ID",settings.CategoriesTable);
        panel.Children.Add(new Expander { Header="Advanced · Case Manager table configuration",Content=advanced,Margin=new Thickness(0,20,0,0) });
        var footer=new StackPanel(); footer.Children.Add(_status);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        _test.Margin=new Thickness(0,0,10,0); _test.Click+=Test_Click; _save.Style=(Style)FindResource("Primary"); _save.Click+=Save_Click; buttons.Children.Add(_test); buttons.Children.Add(_save); footer.Children.Add(buttons); Grid.SetRow(footer,2); outer.Children.Add(footer); Content=outer;
        _fields["email"].TextChanged+=(_,_)=>ClearDetectedIdentity();
        _fields["realm"].TextChanged+=(_,_)=>ClearDetectedIdentity();
        _quickbase.PasswordChanged+=(_,_)=>ClearDetectedIdentity();
        _toggl.PasswordChanged+=(_,_)=> { _testedConfiguration=null; _identity.IsChecked=false; };
    }
    private static TextBlock Label(string value)=>new() { Text=value,FontWeight=FontWeights.SemiBold,FontSize=12,Margin=new Thickness(0,12,0,5) };
    private static void Heading(Panel panel,string value)=>panel.Children.Add(new TextBlock { Text=value,FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,18,0,5) });
    private void AddField(Panel panel,string key,string label,string value,string? help=null)
    {
        panel.Children.Add(Label(label)); var field=new TextBox { Text=value }; _fields[key]=field; panel.Children.Add(field);
        if(help!=null) panel.Children.Add(new TextBlock { Text=help,FontSize=11,Foreground=Brushes.SlateGray,Margin=new Thickness(0,4,0,0),TextWrapping=TextWrapping.Wrap });
    }
    private void ClearDetectedIdentity()
    {
        _detectedEmployeeId=""; _testedConfiguration=null; _identity.IsChecked=false;
        _status.Text="Test connections to verify both accounts.";
    }
    private AppSettings Collect(bool allowMissingEmployeeId=false)
    {
        string Get(string key)=>_fields[key].Text.Trim();
        decimal Number(string key)=>decimal.TryParse(Get(key),NumberStyles.Number,CultureInfo.InvariantCulture,out var value)?value:throw new ArgumentException($"Enter a valid number for {key}.");
        int Id(string key,bool optional=false)=> optional&&Get(key)==""?0:int.TryParse(Get(key),out var value)?value:throw new ArgumentException($"Enter a valid ID for {key}.");
        var result=_original with { Realm=Get("realm").ToLowerInvariant(),Email=Get("email"),EmployeeId=_detectedEmployeeId,TimeZoneId=Get("zone"),InternalProjectId=Id("project",true),InternalProjectSearch=Get("projectSearch"),InternalTaskId=Id("internalTask"),TimecardsHours=Number("allowance"),TargetHours=Number("target"),AddTimecards=_add.IsChecked==true,FillWeekdays=_fill.IsChecked==true,CopilotUrl=Get("copilot"),TimecardsTable=Get("timecardsTable"),TasksTable=Get("tasksTable"),ProjectsTable=Get("projectsTable"),AssignmentsTable=Get("assignmentsTable"),CategoriesTable=Get("categoriesTable") };
        var errors=Rules.ValidateSettings(result,allowMissingEmployeeId); if(errors.Count>0) throw new ArgumentException(string.Join("\n",errors));
        if(string.IsNullOrWhiteSpace(_toggl.Password)||string.IsNullOrWhiteSpace(_quickbase.Password)) throw new ArgumentException("Enter both API tokens.");
        return result;
    }
    private async void Test_Click(object sender,RoutedEventArgs e)
    {
        _test.IsEnabled=false; _save.IsEnabled=false; _inputPanel.IsEnabled=false; _testedConfiguration=null;
        try
        {
            var settings=Collect(allowMissingEmployeeId:true); var credentials=new Credentials(_toggl.Password.Trim(),_quickbase.Password.Trim());
            _status.Text="Verifying your Quickbase account…";
            var detected=await TimecardApi.DiscoverQuickbaseIdentityAsync(settings,credentials);
            _detectedEmployeeId=detected.EmployeeId;
            settings=Collect();
            _status.Text="Checking both accounts and your internal project…";
            using var api=new TimecardApi(settings,credentials);
            var identity=await api.TestAsync();
            var reference=await api.ReadReferenceAsync(default);
            if(reference.InternalProject is null) throw new InvalidOperationException("No internal project was found. Enter an exact project ID.");
            _fields["project"].Text=reference.InternalProject.Id.ToString();
            settings=Collect();
            _testedConfiguration=System.Text.Json.JsonSerializer.Serialize(settings,JsonDefaults.Options); _testedToggl=credentials.TogglToken; _testedQuickbase=credentials.QuickbaseToken;
            _status.Text=$"✓ {identity}\nInternal project: {reference.InternalProject.Name} ({reference.InternalProject.Id}). Confirm your identity and save.";
        }
        catch(Exception ex) { _status.Text=ex.Message; }
        finally { _test.IsEnabled=true; _save.IsEnabled=true; _inputPanel.IsEnabled=true; }
    }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var settings=Collect();
            if(_identity.IsChecked!=true) throw new ArgumentException("Confirm that the verified Toggl and Quickbase accounts belong to you.");
            if(_testedConfiguration!=System.Text.Json.JsonSerializer.Serialize(settings,JsonDefaults.Options)||_testedToggl!=_toggl.Password.Trim()||_testedQuickbase!=_quickbase.Password.Trim()) throw new ArgumentException("Test connections with these settings before saving.");
            CredentialVault.Save(settings,new Credentials(_toggl.Password.Trim(),_quickbase.Password.Trim())); _store.SaveSettings(settings); SavedSettings=settings; DialogResult=true; Close();
        }
        catch(Exception ex) { _status.Text=ex.Message; }
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
        _detectedEmployeeId="123.test"; _=Collect();
        _testedConfiguration="synthetic-tested-state"; _identity.IsChecked=true;
        _quickbase.Password="changed-synthetic-token";
        if(_detectedEmployeeId!=""||_testedConfiguration!=null||_identity.IsChecked==true) throw new InvalidOperationException("Changing the token retained a stale identity.");
        _fields["email"].Text=""; _toggl.Password=""; _quickbase.Password="";
        return "PASS: setup accepts blank ID for discovery only\nPASS: detected user ID is kept out of the settings form\nPASS: token changes clear detected ID and verification\n";
    }
}
