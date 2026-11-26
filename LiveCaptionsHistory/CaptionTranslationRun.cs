using System.Threading.Channels;

namespace LiveCaptionsHistory;

internal readonly record struct CaptionTranslationRequest(FinalizedCaption Caption, long Revision = 0, CancellationToken Cancellation = default);

// One run per opt-in. The UI owns submission and stop; background workers
// own HTTP. Results carry line identity so they may complete out of order.
internal sealed class CaptionTranslationRun
{
    internal const int MaxParallelRequests = 4;

    private readonly Channel<CaptionTranslationRequest> _queue = Channel.CreateUnbounded<CaptionTranslationRequest>(
        new UnboundedChannelOptions { SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly CaptionLlmClient _client;
    private readonly Action<CaptionTranslationRun, CaptionTranslationRequest, string?> _completed;
    private readonly Task _workers;
    private Task? _stopping;

    public CaptionTranslationRun(CaptionLlmClient client, Action<CaptionTranslationRun, CaptionTranslationRequest, string?> completed)
    {
        _client = client;
        _completed = completed;
        _workers = Task.WhenAll(Enumerable.Range(0, MaxParallelRequests).Select(_ => Task.Run(WorkAsync)));
    }

    public bool TryQueue(FinalizedCaption? caption, string? language) =>
        caption is { } line && !string.IsNullOrWhiteSpace(line.Text)
        && IsEnglish(language) && TryQueue(new CaptionTranslationRequest(line));

    internal static bool IsEnglish(string? language) => language is not null && (language.Equals("en", StringComparison.OrdinalIgnoreCase)
        || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase));

    public bool TryQueue(CaptionTranslationRequest work) => _queue.Writer.TryWrite(work);

    private async Task WorkAsync()
    {
        var token = _stop.Token;
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested) break;
                if (work.Cancellation.IsCancellationRequested) continue;
                using var linked = work.Cancellation.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, work.Cancellation) : null;
                var requestToken = linked?.Token ?? token;
                var result = await _client.TranslateAsync(work.Caption.Text, requestToken).ConfigureAwait(false);
                if (!requestToken.IsCancellationRequested) _completed(this, work, result);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task StopAsync() => _stopping ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        var cancel = _stop.CancelAsync();
        _queue.Writer.TryComplete();
        try { await Task.WhenAll(cancel, _workers).ConfigureAwait(false); }
        finally
        {
            while (_queue.Reader.TryRead(out _)) { }
            _client.Dispose();
            _stop.Dispose();
        }
    }
}
