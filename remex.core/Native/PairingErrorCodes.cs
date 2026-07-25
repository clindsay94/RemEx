namespace Remex.Core.Native;

/// <summary>
/// Stable machine-readable causes for the pairing native exports' <c>ERROR:</c> returns.
/// </summary>
/// <remarks>
/// <para>
/// The pairing exports in <see cref="AndroidNativeExports"/> return a string, and on failure that
/// string used to be <c>"ERROR: &lt;diagnostic&gt;"</c> — English developer prose that the Android
/// UI stripped the prefix off and showed to the user verbatim. Remex.Core is shared and NativeAOT
/// and has no access to Android string resources, so it cannot localize anything itself; the only
/// way the client can show a translated message is if it can tell the causes apart. Hence a code.
/// </para>
/// <para>
/// The wire format of a failure return is now <c>"ERROR: &lt;CODE&gt;: &lt;diagnostic&gt;"</c>.
/// The code is always uppercase ASCII with underscores, so the client can split on the first
/// <c>": "</c> after the prefix without the colons inside the diagnostic confusing it. The
/// diagnostic is deliberately kept — the client logs it, which is strictly more information than
/// the old behaviour, where it was shown to the user and logged nowhere.
/// </para>
/// <para>
/// THIS IS NOT A PROTOCOL CHANGE. These strings cross the JNI boundary in-process, not the
/// host/client WebSocket, so <c>protocolVersion</c> is untouched and no host/client version skew
/// is possible: libRemexCore.so is built from this project and packaged into the same APK as the
/// Kotlin that reads it. Adding a code here can never desynchronise a paired device.
/// </para>
/// <para>
/// Codes are additive. A client that meets an unknown code must fall back to a generic message
/// rather than showing the raw diagnostic, so new causes can be introduced here without a
/// coordinated release.
/// </para>
/// </remarks>
internal static class PairingErrorCodes
{
    /// <summary>A required argument was null or blank. Caller error; not expected to reach a user.</summary>
    internal const string ArgMissing = "ARG_MISSING";

    /// <summary>The host URL could not be parsed.</summary>
    internal const string HostUrlInvalid = "HOST_URL_INVALID";

    /// <summary>The TCP probe to the host timed out — unreachable, firewalled, or wrong address.</summary>
    internal const string TcpTimeout = "TCP_TIMEOUT";

    /// <summary>The host actively refused the TCP connection.</summary>
    internal const string TcpRefused = "TCP_REFUSED";

    /// <summary>TCP connected but the TLS handshake or WebSocket upgrade never completed.</summary>
    internal const string TlsTimeout = "TLS_TIMEOUT";

    /// <summary>The host never returned a PairingResponse.</summary>
    internal const string PairTimeout = "PAIR_TIMEOUT";

    /// <summary>The host answered but the PairingResponse payload was missing or unreadable.</summary>
    internal const string PairMalformed = "PAIR_MALFORMED";

    /// <summary>There is no active pairing session — it expired or was never started.</summary>
    internal const string NoSession = "NO_SESSION";

    /// <summary>The pairing session lost its client key state and cannot be completed.</summary>
    internal const string SessionKeyLost = "SESSION_KEY_LOST";

    /// <summary>The host did not confirm the submitted PIN in time.</summary>
    internal const string PinConfirmTimeout = "PIN_CONFIRM_TIMEOUT";

    /// <summary>The host rejected the PIN — wrong digits, or the session had already expired.</summary>
    internal const string PinRejected = "PIN_REJECTED";

    /// <summary>Fetching the PIN from the host timed out.</summary>
    internal const string PinFetchTimeout = "PIN_FETCH_TIMEOUT";

    /// <summary>The host has no PIN available to relay.</summary>
    internal const string PinUnavailable = "PIN_UNAVAILABLE";

    /// <summary>An unexpected exception. The diagnostic carries the type and message.</summary>
    internal const string Unexpected = "UNEXPECTED";
}
