namespace Remex.Core.Models;

/// <summary>
/// Stable, locale-independent codes for remote-desktop errors so the client can render a localized
/// message instead of the host's English text.
/// <para>
/// The code travels inside the existing <c>errorText</c> wire field using <see cref="Format"/>:
/// <c>"code␟arg␟englishFallback"</c> (␟ = ASCII unit separator). This is intentionally
/// backward-compatible — a client that does not parse codes simply renders the trailing English
/// fallback, and the native client forwards the field verbatim, so no native/JNI change is required.
/// The client uses <c>TryParse</c>'s equivalent to recover the code, an optional numeric argument
/// (e.g. a frame count), and the fallback text. (RemEx-728)
/// </para>
/// </summary>
public static class DesktopErrorCodes
{
    /// <summary>Delimiter between code, optional argument, and human-readable fallback text.</summary>
    public const char Delimiter = '\u001F';

    /// <summary>Screen capture never produced a frame (capture not working on the host).</summary>
    public const string CaptureUnavailable = "capture_unavailable";

    /// <summary>Capture stopped after N frames (session locked/disconnected). Arg = frame count.</summary>
    public const string CaptureStopped = "capture_stopped";

    /// <summary>The requested desktop/display target is unavailable.</summary>
    public const string TargetUnavailable = "target_unavailable";

    /// <summary>The client session does not support in-session display switching.</summary>
    public const string TargetSwitchUnsupported = "target_switch_unsupported";

    /// <summary>Remote desktop is unavailable in the current host runtime.</summary>
    public const string RuntimeUnavailable = "runtime_unavailable";

    /// <summary>
    /// Builds the wire string <c>"code␟arg␟englishFallback"</c>. <paramref name="arg"/> is an
    /// optional machine-readable parameter (e.g. a frame count) the client can substitute into its
    /// localized template; pass null when there is none.
    /// </summary>
    public static string Format(string code, string englishFallback, string? arg = null) =>
        string.Concat(code, Delimiter.ToString(), arg ?? string.Empty, Delimiter.ToString(), englishFallback);
}
