using System.Text.RegularExpressions;

namespace LiveCaptionsHistory;

internal readonly record struct FinalizedCaption(int Index, string Text);

// Partial events replace only the unfinished utterance. Final events establish
// a line boundary, even when two consecutive utterances have identical text.
public sealed class CaptionAccumulator
{
    private readonly List<string> _archive = new();
    private readonly Dictionary<int, string> _translations = new();
    private string? _displayArchive;
    private string _current = "";
    private string? _currentDisplay;
    internal int CurrentIndex => _archive.Count;
    internal string CurrentText => _current;
    public bool Recording { get; private set; } = true;
    public string Text => WithCurrent(string.Join('\n', _archive), _current);
    internal string DisplayText => WithCurrent(_displayArchive ??= _translations.Count == 0
        ? string.Join('\n', _archive)
        : string.Join('\n', _archive.Select((original, index) => _translations.GetValueOrDefault(index, original))), _currentDisplay ?? _current);

    private static string WithCurrent(string archive, string current) => current.Length == 0 ? archive
        : archive.Length == 0 ? current : archive + "\n" + current;

    public bool ObserveRecognition(string input, bool isFinal) => ObserveRecognition(input, isFinal, out _);

    internal bool ObserveRecognition(string input, bool isFinal, out FinalizedCaption? finalized)
    {
        finalized = null;
        if (!Recording) return false;
        var next = Normalize(input);
        var changed = next.Length > 0 && next != _current;
        if (changed)
        {
            _current = next;
            _currentDisplay = null;
        }
        if (isFinal && _current.Length > 0)
        {
            finalized = new(_archive.Count, _current);
            Commit();
        }
        return changed;
    }

    internal bool ApplyTranslation(FinalizedCaption caption, string translation)
    {
        var display = Normalize(translation);
        if (display.Length == 0 || display == caption.Text || caption.Index < 0 || caption.Index >= _archive.Count
            || _archive[caption.Index] != caption.Text || !_translations.TryAdd(caption.Index, display)) return false;
        _displayArchive = null;
        return true;
    }

    internal void SetEarlyDisplay(int index, string? display)
    {
        if (index == CurrentIndex)
        {
            _currentDisplay = display == _current ? null : display;
            return;
        }
        if (display is null || display == _archive[index])
        {
            if (_translations.Remove(index)) _displayArchive = null;
        }
        else if (_translations.GetValueOrDefault(index) != display)
        {
            _translations[index] = display;
            _displayArchive = null;
        }
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
        _archive.Add(_current);
        _displayArchive = null;
        _current = "";
        _currentDisplay = null;
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
