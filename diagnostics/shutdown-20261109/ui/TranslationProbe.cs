using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveCaptionsHistory;

internal static class TranslationProbe
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    public static async void Start(App app, MainWindow window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var export = Path.Combine(Path.GetTempPath(), $"caption-export-{Guid.NewGuid():N}.txt");
        var session = Field<string>(window, "_sessionPath");
        try
        {
            // This probe runs in a dedicated process. Never use the user's real provider.
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Environment.SetEnvironmentVariable("CTRLLOOKUP_LLM_ENDPOINT", $"http://127.0.0.1:{port}/v1/chat/completions");
            Environment.SetEnvironmentVariable("CTRLLOOKUP_LLM_MODEL", "probe");
            Environment.SetEnvironmentVariable("CTRLLOOKUP_LLM_API_KEY", "");

            var reader = Field<CaptionReader>(window, "_reader");
            await reader.PauseAsync();
            await IdleAsync();
            var generation = Field<long>(reader, "_generation");
            void Send(CaptionReading reading) => Invoke(window, "Receive", reading with { Generation = generation });
            void Caption(string text, bool final) => Send(new("已连接 · 测试", text, IsFinal: final));

            var check = Field<CheckBox>(window, "TranslationCheck");
            var menu = Field<MenuItem>(window, "MenuTranslation");
            var transcript = Field<TextBox>(window, "TranscriptBox");
            var earlyCheck = Field<CheckBox>(window, "EarlyTranslationCheck");
            var earlyMenu = Field<MenuItem>(window, "MenuEarlyTranslation");
            var translationStatus = Field<TextBlock>(window, "TranslationStatus");
            var menuStatus = Field<MenuItem>(window, "MenuTranslationStatus");
            Check(check.IsChecked == false && !menu.IsChecked && earlyCheck.IsChecked == false && !earlyMenu.IsEnabled,
                "translation and early translation are off by default");
            Send(new("已连接 · 测试", Languages: ["en-US", "zh-CN"], Language: "en-US"));
            for (var i = 1; i <= 35; i++) Caption($"History line number {i} before enabling translation.", true);
            check.IsChecked = true;
            Check(menu.IsChecked, "full toolbar enables the compact menu option");
            Caption("This English sentence will be replaced.", false);
            Check(transcript.Text.EndsWith("This English sentence will be replaced.") && !listener.Pending()
                && translationStatus.Text.Contains("定稿后翻译"),
                "unfinished English is visible without contacting the provider");
            Caption("This English sentence will be replaced.", true);
            Check(translationStatus.Text.Contains("1 条等待译文") && (string)menuStatus.Header == translationStatus.Text,
                "full and compact status show that one finalized caption is waiting");
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = socket.GetStream();
            using (var request = JsonDocument.Parse(await ReadRequestAsync(stream, timeout.Token)))
                Check(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()
                    == "This English sentence will be replaced.", "only the final caption is sent to the local provider");
            Caption("The newer selection stays intact.", false);
            MainWindow.ToggleCompactCommand.Execute(null, window);
            await IdleAsync();
            transcript.Select(transcript.Text.IndexOf("newer selection", StringComparison.Ordinal), "newer selection".Length);
            typeof(MainWindow).GetField("_following", Private)!.SetValue(window, false);
            transcript.ScrollToVerticalOffset(120);
            await IdleAsync();
            var before = transcript.VerticalOffset;
            Check(before > 0, "probe scrolled away from latest captions");

            const string translation = "这条英文字幕已替换。";
            await ReplyAsync(stream, translation, timeout.Token);
            while (!transcript.Text.Contains(translation)) await Task.Delay(10, timeout.Token);
            await IdleAsync();
            Check(transcript.Text.Contains("History line number 35 before enabling translation.")
                && transcript.Text.EndsWith(translation + "\nThe newer selection stays intact."),
                "only the finalized English line is replaced in compact mode");
            Check(Math.Abs(transcript.VerticalOffset - before) < 1 && !Field<bool>(window, "_following")
                && transcript.SelectedText == "newer selection", "translation keeps the viewport and later selection while reviewing history");

            var saved = (Task)Invoke(window, "SaveAsync")!;
            await saved;
            // First call can return a periodic write already in progress.
            await (Task)Invoke(window, "SaveAsync")!;
            var original = await File.ReadAllTextAsync(session, timeout.Token);
            Check(original.Contains("This English sentence will be replaced.") && !original.Contains(translation),
                "autosave keeps original English");
            await (Task)Invoke(window, "ExportTranscriptAsync", export)!;
            Check(await File.ReadAllTextAsync(export, timeout.Token) == transcript.Text,
                "manual export uses the displayed Chinese transcript");

            Caption("The failed caption remains in English.", true);
            using (var failed = await listener.AcceptTcpClientAsync(timeout.Token))
            using (var failedStream = failed.GetStream())
            {
                await ReadRequestAsync(failedStream, timeout.Token);
                await failedStream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
                while (!translationStatus.Text.Contains("1 条失败")) await Task.Delay(10, timeout.Token);
                Check((string)menuStatus.Header == translationStatus.Text && !translationStatus.Text.Contains("等待译文")
                    && transcript.Text.EndsWith("The failed caption remains in English."),
                    "provider failure clears pending status and leaves the caption in English");
            }

            Caption("Pending when disabled.", true);
            using var pending = await listener.AcceptTcpClientAsync(timeout.Token);
            using var pendingStream = pending.GetStream();
            await ReadRequestAsync(pendingStream, timeout.Token);
            menu.IsChecked = false;
            Check(check.IsChecked == false && transcript.Text.Contains(translation) && menuStatus.Visibility == Visibility.Collapsed,
                "menu turns translation off without reverting completed translations");
            Check(await DisconnectedAsync(pendingStream, timeout.Token), "turning off translation cancels the in-flight connection");
            menu.IsChecked = true;
            Check(check.IsChecked == true && !listener.Pending(), "menu re-enables translation without replaying history");

            earlyMenu.IsChecked = true;
            Check(earlyCheck.IsChecked == true && translationStatus.Text.Contains("提前翻译"),
                "compact menu enables early translation explicitly");
            Caption("Remember this. Unfinished tail", false);
            using (var early = await listener.AcceptTcpClientAsync(timeout.Token))
            using (var earlyStream = early.GetStream())
            {
                Check(await ReadCaptionAsync(earlyStream, timeout.Token) == "Remember this.", "early mode sends only the sentence with terminal punctuation");
                await ReplyAsync(earlyStream, "请记住。", timeout.Token);
                await WaitForDisplayAsync("请记住。 Unfinished tail");
            }
            Caption("Do not remember this. Unfinished tail", false);
            Check(transcript.Text.EndsWith("Do not remember this. Unfinished tail"), "a correction immediately restores the updated English");
            using (var correction = await listener.AcceptTcpClientAsync(timeout.Token))
            using (var correctionStream = correction.GetStream())
            {
                Check(await ReadCaptionAsync(correctionStream, timeout.Token) == "Do not remember this.", "only the corrected sentence is retranslated");
                await ReplyAsync(correctionStream, "不要记住。", timeout.Token);
                await WaitForDisplayAsync("不要记住。 Unfinished tail");
            }
            Caption("Do not remember this. Unfinished tail", true);
            using (var tail = await listener.AcceptTcpClientAsync(timeout.Token))
            using (var tailStream = tail.GetStream())
            {
                Check(await ReadCaptionAsync(tailStream, timeout.Token) == "Unfinished tail", "native final translates the tail and reuses Chinese already shown");
                await ReplyAsync(tailStream, "未完成的后半句", timeout.Token);
                await WaitForDisplayAsync("不要记住。 未完成的后半句");
            }

            Caption("Please wait for this.", false);
            using (var obsolete = await listener.AcceptTcpClientAsync(timeout.Token))
            using (var obsoleteStream = obsolete.GetStream())
            {
                await ReadCaptionAsync(obsoleteStream, timeout.Token);
                earlyMenu.IsChecked = false;
                Check(await DisconnectedAsync(obsoleteStream, timeout.Token) && earlyCheck.IsChecked == false,
                    "disabling only early translation cancels a provisional request");
            }
            Caption("Still pending at close.", true);
            using var last = await listener.AcceptTcpClientAsync(timeout.Token);
            using var lastStream = last.GetStream();
            await ReadRequestAsync(lastStream, timeout.Token);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            Check(await DisconnectedAsync(lastStream, timeout.Token) && File.ReadAllText(session).EndsWith("Still pending at close."),
                "close saves the original without waiting for the provider");
            File.Delete(session);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            app.Shutdown(1);
        }
        finally { File.Delete(export); }

        async Task IdleAsync() => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, timeout.Token);
        async Task WaitForDisplayAsync(string suffix)
        {
            var box = Field<TextBox>(window, "TranscriptBox");
            while (!box.Text.EndsWith(suffix)) await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<bool> DisconnectedAsync(Stream stream, CancellationToken token)
    {
        try { return await stream.ReadAsync(new byte[1], token) == 0; }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        { return true; }
    }

    private static async Task<string?> ReadCaptionAsync(Stream stream, CancellationToken token)
    {
        using var request = JsonDocument.Parse(await ReadRequestAsync(stream, token));
        return request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
    }

    private static async Task<string> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        if (await reader.ReadLineAsync(token) is not "POST /v1/chat/completions HTTP/1.1")
            throw new InvalidDataException("Unexpected request line");
        var length = 0;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..]);
        if (length is <= 0 or > 4096) throw new InvalidDataException("Unexpected request size");
        var body = new char[length]; // Client JSON escapes non-ASCII text.
        if (await reader.ReadBlockAsync(body.AsMemory(), token) != length) throw new EndOfStreamException();
        return new string(body);
    }

    private static async Task ReplyAsync(Stream stream, string translation, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { choices = new[] { new { message = new { content = translation } } } });
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), token);
        await stream.WriteAsync(body, token);
    }

    private static void Check(bool passed, string description)
    {
        if (!passed) throw new Exception(description);
        Console.WriteLine("PASS " + description);
    }
}
