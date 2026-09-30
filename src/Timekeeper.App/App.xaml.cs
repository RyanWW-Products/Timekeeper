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
            Appearance.Initialize(store.RootPath, smoke);
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
                if (((Button)window.FindName("RecoverDeletedButton")).IsEnabled) throw new InvalidOperationException("Demo history enabled live deletion recovery.");
                var recovery=new DeletionRecoveryWindow(window,receipt.Rows.Where(r=>r.Row.Kind=="work").ToList(),smoke:true);
                File.AppendAllText(report,recovery.SmokeSummary());
                RenderWindow(recovery,Path.Combine(output,"timekeeper-deletion-recovery.png"),(int)recovery.Width,(int)recovery.Height);
                Appearance.Apply(new("Dark", "Theme color", 120, "Compact"));
                RenderWindow(recovery,Path.Combine(output,"theme-dark-deletion-recovery-120.png"),(int)recovery.Width,(int)recovery.Height);
                Appearance.Apply(new()); recovery.Close();

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
                File.AppendAllText(report, AppearanceSmoke(output, window, settingsWindow, errorWindow, updatesWindow, details));
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
    private string AppearanceSmoke(string output, MainWindow owner, SettingsWindow settings, SettingsWindow error, UpdatesWindow updates, TextDialog details)
    {
        string appearanceRoot=Path.Combine(output,"appearance-check",Guid.NewGuid().ToString("N"));
        Appearance.Initialize(appearanceRoot,true);
        int originalRows=((DataGrid)owner.FindName("ReviewGrid")).Items.Count;
        var original=new AppearancePreferences();
        var choice=new AppearancePreferences("Dark","Violet",120,"Compact");
        var cancelled=new AppearanceWindow(owner,smoke:true); cancelled.SmokePreview(choice);
        if(Appearance.Current!=choice) throw new InvalidOperationException("Appearance preview did not apply.");
        cancelled.Close();
        if(Appearance.Current!=original || File.Exists(Path.Combine(appearanceRoot,"appearance.json"))) throw new InvalidOperationException("Cancelled appearance was retained or saved.");
        var saved=new AppearanceWindow(owner,smoke:true); saved.SmokePreview(choice); saved.SmokeSave(); saved.Close();
        Appearance.Initialize(appearanceRoot,false);
        if(Appearance.Current!=choice) throw new InvalidOperationException("Appearance did not survive reloading.");
        if(((DataGrid)owner.FindName("ReviewGrid")).Items.Count!=originalRows || ((Button)owner.FindName("WriteButton")).IsEnabled) throw new InvalidOperationException("Appearance changed the reviewed session or write gate.");
        if(Appearance.Normalize(new("missing","unknown",900,"unknown"))!=original) throw new InvalidOperationException("Unsupported appearance values were not normalized.");
        File.WriteAllText(Path.Combine(appearanceRoot,"appearance.json"),"{invalid");
        if(Appearance.Load(Path.Combine(appearanceRoot,"appearance.json"))!=original) throw new InvalidOperationException("Damaged appearance settings did not fall back safely.");
        foreach(var theme in Appearance.Themes)
        {
            foreach(var accent in Appearance.Accents)
            {
                var palette=Appearance.Palette(new(theme.Name,accent));
                foreach(var pair in new[]{("Ink","Surface"),("Ink","AccentSoft"),("Accent","Surface"),("Accent","Canvas"),("Accent","AccentSoft"),("AccentDeep","AccentSoft"),("Muted","ExportHover"),("ToolTipInk","ToolTipSurface"),("Muted","Canvas"),("Muted","SurfaceMuted"),("Muted","AccentSoft"),("OnAccent","Accent"),("OnAccent","AccentDeep"),("SidebarInk","NavActive"),("SidebarMuted","Sidebar"),("Success","SuccessSoft"),("Warning","WarningSoft"),("Danger","DangerSoft")})
                    if(Contrast(palette[pair.Item1],palette[pair.Item2])<4.5) throw new InvalidOperationException($"Low text contrast in {theme.Name}/{accent}: {pair.Item1}/{pair.Item2}.");
            }
            Appearance.Apply(new(theme.Name));
            var main=new MainWindow(new SessionStore(Path.Combine(output,"theme-smoke-data")),true);
            string name=theme.Name.ToLowerInvariant();
            RenderWindow(main,Path.Combine(output,$"theme-{name}.png"),1380,1220);
            FindDescendant<ScrollViewer>((DependencyObject)settings.Content)?.ScrollToTop();
            RenderWindow(settings,Path.Combine(output,$"theme-{name}-settings.png"),(int)settings.Width,850);
            if(((SolidColorBrush)settings.Background).Color!=Appearance.Palette(new(theme.Name))["Canvas"]) throw new InvalidOperationException("An existing dialog did not update its theme.");
            var picker=new AppearanceWindow(owner,smoke:true);
            RenderWindow(picker,Path.Combine(output,$"theme-{name}-appearance.png"),(int)picker.Width,760);
            var calendar=new Window { Style=(Style)FindResource(typeof(Window)), Content=new Calendar { SelectedDate=new DateTime(2030,3,12),DisplayDate=new DateTime(2030,3,12),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center }, Width=310, Height=320 };
            Appearance.Attach(calendar); RenderWindow(calendar,Path.Combine(output,$"theme-{name}-calendar.png"),310,320);
            picker.Close(); calendar.Close(); main.Close();
        }
        Appearance.Apply(new("Dark"));
        RenderWindow(error,Path.Combine(output,"theme-dark-error.png"),(int)error.Width,850);
        RenderWindow(updates,Path.Combine(output,"theme-dark-updates.png"),(int)updates.Width,750);
        RenderWindow(details,Path.Combine(output,"theme-dark-details.png"),(int)details.Width,(int)details.Height);
        Appearance.Apply(choice);
        var scaled=new MainWindow(new SessionStore(Path.Combine(output,"scaled-smoke-data")),true);
        RenderWindow(scaled,Path.Combine(output,"theme-dark-compact-120.png"),1040,720);
        ((ScrollViewer)scaled.FindName("WorkScroll")).ScrollToEnd();
        RenderWindow(scaled,Path.Combine(output,"theme-dark-compact-120-review.png"),1040,720);
        var scaledGrid=(DataGrid)scaled.FindName("ReviewGrid");
        var tableScroll=FindDescendant<ScrollViewer>(scaledGrid);
        if(scaledGrid.Columns[1].ActualWidth<72 || tableScroll is null || tableScroll.ScrollableWidth<=0)
            throw new InvalidOperationException("The enlarged review grid squeezed the hours column instead of scrolling.");
        var scaledPicker=new AppearanceWindow(owner,smoke:true);
        RenderWindow(scaledPicker,Path.Combine(output,"theme-dark-compact-120-appearance.png"),760,760);
        scaledPicker.Close(); scaled.Close(); Appearance.Apply(original);
        return "PASS: appearance previews revert on cancel, save/reload independently, tolerate damaged preferences and preserve the review/write gate\nPASS: all five themes and accent overrides meet 4.5:1 text contrast checks\nPASS: rendered all themes, existing dialogs, calendars and 120% compact/minimum layouts\n";
    }
    private static double Contrast(Color a,Color b)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte b) { double s=b/255d; return s<=0.04045?s/12.92:Math.Pow((s+0.055)/1.055,2.4); }
            return 0.2126*Linear(c.R)+0.7152*Linear(c.G)+0.0722*Linear(c.B);
        }
        double x=Luminance(a),y=Luminance(b); return (Math.Max(x,y)+0.05)/(Math.Min(x,y)+0.05);
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
        date.SelectedDate=null; date.UpdateLayout();
        var watermark=editor.Template.FindName("PART_Watermark",editor) as ContentControl;
        if(watermark is null || watermark.Visibility!=Visibility.Visible || string.IsNullOrWhiteSpace(watermark.Content?.ToString()))
            throw new InvalidOperationException("The empty date field lost its format hint.");
        calendar.DisplayDate=new DateTime(2030,3,12);
        calendar.Measure(new Size(310,320)); calendar.Arrange(new Rect(0,0,310,320)); calendar.UpdateLayout();
        var calendarItem=(CalendarItem)calendar.Template.FindName("PART_CalendarItem",calendar);
        var next=(Button)calendarItem.Template.FindName("PART_NextButton",calendarItem);
        var previous=(Button)calendarItem.Template.FindName("PART_PreviousButton",calendarItem);
        var heading=(Button)calendarItem.Template.FindName("PART_HeaderButton",calendarItem);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,next));
        if(calendar.DisplayDate.Month!=4) throw new InvalidOperationException("The themed calendar did not advance a month.");
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,previous));
        if(calendar.DisplayDate.Month!=3) throw new InvalidOperationException("The themed calendar did not return a month.");
        heading.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,heading)); calendar.UpdateLayout();
        if(calendar.DisplayMode!=CalendarMode.Year || ((Grid)calendarItem.Template.FindName("PART_YearView",calendarItem)).Visibility!=Visibility.Visible)
            throw new InvalidOperationException("The themed calendar did not show its month selector.");
        heading.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,heading)); calendar.UpdateLayout();
        if(calendar.DisplayMode!=CalendarMode.Decade) throw new InvalidOperationException("The themed calendar did not show its year selector.");

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
        return "PASS: styled date fields select text, commit dates, show empty format hints and synchronize selection; calendars navigate months/years without opening a popup\nPASS: horizontal and vertical scrollbars respond to thumb dragging and forward/backward page commands\n";

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
