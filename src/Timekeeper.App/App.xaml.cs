using System.IO;
using System.Windows;
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
                File.WriteAllText(Path.Combine(output, "ui-smoke.txt"), window.SmokeSummary());
                RenderWindow(window,Path.Combine(output,"timekeeper-demo.png"),1380,1220);
                var settingsWindow=new SettingsWindow(window,new AppSettings(),store,false);
                File.AppendAllText(Path.Combine(output,"ui-smoke.txt"),settingsWindow.SmokeSummary());
                RenderWindow(settingsWindow,Path.Combine(output,"timekeeper-settings.png"),720,850);
                var errorWindow=new SettingsWindow(window,new AppSettings(),store,false);
                errorWindow.ShowSampleError();
                RenderWindow(errorWindow,Path.Combine(output,"timekeeper-settings-error.png"),720,850);
                var updatesWindow=new UpdatesWindow(window,smoke:true);
                File.AppendAllText(Path.Combine(output,"ui-smoke.txt"),updatesWindow.SmokeSummary());
                RenderWindow(updatesWindow,Path.Combine(output,"timekeeper-updates.png"),620,600);
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
    private void RenderWindow(Window window,string path,int width,int height)
    {
        var surface=(FrameworkElement)window.Content;
        window.Content=null;
        if(surface is System.Windows.Controls.Panel panel) panel.Background=window.Background;
        System.Windows.Documents.TextElement.SetForeground(surface,window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(surface,window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(surface,window.FontSize);
        var canvas=new System.Windows.Controls.Border { Background=window.Background, Child=surface, Width=width, Height=height };
        canvas.Measure(new Size(width,height)); canvas.Arrange(new Rect(0,0,width,height)); canvas.UpdateLayout();
        Dispatcher.Invoke(()=>{},DispatcherPriority.Render);
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); image.Render(canvas);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream=File.Create(path); png.Save(stream);
    }
}
