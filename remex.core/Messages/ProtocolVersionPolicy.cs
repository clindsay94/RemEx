namespace Remex.Core.Messages;

/// <summary>
/// The single source of truth for which <see cref="RemexMessage.ProtocolVersion"/> values the host
/// will accept. Both the <c>/ws</c> control channel and the <c>/ws/desktop</c> stream gate inbound
/// connections through <see cref="IsSupported"/> so the rule can never drift between the two paths.
/// </summary>
/// <remarks>
/// Forward-compatibility policy: <b>accept-range</b>, not exact match. The host accepts the current
/// minimum (<see cref="Minimum"/>) and any higher version. The wire envelope is additive — newer
/// clients only add optional fields, which a current host safely ignores — so refusing a client
/// merely because it advertises a <i>newer</i> protocol would needlessly brick forward-compatible
/// peers. A breaking wire change must raise <see cref="Minimum"/> AND ship a coordinated host +
/// Android release (see CLAUDE.md "Protocol Versioning").
/// <para>
/// This type lives in <c>Remex.Core</c> and compiles into the NativeAOT <c>libRemexCore.so</c>; it
/// is deliberately pure integer logic — no reflection, no serialization — to stay AOT-safe.
/// </para>
/// </remarks>
public static class ProtocolVersionPolicy
{
    /// <summary>
    /// The lowest protocol version this host build understands. Messages below this are legacy or
    /// malformed (the envelope defaults <see cref="RemexMessage.ProtocolVersion"/> to 2, so a 0/1
    /// value indicates a pre-2.0 or hand-crafted client).
    /// </summary>
    public const int Minimum = 2;

    /// <summary>
    /// The protocol version this build implements in full. Raised to 3 by the RemEx 2.1 file-sharing
    /// overhaul (binary <c>/ws/files</c> channel, resume, consent). <see cref="Minimum"/> deliberately
    /// stays 2 so v2 peers keep working via the legacy base64 file path for one release.
    /// </summary>
    public const int Current = 3;

    /// <summary>
    /// The first protocol version that supports the binary file-transfer channel (<c>/ws/files</c>),
    /// offset-based resume, and the v3 negotiation messages. This is a fixed feature-introduction
    /// version, NOT <see cref="Current"/> — a later <see cref="Current"/> bump must not change the
    /// answer for v3 peers.
    /// </summary>
    public const int BinaryFileTransferMinimum = 3;

    /// <summary>
    /// Returns true when a peer advertising <paramref name="protocolVersion"/> is allowed to connect.
    /// Accept-range semantics: the current minimum and anything newer are accepted; anything older
    /// (including the 0/1 legacy/malformed range) is rejected.
    /// </summary>
    public static bool IsSupported(int protocolVersion) => protocolVersion >= Minimum;

    /// <summary>
    /// Returns true when a peer advertising <paramref name="protocolVersion"/> supports the v3 binary
    /// file-transfer channel and resume. v2 peers return false and MUST be served the legacy base64
    /// path only; they must never be sent v3-only messages or admitted to <c>/ws/files</c>.
    /// </summary>
    public static bool SupportsBinaryFileTransfer(int protocolVersion) => protocolVersion >= BinaryFileTransferMinimum;
}
