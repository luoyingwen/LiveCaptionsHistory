using LiveCaptionsHistory;

var count = 0;
void Check<T>(string name, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
        throw new Exception($"{name}: expected [{expected}], got [{actual}]");
    Console.WriteLine("PASS " + name);
    count++;
}
CaptionAccumulator Feed(params (string Text, bool IsFinal)[] events)
{
    var buffer = new CaptionAccumulator();
    foreach (var item in events) buffer.ObserveRecognition(item.Text, item.IsFinal);
    return buffer;
}

Check("partial extension", Feed(("Hello", false), ("Hello world", false)).Text, "Hello world");
Check("partial recognition correction", Feed(("The capital is pairs", false), ("The capital is Paris", false)).Text, "The capital is Paris");
Check("partial contraction", Feed(("This is a test and extra", false), ("This is a test", false)).Text, "This is a test");
Check("unrelated partial replaces only current utterance", Feed(("Earlier sentence.", true), ("incorrect words", false), ("correct result", false)).Text, "Earlier sentence.\ncorrect result");
Check("final corrects current utterance", Feed(("识别中的文子", false), ("识别中的文字。", true)).Text, "识别中的文字。");
Check("new partial preserves previous final", Feed(("第一句。", true), ("第二", false), ("第二句。", false)).Text, "第一句。\n第二句。");
Check("identical consecutive utterances are retained", Feed(("谢谢。", false), ("谢谢。", true), ("谢谢。", false), ("谢谢。", true)).Text, "谢谢。\n谢谢。");
Check("final only events are retained", Feed(("One.", true), ("Two.", true), ("Two.", true)).Text, "One.\nTwo.\nTwo.");
Check("accumulated text contains no blank lines", Feed(("One.", true), ("Two.", false), ("Two.", true), ("Three.", false)).Text.Contains("\n\n"), false);

var unchanged = Feed(("Same words.", false));
Check("duplicate partial reports no display change", unchanged.ObserveRecognition("Same words.", false), false);
Check("identical final reports no display change", unchanged.ObserveRecognition("Same words.", true), false);
unchanged.ObserveRecognition("Next sentence.", false);
Check("identical final still creates an utterance boundary", unchanged.Text, "Same words.\nNext sentence.");
Check("blank partial does not erase unfinished text", Feed(("Keep these words.", false), ("", false), ("  ", false)).Text, "Keep these words.");
Check("blank final retains and commits unfinished text", Feed(("Keep these words.", false), (" ", true), ("Next words.", false)).Text, "Keep these words.\nNext words.");
Check("empty events add no blank lines", Feed(("", true), ("One.", true), ("", true), ("Two.", true)).Text, "One.\nTwo.");

var pause = Feed(("Completed.", true), ("Visible partial", false));
pause.Pause();
Check("pause preserves visible text", pause.Text, "Completed.\nVisible partial");
Check("pause changes recording state", pause.Recording, false);
Check("paused partial is ignored", pause.ObserveRecognition("Paused speech", false), false);
Check("paused final is ignored", pause.ObserveRecognition("Paused speech.", true), false);
pause.Pause();
Check("repeated pause does not duplicate visible partial", pause.Text, "Completed.\nVisible partial");
pause.Resume();
Check("resume changes recording state", pause.Recording, true);
pause.ObserveRecognition("Fresh speech.", false);
Check("resume starts new utterance without backfill", pause.Text, "Completed.\nVisible partial\nFresh speech.");

var restart = Feed(("Completed.", true), ("Preserved before close", false));
restart.SourceClosed();
restart.SourceClosed();
Check("source close preserves current utterance once", restart.Text, "Completed.\nPreserved before close");
restart.ObserveRecognition("After reconnecting.", true);
Check("reconnect keeps all previous text", restart.Text, "Completed.\nPreserved before close\nAfter reconnecting.");
restart.Pause();
restart.SourceClosed();
Check("source close does not resume paused recording", restart.Recording, false);

Check("recognition whitespace is normalized", Feed(("one\r\ntwo\u00a0three", true)).Text, "one two three");
Check("unicode recognition correction", Feed(("字幕内容🙂正在识别", false), ("字幕内容🙂正在识别完成。", true)).Text, "字幕内容🙂正在识别完成。");
Check("common prefix never splits differing emoji", CaptionAccumulator.CommonPrefix("字幕🙂旧", "字幕🙃新"), 2);
Check("common prefix retains complete matching emoji", CaptionAccumulator.CommonPrefix("字幕🙂旧", "字幕🙂新"), 4);
Check("common prefix handles identical emoji", CaptionAccumulator.CommonPrefix("🙂", "🙂"), 2);

var paragraphs = Enumerable.Range(0, 1000).Select(i => $"Sentence {i}.").ToArray();
var longHistory = Feed(paragraphs.Select(text => (text, true)).ToArray());
longHistory.ObserveRecognition("Current unfinished sentence", false);
longHistory.ObserveRecognition("Current corrected sentence.", true);
Check("long history survives partial revisions", longHistory.Text, string.Join("\n", paragraphs) + "\nCurrent corrected sentence.");
Console.WriteLine($"{count} transcript tests passed.");
