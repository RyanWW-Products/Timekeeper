using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Timekeeper.Core;

namespace Timekeeper.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Timekeeper could not finish that action. " + args.Exception.Message, "Timekeeper", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        bool smoke = e.Args.Length >= 2 && e.Args[0] == "--smoke-test";
        try
        {
            var store = new SessionStore(smoke ? Path.Combine(Path.GetFullPath(e.Args[1]), "smoke-data") : null);
            var window = new MainWindow(store, smoke);
            MainWindow = window;
            if (smoke)
            {
                // Render our own WPF surface without interacting with the user's desktop.
                string output = Path.GetFullPath(e.Args[1]);
                Directory.CreateDirectory(output);
                File.Delete(Path.Combine(output,"ui-smoke-error.txt"));
                string report=Path.Combine(output,"ui-smoke.txt");
                File.WriteAllText(report, window.SmokeSummary());
                File.AppendAllText(report,ControlSmokeSummary());
                RenderWindow(window,Path.Combine(output,"timekeeper-demo.png"),1380,1220);
                RenderWindow(window,Path.Combine(output,"timekeeper-default.png"),(int)window.Width,(int)window.Height);
                RenderWindow(window,Path.Combine(output,"timekeeper-minimum.png"),(int)window.MinWidth,(int)window.MinHeight);
                var workScroll=(ScrollViewer)window.FindName("WorkScroll");
                workScroll.ScrollToEnd();
                RenderWindow(window,Path.Combine(output,"timekeeper-minimum-review.png"),(int)window.MinWidth,(int)window.MinHeight);
                workScroll.ScrollToTop();

                var emptyWindow=new MainWindow(new SessionStore(Path.Combine(output,"empty-smoke-data")),true);
                emptyWindow.ShowSmokeEmptyState();
                RenderWindow(emptyWindow,Path.Combine(output,"timekeeper-initial.png"),(int)emptyWindow.Width,(int)emptyWindow.Height);
                emptyWindow.ShowSmokeHistory();
                RenderWindow(emptyWindow,Path.Combine(output,"timekeeper-history-empty.png"),(int)emptyWindow.Width,(int)emptyWindow.Height);

                var sample=DemoData.CreateSession();
                var proposal=DemoData.CreateProposal(sample);
                var rows=Rules.Validate(sample,proposal).Rows;
                var receipt=new SubmissionReceipt
                {
                    SubmissionId="visual-smoke-submission", SessionId=sample.SessionId,
                    ProfileKey=new AppSettings().ProfileKey,
                    StartedAtUtc=DateTimeOffset.UtcNow.AddMinutes(-2), FinishedAtUtc=DateTimeOffset.UtcNow,
                    Status="complete", Message=$"All {rows.Count} timecards were submitted successfully.",
                    Rows=rows.Select((row,index)=>new RowOutcome { Row=row, Status="created", RecordId=24001+index, Message="Confirmed in Quickbase. Synthetic sample only." }).ToList()
                };
                store.SaveReceipt(receipt);
                window.ShowSmokeHistory();
                ((DataGrid)window.FindName("HistoryGrid")).SelectedIndex=0;
                RenderWindow(window,Path.Combine(output,"timekeeper-history.png"),(int)window.Width,(int)window.Height);

                var settingsWindow=new SettingsWindow(window,new AppSettings(),store,false);
                File.AppendAllText(report,settingsWindow.SmokeSummary());
                RenderWindow(settingsWindow,Path.Combine(output,"timekeeper-settings.png"),(int)settingsWindow.Width,850);
                FindDescendant<ScrollViewer>((DependencyObject)settingsWindow.Content)?.ScrollToEnd();
                RenderWindow(settingsWindow,Path.Combine(output,"timekeeper-settings-preferences.png"),(int)settingsWindow.Width,850);
                var errorWindow=new SettingsWindow(window,new AppSettings(),store,false);
                errorWindow.ShowSampleError();
                RenderWindow(errorWindow,Path.Combine(output,"timekeeper-settings-error.png"),(int)errorWindow.Width,850);
                var updatesWindow=new UpdatesWindow(window,smoke:true);
                RenderWindow(updatesWindow,Path.Combine(output,"timekeeper-updates.png"),(int)updatesWindow.Width,750);
                File.AppendAllText(report,updatesWindow.SmokeSummary());
                RenderWindow(updatesWindow,Path.Combine(output,"timekeeper-updates-error.png"),(int)updatesWindow.Width,750);

                var details=new TextDialog(window,"Timecard details","Date: "+sample.Days[0].Date.ToString("yyyy-MM-dd")+"\nHours: 4.00\nType: work\n\nProject: Example Client / Spring launch (ID 100)\nAssignment: Presentation design (ID 501)\nTask: Design (ID 60)\nCategory: Client work (ID 20)\n\nDescription:\nPrepare presentation visuals and refine the opening sequence for the client review.\n\nToggl source IDs: 1001",smoke:true);
                RenderWindow(details,Path.Combine(output,"timekeeper-details.png"),(int)details.Width,(int)details.Height);
                var paste=new TextDialog(window,"Paste Copilot's proposal",JsonSerializer.Serialize(proposal,JsonDefaults.Options),editable:true,smoke:true);
                RenderWindow(paste,Path.Combine(output,"timekeeper-proposal.png"),(int)paste.Width,(int)paste.Height);
                File.AppendAllText(report,"PASS: rendered initial, default, minimum, review, history, settings, errors, updates, details and proposal views with synthetic data only\n");
                Shutdown(0);
            }
            else window.Show();
        }
        catch (Exception ex)
        {
            if (smoke)
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "ui-smoke-error.txt"), ex.ToString());
            }
            else MessageBox.Show("Timekeeper could not open its local data. " + ex.Message, "Timekeeper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private string ControlSmokeSummary()
    {
        // Exercise the real shared templates entirely offscreen. Never open a Popup or send native input.
        var date=new DatePicker
        {
            Style=(Style)FindResource(typeof(DatePicker)), Language=XmlLanguage.GetLanguage("en-US"),
            SelectedDateFormat=DatePickerFormat.Short, SelectedDate=new DateTime(2030,3,12)
        };
        date.Measure(new Size(180,40)); date.Arrange(new Rect(0,0,180,40)); date.UpdateLayout();
        var editor=date.Template.FindName("PART_TextBox",date) as DatePickerTextBox
            ?? throw new InvalidOperationException("The date picker has no editable date field.");
        var popup=date.Template.FindName("PART_Popup",date) as Popup;
        var calendar=popup?.Child as Calendar
            ?? throw new InvalidOperationException("The date picker did not connect its calendar.");
        editor.SelectAll();
        if(editor.SelectedText!=editor.Text || editor.SelectionLength==0)
            throw new InvalidOperationException("The date field does not support text selection.");
        editor.Text="3/14/2030";
        editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent,editor));
        if(date.SelectedDate!=new DateTime(2030,3,14))
            throw new InvalidOperationException("The date picker did not commit a typed date.");
        calendar.SelectedDate=new DateTime(2030,3,18);
        if(date.SelectedDate!=calendar.SelectedDate || !editor.Text.Contains("18"))
            throw new InvalidOperationException("Calendar selection did not update the date field.");
        if(date.IsDropDownOpen || popup!.IsOpen)
            throw new InvalidOperationException("The offscreen date smoke check opened a popup.");

        foreach(var orientation in new[]{Orientation.Horizontal,Orientation.Vertical})
        {
            bool horizontal=orientation==Orientation.Horizontal;
            var bar=new ScrollBar
            {
                Style=(Style)FindResource(typeof(ScrollBar)), Orientation=orientation,
                Minimum=0, Maximum=100, Value=20, ViewportSize=20, LargeChange=10,
                Width=horizontal?200:10, Height=horizontal?10:200
            };
            var bounds=new Size(bar.Width,bar.Height);
            bar.Measure(bounds); bar.Arrange(new Rect(new Point(),bounds)); bar.UpdateLayout();
            var track=bar.Template.FindName("PART_Track",bar) as Track
                ?? throw new InvalidOperationException($"The {orientation} scrollbar has no track.");
            double before=bar.Value;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0,0));
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(horizontal?18:0,horizontal?0:18));
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(horizontal?18:0,horizontal?0:18,false));
            if(bar.Value<=before)
                throw new InvalidOperationException($"Dragging the {orientation} scrollbar did not advance its value.");
            before=bar.Value;
            ExecutePage(track.IncreaseRepeatButton);
            if(bar.Value<=before)
                throw new InvalidOperationException($"The {orientation} scrollbar did not page forward.");
            before=bar.Value;
            ExecutePage(track.DecreaseRepeatButton);
            if(bar.Value>=before)
                throw new InvalidOperationException($"The {orientation} scrollbar did not page backward.");
        }
        return "PASS: styled date fields select text, commit typed dates and synchronize calendar selection without opening a popup\nPASS: horizontal and vertical scrollbars respond to thumb dragging and forward/backward page commands\n";

        static void ExecutePage(RepeatButton button)
        {
            if(button.Command is not RoutedCommand command || !command.CanExecute(null,button))
                throw new InvalidOperationException("The scrollbar page action is unavailable.");
            command.Execute(null,button);
        }
    }
    private void RenderWindow(Window window,string path,int width,int height)
    {
        var surface=(FrameworkElement)window.Content;
        window.Content=null;
        if(surface is Panel panel && panel.Background is null) panel.Background=window.Background;
        System.Windows.Documents.TextElement.SetForeground(surface,window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(surface,window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(surface,window.FontSize);
        var canvas=new Border { Background=window.Background, Child=surface, Width=width, Height=height };
        try
        {
            canvas.Measure(new Size(width,height)); canvas.Arrange(new Rect(0,0,width,height)); canvas.UpdateLayout();
            Dispatcher.Invoke(()=>{},DispatcherPriority.Render);
            canvas.UpdateLayout();
            var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); image.Render(canvas);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream=File.Create(path); png.Save(stream);
        }
        finally
        {
            canvas.Child=null;
            window.Content=surface;
        }
    }
    private static T? FindDescendant<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
        {
            var child=VisualTreeHelper.GetChild(parent,index);
            if(child is T match) return match;
            if(FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
