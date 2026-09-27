namespace Remex.Branding;

/// <summary>
/// The words Live Handshake draws. remex.branding has no resource system, so the host supplies a
/// localized implementation (remex.desktop reads its .resx); <see cref="EnglishHandshakeText"/> is the
/// fallback for tools and tests. Implementations return text already upper-cased for their culture —
/// the splash draws its status line and node suffixes in capitals.
/// </summary>
public interface ILiveHandshakeText
{
    /// <summary>The status line for <paramref name="status"/>, upper-cased.</summary>
    string Status(HandshakeStatus status);

    /// <summary>The suffix typed after a linked peer's name ("LINKED"), upper-cased.</summary>
    string LinkedSuffix { get; }
}

/// <summary>English fallback for <see cref="ILiveHandshakeText"/> (PC wording: peers are phones).</summary>
public sealed class EnglishHandshakeText : ILiveHandshakeText
{
    public static EnglishHandshakeText Instance { get; } = new();

    public string LinkedSuffix => "LINKED";

    public string Status(HandshakeStatus status) => status.Kind switch
    {
        HandshakeStatusKind.Starting => "STARTING",
        HandshakeStatusKind.Pinging => status.Count == 1 ? "PINGING 1 PHONE" : $"PINGING {status.Count} PHONES",
        HandshakeStatusKind.SomeLinked => $"{status.Count} OF {status.Total} LINKED",
        HandshakeStatusKind.LinkedTo => $"LINKED · {status.Name}".ToUpperInvariant(),
        HandshakeStatusKind.NotAnswering => $"{status.Name} IS NOT ANSWERING".ToUpperInvariant(),
        HandshakeStatusKind.NonePaired => "NO PHONES PAIRED YET",
        HandshakeStatusKind.Listening => status.Port is { } port ? $"LISTENING ON PORT {port}" : "LISTENING",
        _ => string.Empty,
    };
}
