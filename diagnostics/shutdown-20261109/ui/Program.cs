using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionsHistory;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        var mode = args.FirstOrDefault() ?? "empty";
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (mode.StartsWith("flicker")) FlickerProbe.Start(app, (MainWindow)app.MainWindow, mode == "flicker-observe");
            else if (mode == "compact") CompactProbe.Start(app, (MainWindow)app.MainWindow);
            else new ShutdownProbe(mode).Start(app, (MainWindow)app.MainWindow);
        }));
        return app.Run();
    }
}

// Exercise the production WPF Closing/Closed/save code in an owned test window;
// this does not send input to or inspect any other application's windows.
internal sealed class ShutdownProbe(string mode)
{
    public void Start(App app, MainWindow window)
    {
        window.Title = "关闭流程测试";
        var watch = new Stopwatch();
        var start = Stopwatch.StartNew();
        string? session = null;
        FileStream? lockedFile = null;
        window.Closed += (_, _) =>
        {
            Console.WriteLine($"WINDOW_CLOSED mode={mode} elapsed={watch.ElapsedMilliseconds} ms");
            if (mode != "empty")
            {
                if (session is null || !File.ReadAllText(session).Contains("关闭流程测试文本")) throw new Exception("Final text was not saved");
                Console.WriteLine("PASS final snapshot saved");
            }
        };
        app.Exit += (_, _) => Console.WriteLine($"APP_EXIT elapsed_since_close={watch.ElapsedMilliseconds} ms total={start.ElapsedMilliseconds} ms");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(mode == "settled" ? 5000 : 300) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            session = (string)typeof(MainWindow).GetField("_sessionPath", flags)!.GetValue(window)!;
            if (mode != "empty")
            {
                var captions = (CaptionAccumulator)typeof(MainWindow).GetField("_captions", flags)!.GetValue(window)!;
                captions.ObserveRecognition("关闭流程测试文本", false);
                typeof(MainWindow).GetMethod("RenderText", flags)!.Invoke(window, null);
            }
            if (mode == "save-failure")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(session)!);
                lockedFile = new FileStream(session + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            }
            watch.Start();
            window.Close();
            Console.WriteLine($"CLOSE_CALL_RETURNED {watch.ElapsedMilliseconds} ms");
            if (mode == "save-failure")
            {
                await Task.Delay(250);
                if (!window.IsVisible || !window.IsEnabled) throw new Exception("Save failure did not preserve usable window");
                Console.WriteLine("PASS save failure retained window");
                lockedFile!.Dispose();
                watch.Restart();
                window.Close();
            }
        };
        timer.Start();
    }
}
