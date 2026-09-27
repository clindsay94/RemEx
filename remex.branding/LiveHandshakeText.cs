namespace Remex.Branding;

/// <summary>
/// The words Live Handshake draws. remex.branding has no resource system, so the host supplies a
/// localized implementation (remex.desktop reads its .resx); <see cref="EnglishHandshakeText"/> is the
/// fallback for tools and tests. Sentence case, as the console is a terminal readout.
/// </summary>
public interface ILiveHandshakeText
{
    /// <summary>The console text for <paramref name="line"/> (its time is ignored).</summary>
    string Line(HandshakeLine line);

    /// <summary>The suffix typed after a linked phone's name on its node ("linked").</summary>
    string LinkedSuffix { get; }
}

/// <summary>English fallback for <see cref="ILiveHandshakeText"/> (PC wording: peers are phones).</summary>
public sealed class EnglishHandshakeText : ILiveHandshakeText
{
    public static EnglishHandshakeText Instance { get; } = new();

    public string LinkedSuffix => "linked";

    public string Line(HandshakeLine line) => line.Kind switch
    {
        HandshakeLineKind.Paired => line.Count == 1 ? "1 phone paired" : $"{line.Count} phones paired",
        HandshakeLineKind.NonePaired => "No phones paired yet",
        HandshakeLineKind.Listening => line.Port is { } port ? $"Listening on port {port}" : "Listening",
        HandshakeLineKind.Linked => $"{line.Name} is linked",
        HandshakeLineKind.Opening => "Opening RemEx",
        _ => string.Empty,
    };
}
