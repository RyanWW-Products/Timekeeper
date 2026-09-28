using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Timekeeper.App;

internal sealed class TextDialog : Window
{
    private readonly TextBox _text;
    private readonly WrapPanel _buttons;
    public string Value => _text.Text;
    public TextDialog(Window owner, string title, string text, bool editable = false, bool smoke = false)
    {
        if(!smoke) Owner=owner;
        Style=(Style)Application.Current.FindResource(typeof(Window)); Title=title; Width=860; Height=660; MinWidth=520; MinHeight=360; MaxHeight=SystemParameters.WorkArea.Height; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin=new Thickness(28,24,28,24) };
        grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new StackPanel { Margin=new Thickness(0,0,0,22) };
        header.Children.Add(new TextBlock { Text=editable ? "COPILOT / PROPOSAL" : "TIMEKEEPER / DETAILS",Style=(Style)FindResource("SectionLabel"),Margin=new Thickness(0,0,0,9) });
        header.Children.Add(new TextBlock { Text=title, FontFamily=Font("DisplayFont"), FontSize=28 });
        if(editable) header.Children.Add(new TextBlock { Text="Paste the file contents returned by Copilot. Your timecards will be checked before you review them.",FontSize=12,Foreground=Brush("Muted"),Margin=new Thickness(0,10,0,0),LineHeight=18 });
        grid.Children.Add(header);
        _text = new TextBox { Text=text, IsReadOnly=!editable, AcceptsReturn=true, AcceptsTab=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, FontFamily=Font("MonoFont"), FontSize=13, Background=Brush("Surface"), BorderThickness=new Thickness(0), Padding=new Thickness(18), VerticalContentAlignment=VerticalAlignment.Top };
        var textCard=new Border { Style=(Style)FindResource("Card"),Padding=new Thickness(0),Child=_text }; Grid.SetRow(textCard,1); grid.Children.Add(textCard);
        var buttons = _buttons = new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,18,0,0) };
        var copy = new Button { Content="Copy text", Margin=new Thickness(0,0,10,0) }; copy.Click += (_,_) => Clipboard.SetText(_text.Text); buttons.Children.Add(copy);
        var done = new Button { Content=editable ? "Validate proposal" : "Close", IsDefault=true, Style=(Style)FindResource("Primary") }; done.Click += (_,_) => { DialogResult=editable; Close(); }; buttons.Children.Add(done);
        Grid.SetRow(buttons,2); grid.Children.Add(buttons); Content=grid;
        Appearance.Attach(this);
    }
    private static Brush Brush(string key)=>(Brush)Application.Current.FindResource(key);
    private static FontFamily Font(string key)=>(FontFamily)Application.Current.FindResource(key);
    public void AddAction(string label,Action action)
    {
        var button=new Button { Content=label,Margin=new Thickness(0,0,10,0) };
        button.Click+=(_,_)=> { try { action(); button.Content="Copied ✓"; } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Timekeeper"); } };
        _buttons.Children.Insert(0,button);
    }
    public static string? Ask(Window owner, string title, string prompt)
    {
        var dialog = new Window { Owner=owner,Style=(Style)Application.Current.FindResource(typeof(Window)),Title=title,Width=560,MaxHeight=SystemParameters.WorkArea.Height,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin=new Thickness(28,24,28,24) };
        stack.Children.Add(new TextBlock { Text="QUICKBASE / LOOKUP",Style=(Style)owner.FindResource("SectionLabel"),Margin=new Thickness(0,0,0,9) });
        stack.Children.Add(new TextBlock { Text=title,FontFamily=Font("DisplayFont"),FontSize=28,Margin=new Thickness(0,0,0,12) });
        stack.Children.Add(new TextBlock { Text=prompt,Foreground=Brush("Muted"),FontSize=13,LineHeight=20,Margin=new Thickness(0,0,0,18) });
        var input=new TextBox(); stack.Children.Add(input);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0) };
        var cancel=new Button { Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,10,0) }; buttons.Children.Add(cancel);
        var button = new Button { Content="Search assignments", Style=(Style)owner.FindResource("Primary"), IsDefault=true };
        button.Click += (_,_) => { dialog.DialogResult=true; dialog.Close(); }; buttons.Children.Add(button); stack.Children.Add(buttons); dialog.Content=new ScrollViewer { Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        dialog.Loaded+=(_,_)=>input.Focus();
        Appearance.Attach(dialog);
        return dialog.ShowDialog()==true ? input.Text : null;
    }
}
