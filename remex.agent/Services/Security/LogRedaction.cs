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
}
