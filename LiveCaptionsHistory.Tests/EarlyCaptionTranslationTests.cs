using LiveCaptionsHistory;

internal static class EarlyCaptionTranslationTests
{
    public static void Run()
    {
        var mixed = CaptionSentenceBoundaries.Split("Dr. Smith paid 3.14. Use e.g. a pen. \"Ready?\" Not done", final: false);
        Check(mixed.SequenceEqual(new CaptionSentenceBoundaries.Part[]
        {
            new("Dr. Smith paid 3.14.", true), new("Use e.g. a pen.", true), new("\"Ready?\"", true), new("Not done", false)
        }), "sentence splitting preserves titles, decimals, abbreviations and closing quotes");
        foreach (var text in new[] { "The number is 3.", "Wait...", "Use example.com", "Call J. R. R.", "Here is a clause,", "No punctuation" })
        {
            Check(CaptionSentenceBoundaries.Split(text, false).All(part => !part.Ready)
                && CaptionSentenceBoundaries.Split(text, true).All(part => part.Ready), $"ambiguous or missing stop falls back to engine final: {text}");
        }

        var captions = new CaptionAccumulator();
        var requests = new List<CaptionTranslationRequest>();
        var translator = new EarlyCaptionTranslation(captions, work => { requests.Add(work); return true; });
        var settle = EarlyCaptionTranslation.SettleMilliseconds;
        void Feed(string text, long time, bool final = false)
        {
            captions.ObserveRecognition(text, final, out var line);
            translator.Observe(line ?? new(captions.CurrentIndex, captions.CurrentText), final, time);
        }
        Feed("You need to remember. The", 0);
        translator.Flush(settle - 1);
        Check(requests.Count == 0, "punctuated sentence waits briefly for revisions");
        Feed("You need to remember. The next phrase", settle - 1);
        translator.Flush(settle);
        var first = requests.Single();
        Check(first.Caption.Text == "You need to remember." && translator.PendingCount == 1,
            "an evolving tail does not reset the complete sentence's debounce or get sent");
        Check(translator.Complete(first, "你需要记住。") && captions.DisplayText == "你需要记住。 The next phrase",
            "Chinese displays before the native final, leaving the unfinished tail in English");
        Check(captions.Text == "You need to remember. The next phrase", "original English remains separate for autosave");

        Feed("You do not need to remember. The next phrase", 500);
        Check(captions.DisplayText == captions.Text && !translator.Complete(first, "过时"), "correction immediately removes stale Chinese");
        translator.Flush(500 + settle);
        var changed = requests[^1];
        Feed("You need to remember. The next phrase", 1000);
        Check(changed.Cancellation.IsCancellationRequested && !translator.Complete(changed, "过时"),
            "replaced work is canceled and even a late reply cannot overwrite the revision");
        translator.Flush(1000 + settle);
        var back = requests[^1];
        Check(back.Revision != first.Revision && back.Caption.Text == first.Caption.Text
            && !translator.Complete(first, "过时"), "A → B → A cannot accept a completion from the original A");
        translator.Complete(back, "你需要记住。");

        var beforeFinal = requests.Count;
        Feed("You need to remember. The next phrase", 1500, final: true);
        var finalTail = requests[^1];
        Check(requests.Count == beforeFinal + 1 && finalTail.Caption.Text == "The next phrase"
            && captions.DisplayText == "你需要记住。 The next phrase", "final reuses the translated sentence and sends only the unpunctuated remainder");
        Feed("New caption.", 1600);
        translator.Flush(1600 + settle);
        var newCaption = requests[^1];
        Feed("New caption.", 2050, final: true);
        Check(requests[^1] == newCaption && translator.Complete(newCaption, "新字幕。"), "final reuses an identical in-flight request without resending");
        translator.Complete(finalTail, "下一个短语");
        Check(captions.DisplayText == "你需要记住。 下一个短语\n新字幕。"
            && captions.Text == "You need to remember. The next phrase\nNew caption.", "late final translations update only their own archived line");

        Feed("A temporary failure.", 2200);
        translator.Flush(2200 + settle);
        var failed = requests[^1];
        translator.Complete(failed, null);
        Feed("A temporary failure.", 2700, final: true);
        Check(requests[^1].Revision != failed.Revision && requests[^1].Caption == failed.Caption,
            "engine final retries an unsuccessful provisional sentence");
        translator.Complete(requests[^1], null);
        Check(translator.FailedCount == 1 && captions.DisplayText.EndsWith("A temporary failure."), "failed final stays English and is counted");

        Feed("No punctuation here", 3000);
        translator.Flush(3000 + settle);
        var beforeFallback = requests.Count;
        Feed("No punctuation here", 3500, final: true);
        var fallback = requests[^1];
        Check(requests.Count == beforeFallback + 1 && fallback.Caption.Text == "No punctuation here", "engine final alone still translates unpunctuated text");
        Feed("One sentence. A second sentence.", 3600);
        translator.Flush(3600 + settle);
        var oldLast = requests[^1];
        var oldFirst = requests[^2];
        Feed("One sentence and a second sentence.", 4100);
        Check(oldFirst.Cancellation.IsCancellationRequested && oldLast.Cancellation.IsCancellationRequested,
            "merging sentences cancels both obsolete requests");
        translator.Flush(4100 + settle);
        var merged = requests[^1];
        translator.Complete(merged, "一句与第二句。");
        Check(captions.DisplayText.EndsWith("一句与第二句。"), "replacement can change sentence boundaries");
        translator.DiscardCurrent();
        Check(captions.DisplayText.EndsWith("One sentence and a second sentence.")
            && !fallback.Cancellation.IsCancellationRequested, "abandoning unfinished text restores English without canceling prior finals");
        translator.Stop();
        Check(fallback.Cancellation.IsCancellationRequested && !translator.Complete(fallback, "过时"), "stopping also invalidates finalized work");
    }

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
}
