using System.Text.RegularExpressions;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Central redaction helpers for values that must not appear in cleartext in the retained in-memory
/// log buffer (<see cref="Remex.Core.Logging.InMemoryLogSink"/>) or any other sink. The buffer is a
/// disclosure surface — a full paired <c>clientId</c> is not a secret but is an identifier an attacker
/// can harvest and replay against the secondary channels, so logs keep only a short, non-reversible
/// prefix that is enough to correlate events without leaking the whole value (VULN-1, RemEx-s032.1).
/// </summary>
public static class LogRedaction
{
    /// <summary>
    /// Reduces a client identifier to a short, log-safe form: the first 8 characters followed by an
    /// ellipsis. Returns a stable placeholder for null/empty input. The clientId is a 122-bit random
    /// UUID, so an 8-character prefix keeps event correlation possible while never disclosing enough
    /// to reconstruct the identifier.
    /// </summary>
    public static string RedactClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return "<empty>";
        return clientId.Length <= 8 ? clientId : clientId[..8] + "…";
    }

    // ── Free-text redaction for the phone's view of the PC log (RemEx-pp4cm.13) ──
    //
    // The retained buffer is written by every subsystem and was only ever read on the PC. Once it leaves
    // the machine, anything that identifies the user or the network, or that could be replayed, has to be
    // gone first. This is a deny-pattern pass over FREE TEXT, so it is best-effort by nature: it removes
    // the shapes known to carry secrets and identity and does not claim to find every one. The phone only
    // ever sees what this returns, and the log lines most likely to carry something sensitive (pairing,
    // clipboard, paths) already redact at their source.

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // name=value or name: value where the name says "secret". Keeps the name so the line still reads.
    private static readonly Regex SecretAssignment = new(
        @"\b(pin|token|secret|password|passwd|pwd|authorization|api[_-]?key|reconnect[_-]?secret|cookie|nonce|proof|hmac|signature)\b(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;]+)",
        Opts | RegexOptions.IgnoreCase);

    private static readonly Regex BearerToken = new(
        @"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=\-]+", Opts | RegexOptions.IgnoreCase);

    // A GUID is a client id or a session id: keep the same short prefix RedactClientId keeps.
    private static readonly Regex GuidValue = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", Opts);

    private static readonly Regex MacAddress = new(
        @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", Opts);

    private static readonly Regex Ipv4 = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b(?::\d{1,5})?", Opts);

    // Needs "::" or all eight groups, so a clock time such as 12:30:45 is not an address.
    private static readonly Regex Ipv6 = new(
        @"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,6}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?|::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4})(?:%\w+)?(?![\w:])",
        Opts);

    // C:\a\b\file.ext. A folder name may hold spaces ("Program Files") but may not END in one, which is
    // what stops "file.kdbx and /home/..." being read as one long path. The last part holds no space.
    private static readonly Regex WindowsPath = new(
        @"[A-Za-z]:[\\/](?:[^\\/:*?""<>|\r\n]*[^\\/:*?""<>|\r\n\s][\\/])*[^\\/:*?""<>|\r\n\s]*", Opts);

    private static readonly Regex UncPath = new(
        @"\\\\[^\s\\/]+[\\/](?:[^\\/:*?""<>|\r\n]*[^\\/:*?""<>|\r\n\s][\\/])*[^\\/:*?""<>|\r\n\s]*", Opts);

    // Only the roots that hold a user's files. A bare "/ws/desktop" is a route, not a path, and the log
    // is far more useful when routes stay readable.
    private static readonly Regex UnixPath = new(
        @"(?<![\w/:.])/(?:home|Users|root|mnt|media|tmp|var|etc|opt|usr|run|srv)/(?:[^/\s""'<>|]+/)*[^/\s""'<>|]*", Opts);

    private static readonly Regex EmailAddress = new(
        @"\b[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+\b", Opts);

    // Last resort: a long unbroken run of base64 or hex is a key, hash or token whatever it is called.
    private static readonly Regex LongToken = new(
        @"(?<![\w+/=\-])[A-Za-z0-9+/_\-]{32,}={0,2}(?![\w+/=\-])", Opts);

    /// <summary>
    /// Masks the identity, network and secret shapes in <paramref name="text"/> before it leaves the PC:
    /// secret assignments and bearer tokens, client ids (shortened), MAC and IP addresses, file paths
    /// (reduced to the file name), e-mail addresses and long key-like strings. Null or empty reads as empty.
    /// </summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = SecretAssignment.Replace(text, "$1$2<redacted>");
        s = BearerToken.Replace(s, "$1 <redacted>");
        s = GuidValue.Replace(s, m => RedactClientId(m.Value));
        s = MacAddress.Replace(s, "<mac>");
        s = Ipv4.Replace(s, "<ip>");
        s = Ipv6.Replace(s, "<ip>");
        s = UncPath.Replace(s, PathLeaf);
        s = WindowsPath.Replace(s, PathLeaf);
        s = UnixPath.Replace(s, PathLeaf);
        s = EmailAddress.Replace(s, "<email>");
        return LongToken.Replace(s, "<redacted>");
    }

    /// <summary>
    /// The first line of an exception message, redacted and capped, or empty. A stack trace or a wrapped
    /// inner exception can carry far more than the person asked to see, so only line one ever leaves.
    /// </summary>
    public static string RedactFirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        var first = end < 0 ? text : text[..end];
        return RedactText(first.Trim());
    }

    private static string PathLeaf(Match match)
    {
        var value = match.Value;
        var slash = value.LastIndexOfAny(['\\', '/']);
        var leaf = slash < 0 ? string.Empty : value[(slash + 1)..];
        return leaf.Length == 0 ? "<path>" : "<path>/" + leaf;
    }
}
