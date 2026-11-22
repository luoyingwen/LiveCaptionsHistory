namespace SpeechBackendHost;

internal static class RecognitionLanguageSelection
{
    // Null leaves the language initialized by Windows unchanged.
    internal static string? Select(string[] installed, string? requested)
    {
        string? Find(string code) => installed.FirstOrDefault(language =>
            string.Equals(language, code, StringComparison.OrdinalIgnoreCase));

        if (requested is not null)
            return Find(requested) ?? throw new InvalidOperationException("所选语言的本机模型尚未安装。");

        return Find("en-US") ?? installed.FirstOrDefault(language =>
            language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
            language.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
    }
}
