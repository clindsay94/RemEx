namespace Remex.Core.Models;

/// <summary>
/// Stable, locale-independent codes for "end process" failures, so each client can render its own
/// localized message instead of the host's English text.
/// </summary>
/// <remarks>
/// The host authors these messages, but it does not know the client's language — and the two clients
/// do not even share a resource file (the PC has <c>.resx</c>, the phone has <c>strings.xml</c>). So
/// the host sends a code and each client owns the wording, which is the same shape
/// <see cref="DesktopErrorCodes"/> already uses for remote-desktop errors (RemEx-728). This mirrors
/// that deliberately rather than inventing a second convention. (RemEx-r37a.)
/// <para>
/// The code travels inside the EXISTING command-response message field using <see cref="Format"/>:
/// <c>"code␟arg␟englishFallback"</c> (␟ = ASCII unit separator). Nothing is added to the wire
/// envelope and no <c>protocolVersion</c> bump is involved. It is backward-compatible in both
/// directions by construction: a client that does not parse codes shows the trailing English
/// fallback, which is exactly the text it would have shown before, and an older host sends untagged
/// text that a new client passes through unchanged.
/// </para>
/// </remarks>
public static class ProcessKillErrorCodes
{
    /// <summary>Delimiter between code, optional argument, and human-readable fallback text.</summary>
    public const char Delimiter = '\u001F';

    /// <summary>The host lacks the rights to end that process; it must be retried elevated.</summary>
    public const string AccessDenied = "kill_access_denied";

    /// <summary>
    /// The process is already gone — either never found or exited between listing and killing.
    /// </summary>
    /// <remarks>
    /// One code for both because the user's next step is identical: the list is stale, refresh it.
    /// Splitting them would buy a distinction no reader can act on differently.
    /// </remarks>
    public const string NotRunning = "kill_not_running";

    /// <summary>
    /// The PID is now owned by a different program, so nothing was ended. Arg = the name that owns
    /// it now.
    /// </summary>
    public const string IdentityMismatch = "kill_identity_mismatch";

    /// <summary>The kill failed for a reason with no more specific code.</summary>
    public const string Failed = "kill_failed";

    /// <summary>
    /// Builds the wire string <c>"code␟arg␟englishFallback"</c>.
    /// </summary>
    /// <param name="englishFallback">
    /// What a client that cannot parse codes will show. This is not dead weight — it is the entire
    /// backward-compatibility story, so it must stay a complete, useful sentence rather than a stub.
    /// </param>
    /// <param name="arg">
    /// An optional machine-readable value the client substitutes into its localized template, such
    /// as the name now holding the PID. Pass null when the template takes none.
    /// </param>
    public static string Format(string code, string englishFallback, string? arg = null) =>
        string.Concat(code, Delimiter.ToString(), arg ?? string.Empty, Delimiter.ToString(), englishFallback);
}
