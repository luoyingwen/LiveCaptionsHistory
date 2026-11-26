namespace LiveCaptionsHistory;

internal static class CaptionSentenceBoundaries
{
    internal readonly record struct Part(string Text, bool Ready);

    // Conservative for ambiguous stops. Windows' final event still translates
    // the remainder, including captions with no reliable punctuation boundary.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "no", "fig", "approx", "inc", "ltd", "co", "dept",
        "e.g", "i.e", "a.m", "p.m", "u.s", "u.k"
    };

    public static IReadOnlyList<Part> Split(string text, bool final)
    {
        var parts = new List<Part>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsStop(text[i])) continue;
            var stop = i;
            while (i + 1 < text.Length && IsStop(text[i + 1])) i++;
            var punctuationEnd = i;
            while (i + 1 < text.Length && IsCloser(text[i + 1])) i++;
            var end = i + 1;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
            if (text[stop] == '.' && !HasStrongStop(text.AsSpan(stop, punctuationEnd - stop + 1))
                && (stop != punctuationEnd || IsAmbiguousPeriod(text, stop, end))) continue;
            var sentence = text[start..end].Trim();
            if (!sentence.Any(char.IsLetter)) continue;
            parts.Add(new(sentence, true));
            start = end;
        }
        if (text[start..].Trim() is { Length: > 0 } tail) parts.Add(new(tail, final));
        return parts;
    }

    private static bool IsAmbiguousPeriod(string text, int stop, int end)
    {
        var start = stop;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.')) start--;
        var token = text[start..stop];
        if (Abbreviations.Contains(token) || (token.Length == 1 && char.IsUpper(token[0]) && token != "I")
            || (token.Contains('.') && token.Split('.').All(part => part.Length == 1))) return true;
        // "3." at the live edge can still become "3.14". At final it is sent as part of the remainder.
        if (end == text.Length && stop > 0 && char.IsDigit(text[stop - 1])) return true;
        // Quoted speech can be followed by an attribution rather than a new sentence.
        if (end > stop + 1)
        {
            var next = end;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next < text.Length && char.IsLower(text[next])) return true;
        }
        return false;
    }

    private static bool HasStrongStop(ReadOnlySpan<char> punctuation) => punctuation.IndexOfAny('?', '!', '。') >= 0
        || punctuation.IndexOfAny('？', '！') >= 0;
    private static bool IsStop(char c) => c is '.' or '?' or '!' or '。' or '？' or '！';
    private static bool IsCloser(char c) => c is '"' or '\'' or '”' or '’' or ')' or ']' or '}';
}
