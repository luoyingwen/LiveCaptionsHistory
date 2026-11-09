using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using LiveCaptionsHistory;

internal static class CompactProbe
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new Exception(description);
        Console.WriteLine("PASS " + description);
    }
    private static void Snapshot(MainWindow window, string name)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.GetFullPath($"diagnostics/shutdown-20261109/{name}.png"));
        encoder.Save(file);
    }
    public static async void Start(App app, MainWindow window)
    {
        try
        {
            window.Title = "精炼模式测试";
            var reader = Field<CaptionReader>(window, "_reader");
            await reader.PauseAsync();
            var captions = Field<CaptionAccumulator>(window, "_captions");
            for (int i = 1; i <= 24; i++) captions.ObserveRecognition($"第 {i} 段：精炼模式只显示字幕内容。向上滚动可以回看，新的字幕仍会继续记录。", true);
            typeof(MainWindow).GetMethod("RenderText", Private)!.Invoke(window, null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var text = (TextBox)window.FindName("TranscriptBox");
            var originalText = text.Text;
            var width = window.Width;
            var height = window.Height;
            text.Select(5, 8);
            var selected = text.SelectedText;
            Snapshot(window, "full-mode-preview");

            MainWindow.ToggleCompactCommand.Execute(null, window);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(window.WindowStyle == WindowStyle.None, "compact mode removes title bar");
            Check(new[] { "HeaderPanel", "ToolbarPanel", "FooterPanel", "TranscriptHeading", "FollowPanel" }
                .All(name => !((UIElement)window.FindName(name)).IsVisible), "only transcript remains visible");
            Check(text.IsVisible && text.Text == originalText && text.SelectedText == selected, "history and selection survive mode change");
            Check(((UIElement)window.FindName("CompactDragArea")).IsVisible && WindowChrome.GetWindowChrome(window).ResizeBorderThickness.Left == 6,
                "drag area and resize border available");
            Snapshot(window, "compact-mode-preview");

            var left = window.Left;
            var top = window.Top;
            MainWindow.MoveCompactCommand.Execute("right", window);
            MainWindow.MoveCompactCommand.Execute("down", window);
            Check(Math.Abs(window.Left - left - 20) < 1 && Math.Abs(window.Top - top - 20) < 1, "window move commands work");
            var movedLeft = window.Left;
            var movedTop = window.Top;
            typeof(MainWindow).GetField("_following", Private)!.SetValue(window, false);
            text.ScrollToVerticalOffset(120);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var offset = text.VerticalOffset;
            Console.WriteLine($"SCROLL before={offset} following={Field<bool>(window, "_following")} extent={text.ExtentHeight} viewport={text.ViewportHeight}");
            captions.ObserveRecognition("回看期间新增的字幕也会保存。", true);
            typeof(MainWindow).GetMethod("RenderText", Private)!.Invoke(window, null);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Console.WriteLine($"SCROLL after={text.VerticalOffset} following={Field<bool>(window, "_following")}");
            Check(offset > 0 && Math.Abs(text.VerticalOffset - offset) < 1, "new captions preserve scrolled position in compact mode");

            window.Width = 280;
            window.Height = 100;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(text.ActualWidth > 200 && text.ActualHeight > 70, "compact minimum size retains usable transcript");
            Snapshot(window, "compact-small-preview");
            MainWindow.ExitCompactCommand.Execute(null, window);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(window.WindowStyle == WindowStyle.SingleBorderWindow && window.Width == width && window.Height == height,
                "exit restores full window size and title bar");
            Check(((UIElement)window.FindName("ToolbarPanel")).IsVisible, "full controls restored");
            MainWindow.ToggleCompactCommand.Execute(null, window);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Math.Abs(window.Left - movedLeft) < 1 && Math.Abs(window.Top - movedTop) < 1 && window.Width == 280,
                "compact position and size remembered for this session");
            text.ContextMenu!.IsOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(((MenuItem)window.FindName("MenuCompactMode")).Header.ToString() == "返回完整界面", "context menu provides exit");
            text.ContextMenu.IsOpen = false;

            var session = Field<string>(window, "_sessionPath");
            var previousSave = Field<Task>(window, "_saveTask");
            await previousSave;
            captions.ObserveRecognition("保存失败时必须恢复完整界面。", true);
            typeof(MainWindow).GetMethod("RenderText", Private)!.Invoke(window, null);
            Directory.CreateDirectory(Path.GetDirectoryName(session)!);
            using (var lockedFile = new FileStream(session + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                window.Close();
                await Task.Delay(350);
                Check(window.IsVisible && window.IsEnabled && window.WindowStyle == WindowStyle.SingleBorderWindow
                    && ((UIElement)window.FindName("FooterPanel")).IsVisible, "save failure restores full controls and error message");
            }
            window.Close();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            app.Shutdown(1);
        }
    }
}
