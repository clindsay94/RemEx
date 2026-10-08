namespace Remex.Desktop.Services.FilePreview;

/// <summary>What a coloured stretch of a previewed line is (2026-10-08 redesign).</summary>
public enum SyntaxKind
{
    Plain,
    Comment,
    String,
    Number,
    Keyword,
    /// <summary>A JSON object key, or an XML tag or attribute name.</summary>
    Key,
    Timestamp,
    LogError,
    LogWarning,
    LogDebug,
}

/// <summary>One coloured stretch of a line: characters [<see cref="Start"/>, Start + <see cref="Length"/>).</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind);

/// <summary>Which rules colour a previewed file.</summary>
public enum SyntaxLanguage
{
    Plain,
    Log,
    Json,
    Xml,
    /// <summary>C-like: <c>//</c> comments, quoted strings, numbers.</summary>
    CLike,
    /// <summary>Scripts and config: <c>#</c> comments, quoted strings, numbers.</summary>
    Hash,
}

/// <summary>
/// Light, per-line colouring for the text preview: log levels and timestamps, JSON keys and strings, XML tags,
/// comments, strings and numbers. Deliberately not a parser. Each line is coloured on its own, with no state
/// carried between lines, so a live tail can colour each new line as it arrives and a block comment spanning
/// lines is simply left plain. Pure, so every rule is unit-tested; the Kotlin twin follows the same rules.
/// </summary>
public static class SyntaxTokenizer
{
    /// <summary>The rules for <paramref name="fileName"/>, by extension.</summary>
    public static SyntaxLanguage LanguageFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".log" or ".txt" => SyntaxLanguage.Log,
            ".json" or ".jsonc" => SyntaxLanguage.Json,
            ".xml" or ".axaml" or ".xaml" or ".html" or ".htm" or ".csproj" or ".props" or ".targets" or ".resx" => SyntaxLanguage.Xml,
            ".cs" or ".kt" or ".kts" or ".java" or ".gradle" or ".js" or ".ts" or ".tsx" or ".jsx" or ".mjs" or ".c" or ".h"
                or ".cpp" or ".hpp" or ".go" or ".rs" or ".css" => SyntaxLanguage.CLike,
            ".py" or ".rb" or ".sh" or ".bash" or ".zsh" or ".ps1" or ".psm1" or ".psd1" or ".yaml" or ".yml" or ".toml"
                or ".ini" or ".cfg" or ".conf" or ".properties" or ".env" or ".gitignore" or ".editorconfig" => SyntaxLanguage.Hash,
            _ => SyntaxLanguage.Plain,
        };

    /// <summary>The coloured stretches of <paramref name="line"/>, in order and non-overlapping. Plain gaps are omitted.</summary>
    public static IReadOnlyList<SyntaxSpan> Tokenize(string line, SyntaxLanguage language) => language switch
    {
        SyntaxLanguage.Log => TokenizeLog(line),
        SyntaxLanguage.Json => TokenizeCode(line, lineComment: null, jsonKeys: true),
        SyntaxLanguage.Xml => TokenizeXml(line),
        SyntaxLanguage.CLike => TokenizeCode(line, lineComment: "//", jsonKeys: false),
        SyntaxLanguage.Hash => TokenizeCode(line, lineComment: "#", jsonKeys: false),
        _ => [],
    };

    // Upper case as most loggers write them, plus the four-letter lower-case forms the .NET console logger
    // (and so RemEx's own logs) writes: "fail:", "crit:", "warn:", "dbug:", "trce:". Whole words only, so
    // "NoErrors" or "failover" never colour a line.
    private static readonly string[] ErrorWords = ["ERROR", "ERR", "FATAL", "CRITICAL", "CRIT", "FAIL", "FAILED", "EXCEPTION", "fail", "crit"];
    private static readonly string[] WarningWords = ["WARN", "WARNING", "WRN", "warn"];
    private static readonly string[] DebugWords = ["DEBUG", "DBG", "TRACE", "TRC", "VERBOSE", "VRB", "dbug", "trce"];

    private static IReadOnlyList<SyntaxSpan> TokenizeLog(string line)
    {
        var spans = new List<SyntaxSpan>();
        var timestampEnd = LeadingTimestampLength(line);
        if (timestampEnd > 0)
            spans.Add(new SyntaxSpan(0, timestampEnd, SyntaxKind.Timestamp));

        var level = LevelOf(line);
        if (level != SyntaxKind.Plain && timestampEnd < line.Length)
            spans.Add(new SyntaxSpan(timestampEnd, line.Length - timestampEnd, level));
        return spans;
    }

    /// <summary>The level a log line is at, judged by a whole level word (ERROR, WARN, DEBUG…), case-sensitive.</summary>
    internal static SyntaxKind LevelOf(string line)
    {
        foreach (var word in Words(line))
        {
            if (Array.IndexOf(ErrorWords, word) >= 0) return SyntaxKind.LogError;
            if (Array.IndexOf(WarningWords, word) >= 0) return SyntaxKind.LogWarning;
            if (Array.IndexOf(DebugWords, word) >= 0) return SyntaxKind.LogDebug;
        }
        return SyntaxKind.Plain;
    }

    private static IEnumerable<string> Words(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && !char.IsAsciiLetter(line[i])) i++;
            var start = i;
            while (i < line.Length && char.IsAsciiLetter(line[i])) i++;
            if (i > start) yield return line[start..i];
        }
    }

    /// <summary>
    /// The length of a leading timestamp: an optional '[', then digits and the separators of a date or time
    /// (<c>2026-10-08 14:03:22.123</c>, <c>14:03:22</c>, <c>2026-10-08T14:03:22Z</c>), at least one ':' or '-',
    /// and an optional ']'. Zero when the line does not start with one.
    /// </summary>
    internal static int LeadingTimestampLength(string line)
    {
        var i = 0;
        if (i < line.Length && line[i] == '[') i++;
        var digitsStart = i;
        var sawSeparator = false;
        var sawDigit = false;
        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsAsciiDigit(c)) { sawDigit = true; i++; }
            else if (c is ':' or '-' or '/') { sawSeparator = true; i++; }
            else if (c is '.' or ',' or 'T' or 'Z' or '+' && sawDigit) i++;
            else if (c == ' ' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1]) && sawSeparator) i++;
            else break;
        }
        if (!sawDigit || !sawSeparator || i - digitsStart < 5) return 0;
        if (i < line.Length && line[i] == ']') i++;
        return i;
    }

    private static IReadOnlyList<SyntaxSpan> TokenizeCode(string line, string? lineComment, bool jsonKeys)
    {
        var spans = new List<SyntaxSpan>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (lineComment is not null && string.CompareOrdinal(line, i, lineComment, 0, lineComment.Length) == 0)
            {
                spans.Add(new SyntaxSpan(i, line.Length - i, SyntaxKind.Comment));
                break;
            }
            if (c is '"' or '\'' || (c == '`' && !jsonKeys))
            {
                var end = StringEnd(line, i);
                var kind = jsonKeys && c == '"' && NextNonSpace(line, end) == ':' ? SyntaxKind.Key : SyntaxKind.String;
                spans.Add(new SyntaxSpan(i, end - i, kind));
                i = end;
                continue;
            }
            if (char.IsAsciiDigit(c) && (i == 0 || !IsWordChar(line[i - 1])))
            {
                var end = i;
                while (end < line.Length && (char.IsAsciiHexDigit(line[end]) || line[end] is '.' or 'x' or 'X' or '_')) end++;
                if (end == line.Length || !IsWordChar(line[end]))
                {
                    spans.Add(new SyntaxSpan(i, end - i, SyntaxKind.Number));
                    i = end;
                    continue;
                }
            }
            if (jsonKeys && char.IsAsciiLetter(c) && (i == 0 || !IsWordChar(line[i - 1])))
            {
                var end = i;
                while (end < line.Length && char.IsAsciiLetter(line[end])) end++;
                if (line.AsSpan(i, end - i) is "true" or "false" or "null")
                    spans.Add(new SyntaxSpan(i, end - i, SyntaxKind.Keyword));
                i = end;
                continue;
            }
            i++;
        }
        return spans;
    }

    private static IReadOnlyList<SyntaxSpan> TokenizeXml(string line)
    {
        var spans = new List<SyntaxSpan>();
        var i = 0;
        while (i < line.Length)
        {
            if (string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
            {
                var close = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var end = close < 0 ? line.Length : close + 3;
                spans.Add(new SyntaxSpan(i, end - i, SyntaxKind.Comment));
                i = end;
                continue;
            }
            if (line[i] == '<')
            {
                var start = i + 1;
                if (start < line.Length && line[start] is '/' or '?' or '!') start++;
                var end = start;
                while (end < line.Length && (IsWordChar(line[end]) || line[end] is ':' or '-' or '.')) end++;
                if (end > start)
                    spans.Add(new SyntaxSpan(start, end - start, SyntaxKind.Key));
                i = end;
                continue;
            }
            if (line[i] is '"' or '\'')
            {
                var end = StringEnd(line, i);
                spans.Add(new SyntaxSpan(i, end - i, SyntaxKind.String));
                i = end;
                continue;
            }
            i++;
        }
        return spans;
    }

    /// <summary>One past the closing quote of the string starting at <paramref name="open"/>, honouring backslash escapes; the line's end if unclosed.</summary>
    private static int StringEnd(string line, int open)
    {
        var quote = line[open];
        var i = open + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\') { i += 2; continue; }
            if (line[i] == quote) return i + 1;
            i++;
        }
        return line.Length;
    }

    private static char NextNonSpace(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
            if (!char.IsWhiteSpace(line[i])) return line[i];
        return '\0';
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
