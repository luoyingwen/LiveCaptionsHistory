using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace LiveCaptionsHistory;

public sealed record CaptionReading(string State, string? Text = null, bool SourceClosed = false,
    bool IsFinal = false, string[]? Languages = null, string? Language = null, long Generation = 0);

// Each recording interval owns a fresh backend process, discarding old queued
// callbacks and audio whenever recording is paused, reconnected or switched.
public sealed class CaptionReader : IAsyncDisposable
{
    private sealed record Settings(long Generation, bool Paused, string? Language)
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _gate = new();
    private readonly Channel<Settings> _requests = Channel.CreateUnbounded<Settings>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private readonly string _hostPath;
    private Task? _disposeTask;
    private long _generation;
    private bool _paused, _started, _disposed;
    private string? _language;
    public event Action<CaptionReading>? Reading;

    public CaptionReader() : this(Path.Combine(AppContext.BaseDirectory, "engine", "SpeechBackendHost.exe")) { }
    internal CaptionReader(string hostPath) { _hostPath = hostPath; _worker = Task.Run(CoordinateAsync); }
    public void Start() { lock (_gate) { if (_started || _disposed) return; _started = true; Request(); } }
    public void Pause() { lock (_gate) { if (_disposed) return; _paused = true; Request(); } }
    public Task PauseAsync()
    {
        lock (_gate) { if (_disposed) return _disposeTask ?? Task.CompletedTask; _paused = true; return Request(); }
    }
    public void Resume() { lock (_gate) { if (_disposed) return; _paused = false; Request(); } }
    public void Restart() { lock (_gate) { if (!_disposed) Request(); } }
    public void SetLanguage(string language)
    {
        lock (_gate) { if (_disposed || string.Equals(_language, language, StringComparison.OrdinalIgnoreCase)) return; _language = language; Request(); }
    }
    public bool IsCurrent(CaptionReading reading) { lock (_gate) return !_disposed && reading.Generation == _generation; }
    private Task Request()
    {
        var settings = new Settings(++_generation, _paused, _language);
        Publish(settings, new(_paused ? "已暂停，识别引擎正在停止…" : "正在连接本机识别引擎…", SourceClosed: true));
        _requests.Writer.TryWrite(settings);
        return settings.Completed.Task;
    }
    private void Publish(Settings settings, CaptionReading reading)
    {
        lock (_gate)
        {
            if (_disposed || settings.Generation != _generation) return;
            Reading?.Invoke(reading with { Generation = settings.Generation });
        }
    }
    private async Task CoordinateAsync()
    {
        CancellationTokenSource? activeCancellation = null;
        Task active = Task.CompletedTask;
        try
        {
            await foreach (var first in _requests.Reader.ReadAllAsync())
            {
                activeCancellation?.Cancel();
                await active;
                activeCancellation?.Dispose();
                activeCancellation = null;
                var settings = first;
                while (_requests.Reader.TryRead(out var newer)) { settings.Completed.TrySetResult(); settings = newer; }
                lock (_gate) { if (_disposed) { settings.Completed.TrySetResult(); break; } }
                if (settings.Paused)
                {
                    Publish(settings, new("已暂停，音频采集已停止", SourceClosed: true));
                    active = Task.CompletedTask;
                    settings.Completed.TrySetResult();
                    continue;
                }
                activeCancellation = new CancellationTokenSource();
                active = RunSessionAsync(settings, activeCancellation.Token);
                settings.Completed.TrySetResult();
            }
        }
        finally
        {
            activeCancellation?.Cancel();
            await active;
            activeCancellation?.Dispose();
        }
    }
    private async Task RunSessionAsync(Settings settings, CancellationToken cancellation)
    {
        using var process = new Process();
        bool launched = false, reportedError = false;
        Task<string>? stderr = null;
        Task? stopping = null;
        var stoppingGate = new object();
        Task EnsureStopped()
        {
            lock (stoppingGate) return stopping ??= Task.Run(() => StopOwnedProcessAsync(process));
        }
        CancellationTokenRegistration stopRegistration = default;
        try
        {
            var host = _hostPath;
            if (!File.Exists(host)) throw new FileNotFoundException("找不到本机识别引擎，请重新构建或完整解压应用。");
            process.StartInfo = new ProcessStartInfo(host)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(host)!
            };
            process.StartInfo.ArgumentList.Add("--parent");
            process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            if (settings.Language is not null) { process.StartInfo.ArgumentList.Add("--language"); process.StartInfo.ArgumentList.Add(settings.Language); }
            cancellation.ThrowIfCancellationRequested();
            process.Start();
            launched = true;
            // Sending stop is independent of stdout cancellation. In particular,
            // a silent or unresponsive reader cannot delay the stop request.
            stopRegistration = cancellation.Register(() => { _ = EnsureStopped(); });
            stderr = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                switch (root.GetProperty("kind").GetString())
                {
                    case "ready":
                        var language = root.GetProperty("language").GetString();
                        lock (_gate) { if (settings.Generation == _generation) _language = language; }
                        Publish(settings, new("已连接本机引擎，等待系统声音…", Languages: root.GetProperty("languages").EnumerateArray().Select(l => l.GetString()!).ToArray(), Language: language));
                        break;
                    case "caption":
                        Publish(settings, new("已连接本机引擎 · 正在转写", root.GetProperty("text").GetString(), IsFinal: root.GetProperty("final").GetBoolean()));
                        break;
                    case "error":
                        reportedError = true;
                        Publish(settings, new(root.GetProperty("message").GetString() ?? "识别引擎发生错误", SourceClosed: true));
                        return;
                }
            }
            if (!cancellation.IsCancellationRequested && !reportedError)
                Publish(settings, new("识别引擎已退出，已有记录已保留；点击重新连接可重试。", SourceClosed: true));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or UnauthorizedAccessException)
        {
            Publish(settings, new("无法连接识别引擎：" + e.Message, SourceClosed: true));
        }
        finally
        {
            stopRegistration.Dispose();
            if (launched) await EnsureStopped();
            if (stderr is not null)
            {
                ObserveFault(stderr);
                try { await stderr.WaitAsync(TimeSpan.FromMilliseconds(200)); }
                catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) { }
            }
        }
    }
    private static async Task StopOwnedProcessAsync(Process process)
    {
        // One graceful budget covers both the command and process termination.
        using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (process.HasExited) return;
            var send = Task.Run(async () =>
            {
                await process.StandardInput.WriteLineAsync("{\"command\":\"stop\"}");
                await process.StandardInput.FlushAsync();
            });
            ObserveFault(send);
            try { await send.WaitAsync(graceful.Token); } catch (IOException) { }
            await process.WaitForExitAsync(graceful.Token);
        }
        catch (OperationCanceledException)
        {
            // This host launches no child processes. Avoid enumerating the
            // machine's process tree and bound the kill/confirmation as well.
            var force = Task.Run(async () =>
            {
                if (!process.HasExited) process.Kill();
                using var confirmation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                await process.WaitForExitAsync(confirmation.Token);
            });
            ObserveFault(force);
            try { await force.WaitAsync(TimeSpan.FromMilliseconds(500)); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { Debug.WriteLine(e.Message); }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Debug.WriteLine(e.Message); }
    }
    private static void ObserveFault(Task task) => _ = task.ContinueWith(t => Debug.WriteLine(t.Exception),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _generation++;
            _requests.Writer.TryWrite(new(_generation, true, _language));
            _requests.Writer.TryComplete();
            _disposeTask = _worker;
            return new(_disposeTask);
        }
    }
}
