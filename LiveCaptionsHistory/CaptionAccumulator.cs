using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsHistory;

// Partial events replace only the unfinished utterance. Final events establish
// a line boundary, even when two consecutive utterances have identical text.
public sealed class CaptionAccumulator
{
    private readonly StringBuilder _archive = new();
    private string _current = "";
    public bool Recording { get; private set; } = true;
    public string Text => _current.Length == 0 ? _archive.ToString()
        : _archive.Length == 0 ? _current : _archive + "\n" + _current;

    public bool ObserveRecognition(string input, bool isFinal)
    {
        if (!Recording) return false;
        var next = Normalize(input);
        if (next.Length == 0 && !isFinal) return false;
        var before = Text;
        if (next.Length > 0) _current = next;
        if (isFinal) Commit();
        return before != Text;
    }

    public void Pause()
    {
        if (!Recording) return;
        Commit();
        Recording = false;
    }
    public void Resume() => Recording = true;
    public void SourceClosed() => Commit();
    private void Commit()
    {
        if (_current.Length == 0) return;
        if (_archive.Length > 0) _archive.Append("\n");
        _archive.Append(_current);
        _current = "";
    }
    public static string Normalize(string input) => Regex.Replace(input.Replace('\u00a0', ' '), @"\s+", " ").Trim();
    public static int CommonPrefix(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        // Never split a UTF-16 surrogate pair when replacing text.
        if (i > 0 && char.IsHighSurrogate(a[i - 1]) && (i < a.Length || i < b.Length)) i--;
        return i;
    }
}
