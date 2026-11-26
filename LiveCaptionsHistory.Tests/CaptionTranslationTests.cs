using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LiveCaptionsHistory;

internal static class CaptionTranslationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    private const string Original = "This sentence repeats.";

    public static async Task RunAsync()
    {
        await RequestAndSettingsAsync();
        await CaptionOrderAndCancellationAsync();
    }

    private static async Task RequestAndSettingsAsync()
    {
        var settings = Path.Combine(Path.GetTempPath(), $"caption-settings-{Guid.NewGuid():N}.json");
        using var handler = new StubHandler();
        try
        {
            Check(CaptionLlmClient.Load(null, null, null, settings, handler) is null, "missing configuration disables translation");
            await File.WriteAllTextAsync(settings, "not json");
            Check(CaptionLlmClient.Load(null, null, null, settings, handler) is null, "malformed configuration disables translation");
            await File.WriteAllTextAsync(settings, """{"endpoint":"http://127.0.0.1:1/v1/chat/completions","model":"local"}""");
            Check(CaptionLlmClient.Load("ftp://example.com", "invalid", null, settings, handler) is null,
                "invalid explicit configuration does not fall through");

            using var explicitClient = CaptionLlmClient.Load(Endpoint, " cloud ", " test-key ", settings, handler)!;
            var request = explicitClient.TranslateAsync(Original, CancellationToken.None);
            var call = await handler.NextAsync();
            using (var json = JsonDocument.Parse(call.Body))
            {
                var root = json.RootElement;
                var messages = root.GetProperty("messages");
                Check(call.Endpoint == Endpoint && call.Method == HttpMethod.Post && call.ContentType == "application/json"
                    && call.Authorization == "Bearer test-key" && root.GetProperty("model").GetString() == "cloud"
                    && messages[0].GetProperty("role").GetString() == "system"
                    && messages[0].GetProperty("content").GetString()!.Contains("Simplified Chinese")
                    && messages[1].GetProperty("role").GetString() == "user"
                    && messages[1].GetProperty("content").GetString() == Original,
                    "Chat Completions request uses explicit settings and sends the source caption as data");
                Check(root.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "DeepSeek thinking is disabled");
            }
            call.Translate("这是重复的句子。");
            Check(await request.WaitAsync(Deadline) == "这是重复的句子。", "translation reads message.content");

            using var localClient = CaptionLlmClient.Load(Endpoint, null, "ignored", settings, handler)!;
            var localRequest = localClient.TranslateAsync("Local.", CancellationToken.None);
            call = await handler.NextAsync();
            using (var json = JsonDocument.Parse(call.Body))
                Check(call.Endpoint == "http://127.0.0.1:1/v1/chat/completions" && call.Authorization is null
                    && json.RootElement.GetProperty("model").GetString() == "local"
                    && !json.RootElement.TryGetProperty("thinking", out _), "partial override uses file settings and allows keyless local providers");
            call.Reply(HttpStatusCode.Unauthorized, "{}");
            Check(await localRequest.WaitAsync(Deadline) is null && !handler.TryTake(), "permanent error leaves English without retrying");

            var retried = localClient.TranslateAsync("Try once more.", CancellationToken.None);
            var rateLimited = await handler.NextAsync();
            rateLimited.Reply(HttpStatusCode.TooManyRequests, "{}", TimeSpan.Zero);
            var retry = await handler.NextAsync();
            Check(retry.Body == rateLimited.Body, "brief rate limit retries the same caption");
            retry.Translate("再试一次。");
            Check(await retried.WaitAsync(Deadline) == "再试一次。", "retry can recover a translation");

            var unavailable = localClient.TranslateAsync("Service unavailable.", CancellationToken.None);
            (await handler.NextAsync()).Reply(HttpStatusCode.ServiceUnavailable, "{}");
            (await handler.NextAsync()).Reply(HttpStatusCode.BadGateway, "{}");
            Check(await unavailable.WaitAsync(Deadline) is null && !handler.TryTake(), "temporary server failures get only one retry");

            var longWait = localClient.TranslateAsync("Busy provider.", CancellationToken.None);
            (await handler.NextAsync()).Reply(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(10));
            Check(await longWait.WaitAsync(Deadline) is null && !handler.TryTake(), "long rate limits leave English instead of blocking a slot");
            foreach (var body in new[] { "{", "{}", """{"choices":[{"message":{"content":" "}}]}""",
                """{"choices":[{"finish_reason":"length","message":{"content":"译文"}}]}""",
                """{"choices":[{"message":{"content":"\uD800"}}]}""", new string(' ', 65537) })
            {
                var failed = localClient.TranslateAsync("Keep this.", CancellationToken.None);
                (await handler.NextAsync()).Reply(HttpStatusCode.OK, body);
                Check(await failed.WaitAsync(Deadline) is null, "invalid, incomplete or oversized reply returns no replacement");
            }
            var timedOut = localClient.TranslateAsync("Keep this.", CancellationToken.None);
            (await handler.NextAsync()).Response.SetException(new TaskCanceledException("Simulated provider timeout"));
            Check(await timedOut.WaitAsync(Deadline) is null, "timeout returns no replacement");
        }
        finally { File.Delete(settings); }
    }

    private static async Task CaptionOrderAndCancellationAsync()
    {
        using var handler = new StubHandler();
        var client = CaptionLlmClient.Load(Endpoint, "test", null, "", handler)!;
        var results = Channel.CreateUnbounded<(FinalizedCaption Line, string? Text)>();
        var run = new CaptionTranslationRun(client, (_, request, text) => results.Writer.TryWrite((request.Caption, text)));
        var captions = new CaptionAccumulator();
        try
        {
            bool Feed(string text, bool final, string? language)
            {
                captions.ObserveRecognition(text, final, out var line);
                return run.TryQueue(line, language);
            }
            Check(!Feed(" ", true, "en") && !Feed("你好。", true, "zh-CN") && !Feed("Unknown language.", true, null)
                && !Feed(Original, false, "en-US"), "only nonempty English finals are queued");
            Check(!captions.ObserveRecognition(Original, true, out var first) && run.TryQueue(first, "EN-us"),
                "a final identical to its partial still queues once");
            var firstCall = await handler.NextAsync();
            Check(Feed(Original, true, "en"), "consecutive identical utterances both queue");
            var secondCall = await handler.NextAsync();
            Check(!Feed("Unfinished.", false, "en-GB") && !Feed("Current partial.", false, "en-GB"),
                "partial revisions do not queue while HTTP is in flight");
            secondCall.Translate("第二条。");
            var second = await results.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Check(second.Text is not null && captions.ApplyTranslation(second.Line, second.Text)
                && captions.DisplayText.Contains($"{Original}\n第二条。\nCurrent partial."), "second result updates its own line before the first result");
            firstCall.Translate("第一条。");
            var earlier = await results.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Check(earlier.Text is not null && captions.ApplyTranslation(earlier.Line, earlier.Text)
                && captions.DisplayText.EndsWith("第一条。\n第二条。\nCurrent partial.")
                && captions.Text.EndsWith($"{Original}\n{Original}\nCurrent partial."),
                "out-of-order replies preserve line order, partial and original text");
            Check(!captions.ApplyTranslation(earlier.Line, "不要覆盖") && !captions.ApplyTranslation(second.Line with { Index = -1 }, "不要覆盖"),
                "duplicate and invalid replacements are ignored");
            captions.SourceClosed();
            captions.ObserveRecognition("Partial at pause.", false);
            captions.Pause();
            Check(captions.DisplayText.EndsWith("Current partial.\nPartial at pause.") && !Feed("Ignored final.", true, "en-US"),
                "source close and pause preserve English without producing a final for translation");
            captions.Resume();
            Check(!Feed("Last words.", false, "en") && Feed(" ", true, "en"),
                "an explicit blank final can finalize the recognizer's nonempty partial");
            var inFlight = new List<Call> { await handler.NextAsync() };
            for (var i = 2; i <= 4; i++)
            {
                Check(Feed($"Caption {i} in the burst.", true, "en"), $"caption {i} in the burst queues");
                inFlight.Add(await handler.NextAsync());
            }
            Check(Feed("Queued but not started.", true, "en") && !handler.TryTake(), "four slow requests run concurrently; the fifth waits");
            inFlight[2].Reply(HttpStatusCode.Unauthorized, "{}");
            var failed = await results.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Check(failed is { Line.Text: "Caption 3 in the burst.", Text: null }, "failed caption reports completion without replacing English");
            var next = await handler.NextAsync();
            Check(next.Body.Contains("Queued but not started."), "next caption starts as soon as a slot opens");
            inFlight.RemoveAt(2);
            inFlight.Add(next);
            await run.StopAsync().WaitAsync(Deadline);
            Check(inFlight.All(call => call.Cancelled.IsCompletedSuccessfully)
                && !handler.TryTake() && !results.Reader.TryRead(out _)
                && captions.DisplayText.EndsWith("Caption 4 in the burst.\nQueued but not started."),
                "stop cancels in-flight requests and keeps English visible");
        }
        finally { await run.StopAsync().WaitAsync(Deadline); }
    }

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = new Call(request.RequestUri!.AbsoluteUri, request.Method, request.Headers.Authorization?.ToString(),
                request.Content!.Headers.ContentType?.MediaType, await request.Content.ReadAsStringAsync(cancellationToken));
            _calls.Writer.TryWrite(call);
            try { return await call.Response.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { call.Cancellation.TrySetResult(); throw; }
        }

        public Task<Call> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        public bool TryTake() => _calls.Reader.TryRead(out _);
    }

    private sealed record Call(string Endpoint, HttpMethod Method, string? Authorization, string? ContentType, string Body)
    {
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Cancelled => Cancellation.Task;

        public void Reply(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is { } delay) response.Headers.RetryAfter = new(delay);
            Response.SetResult(response);
        }

        public void Translate(string text) => Reply(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }));
    }
}
