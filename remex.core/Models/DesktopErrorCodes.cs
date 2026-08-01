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
    /// The desktop socket never answered. Arg = <c>host:port</c>. (RemEx-nl0z)
    /// </summary>
    /// <remarks>
    /// The first code here that describes a failure the HOST never saw — every other one is composed
    /// on the host and travels over an established stream. This one is raised client-side, which is
    /// the point: before RemEx-nl0z a connect that timed out reached the user not at all. It was
    /// thrown out of <c>ConnectAsync</c>, caught by the ordered work queue that drives the native
    /// exports, and written to logcat, so the phone showed a stalled screen and no explanation.
    /// </remarks>
    public const string ConnectTimeout = "connect_timeout";

    /// <summary>
    /// The socket opened but the host stopped responding during the proof-of-possession exchange
    /// that immediately follows it. Arg = <c>host:port</c>. (RemEx-nl0z)
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="ConnectTimeout"/> even though both are "the PC went
    /// quiet", because the advice differs: reaching this point proves the address, the network path
    /// and the pinned certificate are all good, so "check the PC is awake and on this network" — the
    /// right next step for a connect timeout — is misleading here.
    /// </remarks>
    public const string HandshakeTimeout = "handshake_timeout";

    /// <summary>
    /// Builds the wire string <c>"code␟arg␟englishFallback"</c>. <paramref name="arg"/> is an
    /// optional machine-readable parameter (e.g. a frame count) the client can substitute into its
    /// localized template; pass null when there is none.
    /// </summary>
    public static string Format(string code, string englishFallback, string? arg = null) =>
        string.Concat(code, Delimiter.ToString(), arg ?? string.Empty, Delimiter.ToString(), englishFallback);
}
