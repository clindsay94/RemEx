using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// What the CLIENT can do, told to the host (RemEx-220r).
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="HostCapabilities"/>, which has always travelled the other way. It exists
/// because the host now has decisions that depend on what the phone can render rather than only on
/// what the host can do — the first being whether a file-consent question can be asked on the phone
/// that asked it, instead of on a PC monitor in another room.
/// </para>
/// <para>
/// **ADDITIVE AND OPTIONAL, SO NO <c>protocolVersion</c> BUMP.** Every field must be safe to omit,
/// and omission must mean the OLD behaviour — a client that never sends this is a client that cannot
/// do any of it, which is exactly what an older build is. A field whose absence meant "yes" would
/// turn every existing phone into a liar the moment the host started reading it.
/// </para>
/// </remarks>
public sealed record ClientCapabilities
{
    /// <summary>
    /// Whether this client can render a file-consent prompt on its own screen.
    /// </summary>
    /// <remarks>
    /// False, and absent, both mean the PC dialog — the compatibility path. A phone built before the
    /// consent sheet cannot show the prompt, so routing one to it would make every gated transfer
    /// fail on an app the user has no reason to suspect, caused entirely by updating the PC.
    /// </remarks>
    [JsonPropertyName("supportsConsentPrompt")]
    public bool SupportsConsentPrompt { get; init; }
}
