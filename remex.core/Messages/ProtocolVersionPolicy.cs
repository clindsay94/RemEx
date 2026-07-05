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
    /// Returns true when a peer advertising <paramref name="protocolVersion"/> is allowed to connect.
    /// Accept-range semantics: the current minimum and anything newer are accepted; anything older
    /// (including the 0/1 legacy/malformed range) is rejected.
    /// </summary>
    public static bool IsSupported(int protocolVersion) => protocolVersion >= Minimum;
}
