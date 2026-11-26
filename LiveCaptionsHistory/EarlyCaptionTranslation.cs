namespace LiveCaptionsHistory;

// UI-thread state. Translation requests may complete in any order; only an
// exact, still-live source revision can update an utterance's display.
internal sealed class EarlyCaptionTranslation(CaptionAccumulator captions, Func<CaptionTranslationRequest, bool> enqueue)
{
    internal const long SettleMilliseconds = 400;
    private readonly Dictionary<long, (Utterance Line, Sentence Sentence)> _pending = new();
    private Utterance? _current;
    private long _revision;

    public int FailedCount { get; private set; }
    public bool HasDebouncedWork => _current?.Sentences.Values.Any(sentence => sentence.State == State.Waiting) == true;
    public int PendingCount => _pending.Count + (_current?.Sentences.Values.Count(sentence => sentence.State == State.Waiting) ?? 0);

    public void Observe(FinalizedCaption caption, bool final, long now)
    {
        var line = _current ??= new(caption.Index);
        var segments = CaptionSentenceBoundaries.Split(caption.Text, final);
        var eligible = segments.Where(part => part.Ready).Select(part => part.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var text in line.Sentences.Keys.Where(text => !eligible.Contains(text)).ToArray())
        {
            Cancel(line.Sentences[text]);
            line.Sentences.Remove(text);
        }
        foreach (var part in segments)
        {
            if (!part.Ready || line.Sentences.ContainsKey(part.Text)) continue;
            line.Sentences.Add(part.Text, new() { Due = now + SettleMilliseconds });
        }
        line.Parts = segments;
        line.Final = final;
        UpdateDisplay(line);
        if (!final) return;

        // Keep any translated or still-running identical sentence. A failed
        // provisional attempt may try again once Windows confirms the text.
        _current = null;
        foreach (var (text, sentence) in line.Sentences)
            if (sentence.State is State.Waiting or State.Failed) Queue(line, text, sentence);
    }

    public void Flush(long now)
    {
        if (_current is not { } line) return;
        foreach (var (text, sentence) in line.Sentences)
            if (sentence.State == State.Waiting && sentence.Due <= now) Queue(line, text, sentence);
    }

    public bool Complete(CaptionTranslationRequest request, string? translation)
    {
        if (!_pending.Remove(request.Revision, out var completed)) return false;
        var (line, sentence) = completed;
        sentence.Cancellation!.Dispose();
        sentence.Cancellation = null;
        sentence.Translation = translation is null ? null : CaptionAccumulator.Normalize(translation);
        if (string.IsNullOrEmpty(sentence.Translation))
        {
            sentence.State = State.Failed;
            if (line.Final) FailedCount++;
        }
        else
        {
            sentence.State = State.Translated;
            UpdateDisplay(line);
        }
        return true;
    }

    public void DiscardCurrent()
    {
        if (_current is not { } line) return;
        _current = null;
        foreach (var sentence in line.Sentences.Values) Cancel(sentence);
        captions.SetEarlyDisplay(line.Index, null);
    }

    public void Stop()
    {
        DiscardCurrent();
        foreach (var (_, sentence) in _pending.Values.ToArray()) Cancel(sentence);
    }

    private void Queue(Utterance line, string text, Sentence sentence)
    {
        var cancellation = new CancellationTokenSource();
        var revision = ++_revision;
        if (!enqueue(new(new(line.Index, text), revision, cancellation.Token)))
        {
            cancellation.Dispose();
            sentence.State = State.Failed;
            return;
        }
        sentence.Cancellation = cancellation;
        sentence.Revision = revision;
        sentence.State = State.Pending;
        _pending.Add(revision, (line, sentence));
    }

    private void Cancel(Sentence sentence)
    {
        if (sentence.Cancellation is not { } cancellation) return;
        _pending.Remove(sentence.Revision);
        sentence.Cancellation = null;
        _ = CancelAsync(cancellation);
    }

    private static async Task CancelAsync(CancellationTokenSource cancellation)
    {
        using (cancellation) await cancellation.CancelAsync().ConfigureAwait(false);
    }

    private void UpdateDisplay(Utterance line) => captions.SetEarlyDisplay(line.Index, string.Join(' ', line.Parts.Select(part =>
        part.Ready && line.Sentences.TryGetValue(part.Text, out var sentence) ? sentence.Translation ?? part.Text : part.Text)));

    private enum State { Waiting, Pending, Failed, Translated }
    private sealed class Sentence
    {
        public long Due;
        public long Revision;
        public State State;
        public CancellationTokenSource? Cancellation;
        public string? Translation;
    }
    private sealed class Utterance(int index)
    {
        public int Index { get; } = index;
        public bool Final;
        public IReadOnlyList<CaptionSentenceBoundaries.Part> Parts = [];
        public Dictionary<string, Sentence> Sentences { get; } = new(StringComparer.Ordinal);
    }
}
