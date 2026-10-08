namespace Remex.Core.Messages;

/// <summary>
/// Which client surface a host → client message is meant to reach (RemEx-y94aa).
/// </summary>
/// <remarks>
/// <para>
/// **"THE CLIENT" IS FOUR THINGS, AND UNTIL THIS EXISTED THE CODE CARRIED NO NOTION OF WHICH.**
/// Direction lives in prose on the <see cref="MessageTypes"/> constants; the intended recipient lived
/// nowhere. That gap is measured, not theorised: <c>launcher_sync</c> is received by the Android
/// control client AND the PC's own UI, so deleting the Android case leaves a client receiver standing
/// and <c>MessageTypeDeliveryTests</c> stays green — while the Android launcher screen silently stops
/// updating on every add, remove and sync request. That mutation was run and watched not to go red.
/// </para>
/// <para>
/// **DECLARED HERE RATHER THAN INFERRED, BECAUSE INFERENCE WOULD HAVE BEEN WRONG.** The alternative
/// considered was deducing audience from which handler sends a type. It is fragile in general and
/// false in particular for the five types both surfaces consume. The other alternative — per-receiver
/// coverage lists in the test — is an exemption list wearing a different hat, and AGENTS.md is
/// explicit that a list which can absorb a false positive will eventually absorb a real one.
/// </para>
/// <para>
/// **THE COMPLETENESS ASSERTION IS WHAT KEEPS THIS FROM BEING THAT LIST.** A type the host sends and
/// this table does not name fails the guard. There is no way to omit one, so the table cannot quietly
/// stop covering something — it can only be wrong in a way that shows. Adding a host → client type
/// means saying who it is for, which is a sentence the author of that type is uniquely able to write
/// and nobody afterwards is.
/// </para>
/// <para>
/// **THIS RECORDS INTENT, AND IT WAS SEEDED FROM BEHAVIOUR, WHICH IS A REAL RISK.** The initial
/// contents were derived by scanning who receives what today, so a receiver that is ALREADY missing
/// would have been written down as though its absence were deliberate. Every entry was read against
/// the feature it belongs to before it landed. If a future reader finds an entry that contradicts a feature,
/// the entry is the thing that is wrong.
/// </para>
/// <para>
/// Not consulted at runtime. Nothing routes by this; it is the written-down half of a rule the
/// dispatchers implement, and <c>MessageTypeDeliveryTests</c> is what holds the two together.
/// </para>
/// </remarks>
[Flags]
public enum ClientSurface
{
    /// <summary>Nothing. Never valid for a host → client type, and the guard refuses it.</summary>
    None = 0,

    /// <summary>The phone's control socket — <c>RemexNativeClient</c>, or the JNI router behind it.</summary>
    AndroidControl = 1,

    /// <summary>The pairing handshake client, which runs before there is a control socket.</summary>
    Pairing = 2,

    /// <summary>The remote-desktop stream client, on its own socket.</summary>
    DesktopStream = 4,

    /// <summary>The PC's own UI, talking to its embedded host over loopback.</summary>
    PcUi = 8,
}

/// <summary>The audience of every host → client message type.</summary>
public static class MessageAudience
{
    /// <summary>
    /// Who each host → client type is for.
    /// </summary>
    /// <remarks>
    /// Keyed by WIRE VALUE rather than by constant name, because the wire value is what a dispatcher
    /// actually matches and what a reader chasing a missing message has in front of them in a log.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, ClientSurface> HostToClient =
        new Dictionary<string, ClientSurface>(StringComparer.Ordinal)
    {
        [MessageTypes.ClipboardContent] = ClientSurface.AndroidControl,
        [MessageTypes.ClipboardPushResult] = ClientSurface.AndroidControl,
        [MessageTypes.CommandResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.DesktopCursorShape] = ClientSurface.DesktopStream,
        [MessageTypes.DesktopCursorState] = ClientSurface.DesktopStream,
        [MessageTypes.DesktopDisplayList] = ClientSurface.DesktopStream,
        [MessageTypes.DesktopError] = ClientSurface.DesktopStream,
        [MessageTypes.DesktopMeta] = ClientSurface.DesktopStream,
        [MessageTypes.DesktopStreamDescriptor] = ClientSurface.DesktopStream,
        [MessageTypes.DesktopWindowResult] = ClientSurface.DesktopStream,
        [MessageTypes.FileBrowseResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileConsentRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileHashResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileManageResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileMetadataResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FilePushOffer] = ClientSurface.AndroidControl,
        [MessageTypes.FileRootManageResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileRootsResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileSearchResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileManifestResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileThumbnailResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileReadRangeResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileTransferChunk] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileTransferComplete] = ClientSurface.AndroidControl,
        // Host -> client only since RemEx-cc30z, which made the host able to say "let go of this"
        // rather than abandoning a push in silence. Inbound long before that; the phone is the only
        // surface with sessions to release, and it receives this through the file_ prefix forward.
        [MessageTypes.FileTransferControl] = ClientSurface.AndroidControl,
        [MessageTypes.FileTransferEnd] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileTransferOffer] = ClientSurface.AndroidControl,
        [MessageTypes.FileTransferProgress] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.FileTransferReady] = ClientSurface.AndroidControl,
        [MessageTypes.FileTransferResult] = ClientSurface.AndroidControl,
        [MessageTypes.FileVolumesResponse] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        // The PC browsing a paired phone (RemEx-xt0af). PhoneFileRelay forwards these REQUESTS (the nine
        // read-only ones, plus file_manage_request since RemEx-fgmne),
        // built by the PC's own File Transfer screen, down a paired phone's existing session; the
        // phone's file host answers them as it always has. The phone only — the PC's UI is the asker
        // here, never the receiver — and they reach Kotlin through the file_ prefix forward, which is
        // exactly why naming them here matters: narrow that forward and these go red.
        [MessageTypes.FileRootsRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileBrowseRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileVolumesRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileSearchRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileManifestRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileMetadataRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileThumbnailRequest] = ClientSurface.AndroidControl,
        // Live preview and "Verify against…" on a phone's files, from the PC (2026-10-08 redesign). Both are
        // read-only and pass the same shared-folder gate on the phone as a browse does, re-checked per request.
        [MessageTypes.FileReadRangeRequest] = ClientSurface.AndroidControl,
        [MessageTypes.FileHashRequest] = ClientSurface.AndroidControl,
        // Rename / delete / move / copy / new folder on the phone, from the PC (RemEx-fgmne). Relayed only to a
        // phone whose own roots reply said the person allows it, and the phone checks that switch again.
        [MessageTypes.FileManageRequest] = ClientSurface.AndroidControl,
        // The phone only (RemEx-wqo7a.5). The PC's own UI publishes this list in-process through
        // IHomePinnedSensorsStore and the host never sends it to a loopback session. It reaches Kotlin
        // through the home_pins_ prefix forward; declaring it here is what makes that forward
        // impossible to delete in silence.
        // The phone only (RemEx-pp4cm.13). The PC's own Logs & diagnostics page reads the buffer
        // in-process, and the host refuses both requests from loopback. Both reach Kotlin through the
        // diagnostic_ prefix forward; declaring them here is what makes that forward impossible to
        // delete in silence.
        [MessageTypes.DiagnosticLogsResult] = ClientSurface.AndroidControl,
        [MessageTypes.DiagnosticSummaryResult] = ClientSurface.AndroidControl,
        [MessageTypes.HomePinsSync] = ClientSurface.AndroidControl,
        [MessageTypes.HostInfo] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.LauncherSync] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        // The phone only, for the same reason as media_state below (RemEx-vtorl): the PC's UI shares
        // a process with the artwork store and would read the bytes by reference, and
        // PingPongHandler does not run this stream for a loopback connection at all. Declared here so
        // that the router line carrying it to Kotlin cannot be removed in silence — an artwork reply
        // that is sent and never delivered looks, from the phone, exactly like a host that has no
        // cover for the track.
        [MessageTypes.MediaArtwork] = ClientSurface.AndroidControl,
        // The phone only (RemEx-xx6xf). The PC's own UI is in the same process as the sampler and
        // would read it by reference if it ever wanted it, so sending it over loopback would be the
        // round trip RemEx-ite8 removed for telemetry — and PingPongHandler does not start this
        // stream for a loopback connection at all, so declaring PcUi here would describe a message
        // that is never sent.
        [MessageTypes.MediaState] = ClientSurface.AndroidControl,
        [MessageTypes.PairingComplete] = ClientSurface.Pairing,
        [MessageTypes.PairingError] = ClientSurface.Pairing,
        [MessageTypes.PairingPinResponse] = ClientSurface.Pairing,
        [MessageTypes.PairingResponse] = ClientSurface.Pairing,
        [MessageTypes.Pong] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.ProcessListSync] = ClientSurface.AndroidControl | ClientSurface.PcUi,
        [MessageTypes.ReconnectChallenge] = ClientSurface.AndroidControl | ClientSurface.DesktopStream,
        // Only the control socket's PingPongHandler acks a proof; the desktop stream's handshake
        // authenticates inline and never sends this (RemEx-0vpw5).
        [MessageTypes.ReconnectResult] = ClientSurface.AndroidControl,
        // Routines (RemEx-pp0rt.3, spec §7.7). The phone only: it is the sole editor and the owner of
        // every routine, and the PC's own UI reads routine state in-process from the agent, never over
        // loopback. All four reach Kotlin through the routine_ prefix forward in
        // AndroidNativeExports.OnNativeMessageReceived; declaring them here is what makes that forward
        // impossible to delete in silence.
        [MessageTypes.RoutineNotify] = ClientSurface.AndroidControl,
        [MessageTypes.RoutineRunReport] = ClientSurface.AndroidControl,
        [MessageTypes.RoutineStepResult] = ClientSurface.AndroidControl,
        [MessageTypes.RoutineSyncResult] = ClientSurface.AndroidControl,
        // The PC's alert rules firing on the phone (RemEx-pp4cm.12). The phone only: the PC's own UI
        // raises its alerts in-process from the same tracker, so sending these over loopback would be a
        // second evaluator's worth of noise. Both reach Kotlin through the sensor_alert_ prefix forward;
        // declaring them here is what makes that forward impossible to delete in silence.
        [MessageTypes.SensorAlertFired] = ClientSurface.AndroidControl,
        [MessageTypes.SensorAlertRules] = ClientSurface.AndroidControl,
        [MessageTypes.Telemetry] = ClientSurface.AndroidControl | ClientSurface.PcUi,
    };
}
