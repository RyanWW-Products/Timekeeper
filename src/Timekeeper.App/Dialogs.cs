using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Timekeeper.App;

internal sealed class TextDialog : Window
{
    private readonly TextBox _text;
    private readonly StackPanel _buttons;
    public string Value => _text.Text;
    public TextDialog(Window owner, string title, string text, bool editable = false)
    {
        Owner=owner; Style=(Style)Application.Current.FindResource(typeof(Window)); Title=title; Width=820; Height=620; MinWidth=520; MinHeight=360; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin=new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        grid.Children.Add(new TextBlock { Text=title, FontSize=24, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,16) });
        _text = new TextBox { Text=text, IsReadOnly=!editable, AcceptsReturn=true, AcceptsTab=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, FontFamily=new FontFamily("Consolas"), FontSize=13 };
        Grid.SetRow(_text,1); grid.Children.Add(_text);
        var buttons = _buttons = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,16,0,0) };
        var copy = new Button { Content="Copy text", Margin=new Thickness(0,0,10,0) }; copy.Click += (_,_) => Clipboard.SetText(_text.Text); buttons.Children.Add(copy);
        var done = new Button { Content=editable ? "Validate proposal" : "Close", IsDefault=true, Style=(Style)FindResource("Primary") }; done.Click += (_,_) => { DialogResult=editable; Close(); }; buttons.Children.Add(done);
        Grid.SetRow(buttons,2); grid.Children.Add(buttons); Content=grid;
    }
    public void AddAction(string label,Action action)
    {
        var button=new Button { Content=label,Margin=new Thickness(0,0,10,0) };
        button.Click+=(_,_)=> { try { action(); button.Content="Copied ✓"; } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Timekeeper"); } };
        _buttons.Children.Insert(0,button);
    }
    public static string? Ask(Window owner, string title, string prompt)
    {
        var dialog = new Window { Owner=owner,Style=(Style)Application.Current.FindResource(typeof(Window)),Title=title,Width=540,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin=new Thickness(24) };
        stack.Children.Add(new TextBlock { Text=prompt, Margin=new Thickness(0,0,0,14) });
        var input=new TextBox(); stack.Children.Add(input);
        var button = new Button { Content="Search", Style=(Style)owner.FindResource("Primary"), Margin=new Thickness(0,16,0,0), HorizontalAlignment=HorizontalAlignment.Right, IsDefault=true };
        button.Click += (_,_) => { dialog.DialogResult=true; dialog.Close(); }; stack.Children.Add(button); dialog.Content=stack;
        return dialog.ShowDialog()==true ? input.Text : null;
    }
}
