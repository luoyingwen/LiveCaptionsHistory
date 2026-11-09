using System.Windows;

namespace LiveCaptionsHistory;

public partial class App : Application
{
    private Mutex? _instance;
    private Task? _shutdownTask;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\LiveCaptionsHistory-" + Environment.UserName, out var first);
        if (!first)
        {
            MessageBox.Show("字幕历史已经在运行，请从任务栏打开窗口。", "字幕历史");
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
    public void FinishShutdown(Task cleanup) => _shutdownTask ??= FinishShutdownAsync(cleanup);
    private async Task FinishShutdownAsync(Task cleanup)
    {
        try
        {
            // Keep the application dispatcher alive after the visible window
            // closes, but never hold the process indefinitely for native cleanup.
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _ = cleanup.ContinueWith(t => System.Diagnostics.Debug.WriteLine(t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        finally { Shutdown(); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
