using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveCaptionsHistory;

internal static class FlickerProbe
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    public static async void Start(App app, MainWindow window, bool observeOnly)
    {
        try
        {
            window.Title = "字幕逐字更新测试";
            var reader = (CaptionReader)typeof(MainWindow).GetField("_reader", Flags)!.GetValue(window)!;
            await reader.PauseAsync();
            var captions = (CaptionAccumulator)typeof(MainWindow).GetField("_captions", Flags)!.GetValue(window)!;
            var render = typeof(MainWindow).GetMethod("RenderText", Flags)!;
            var text = (TextBox)window.FindName("TranscriptBox");
            for (int i = 0; i < 30; i++) captions.ObserveRecognition($"第 {i + 1} 段历史字幕保持不变，用于检查逐字追加时整个阅读区域是否跳动。", true);
            render.Invoke(window, null);
            await Task.Delay(150);

            foreach (var mode in new[] { "full", "full-focused", "compact", "compact-correction", "compact-selection", "compact-history", "compact-wrap" })
            {
                if (mode == "compact") MainWindow.ToggleCompactCommand.Execute(null, window);
                else if (mode == "full") ((Button)window.FindName("PauseButton")).Focus();
                else if (mode == "full-focused") text.Focus();
                bool history = mode == "compact-history";
                bool wrapping = mode == "compact-wrap";
                captions.ObserveRecognition("", true);
                captions.ObserveRecognition("新句：", false);
                render.Invoke(window, null);
                await Task.Delay(200);
                if (mode is "compact-selection" or "compact-history")
                {
                    text.Select(5, 8);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                }
                typeof(MainWindow).GetMethod("LatestClick", Flags)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (history)
                {
                    text.ScrollToVerticalOffset(120);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                }
                var initialOffset = text.VerticalOffset;
                var initialHeight = text.ExtentHeight;
                var selection = text.SelectedText;
                int backwards = 0, framesOffBottom = 0, frames = 0;
                double minimum = initialOffset;
                var positions = new List<string>();
                ScrollChangedEventHandler scroll = (_, e) =>
                {
                    if (e.VerticalChange < -1) backwards++;
                    if (Math.Abs(e.VerticalChange) > 1) positions.Add($"{e.VerticalOffset:F1}");
                    minimum = Math.Min(minimum, e.VerticalOffset);
                };
                EventHandler frame = (_, _) =>
                {
                    frames++;
                    if (history ? Math.Abs(text.VerticalOffset - initialOffset) > 1
                        : text.VerticalOffset < text.ExtentHeight - text.ViewportHeight - 2) framesOffBottom++;
                };
                text.AddHandler(ScrollViewer.ScrollChangedEvent, scroll);
                CompositionTarget.Rendering += frame;
                const string phrase = "逐字更新保持稳定不应闪烁";
                for (int i = 1; i <= phrase.Length; i++)
                {
                    var next = mode is "compact-correction" or "compact-selection" or "compact-history"
                        ? "新句：识别修订" + (i % 2 == 0 ? "甲" : "乙")
                        : wrapping ? "新句：" + string.Concat(Enumerable.Repeat("连续追加字幕直到自然换行", i)) : "新句：" + phrase[..i];
                    captions.ObserveRecognition(next, false);
                    render.Invoke(window, null);
                    await Task.Delay(110);
                }
                text.RemoveHandler(ScrollViewer.ScrollChangedEvent, scroll);
                CompositionTarget.Rendering -= frame;
                Console.WriteLine($"{mode}: focused={text.IsKeyboardFocusWithin}, updates={phrase.Length}, backwards={backwards}, offBottomFrames={framesOffBottom}/{frames}, offset={initialOffset:F1}->{text.VerticalOffset:F1}, min={minimum:F1}, extent={initialHeight:F1}->{text.ExtentHeight:F1}, positions=[{string.Join(',', positions)}]");
                if (!observeOnly && (backwards != 0 || (!wrapping && framesOffBottom != 0))) throw new Exception(mode + " jumped during incremental updates");
                if (!observeOnly && selection != text.SelectedText) throw new Exception(mode + " changed the user selection");
                if (wrapping && !observeOnly && (text.ExtentHeight <= initialHeight || Math.Abs(text.VerticalOffset - (text.ExtentHeight - text.ViewportHeight)) > 1))
                    throw new Exception("Wrapped text did not follow to the new bottom");
            }
            window.Close();
        }
        catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(1); }
    }
}
