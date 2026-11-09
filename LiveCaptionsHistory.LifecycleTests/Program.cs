using System.Diagnostics;
using System.Text;
using LiveCaptionsHistory;

// This test executable also acts as a child with the real stdin/stdout protocol.
// It can remain silent or ignore stop, exercising actual OS pipe/kill behavior.
if (args.Contains("--parent"))
{
    Console.OutputEncoding = new UTF8Encoding(false);
    var folder = Environment.GetEnvironmentVariable("CAPTION_LIFECYCLE_TEST")!;
    File.WriteAllText(Path.Combine(folder, "pid"), Environment.ProcessId.ToString());
    var mode = File.ReadAllText(Path.Combine(folder, "mode"));
    if (mode != "startup") Console.WriteLine("{\"kind\":\"ready\",\"languages\":[\"en-US\"],\"language\":\"en-US\"}");
    await Console.In.ReadLineAsync();
    File.WriteAllText(Path.Combine(folder, "stop-received"), "yes");
    if (mode == "hang") await Task.Delay(Timeout.Infinite);
    return;
}

bool Alive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
foreach (var mode in new[] { "clean", "hang", "startup" })
{
    var folder = Path.Combine(Path.GetTempPath(), "CaptionLifecycle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "mode"), mode);
    Environment.SetEnvironmentVariable("CAPTION_LIFECYCLE_TEST", folder);
    var reader = new CaptionReader(Environment.ProcessPath!);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    reader.Reading += r => { if (r.Languages is not null) ready.TrySetResult(); };
    int pid = 0;
    try
    {
        reader.Start();
        if (mode != "startup") await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
        else
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(Path.Combine(folder, "pid"))) await Task.Delay(10, startup.Token);
        }
        pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "pid")));
        var timer = Stopwatch.StartNew();
        var stopping = reader.DisposeAsync().AsTask();
        var requestTime = timer.ElapsedMilliseconds;
        Assert(requestTime < 200, "DisposeAsync blocked its caller");
        Assert(ReferenceEquals(stopping, reader.DisposeAsync().AsTask()), "Repeated disposal must share completion");
        await stopping.WaitAsync(TimeSpan.FromSeconds(4));
        Assert(!Alive(pid), "Child remains after disposal");
        Assert(File.Exists(Path.Combine(folder, "stop-received")), "Stop was not sent while stdout was silent");
        Console.WriteLine($"PASS {mode}: request={requestTime} ms, total={timer.ElapsedMilliseconds} ms, child exited");
    }
    finally
    {
        if (pid != 0 && Alive(pid)) { using var child = Process.GetProcessById(pid); child.Kill(); }
        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
}
Environment.SetEnvironmentVariable("CAPTION_LIFECYCLE_TEST", null);
Console.WriteLine("Lifecycle scenarios passed.");
