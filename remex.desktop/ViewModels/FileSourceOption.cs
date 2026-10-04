namespace Remex.Desktop.ViewModels;

/// <summary>
/// One entry in the File Transfer screen's Source picker: This PC, or a paired phone (RemEx-xt0af).
/// </summary>
/// <param name="ClientId">The phone's paired client id; null for This PC. Never shown.</param>
/// <param name="DeviceName">The phone's friendly name, or null when it has none (and for This PC).</param>
/// <remarks>
/// THE LABEL IS NOT STORED HERE. "This PC" and the fallback for an unnamed phone are localized at the
/// binding site with <c>Localize</c>, so a language switch re-words the picker without rebuilding it;
/// a pre-formatted string here would freeze in whatever language it was built in.
/// </remarks>
public sealed record FileSourceOption(string? ClientId, string? DeviceName)
{
    /// <summary>This PC's own shared folders. Always the first entry.</summary>
    public static FileSourceOption ThisPc { get; } = new(null, null);

    public bool IsThisPc => ClientId is null;

    /// <summary>A phone with a name to show.</summary>
    public bool HasDeviceName => ClientId is not null && !string.IsNullOrWhiteSpace(DeviceName);

    /// <summary>A phone that never reported a name; the picker shows a localized fallback.</summary>
    public bool IsUnnamedPhone => ClientId is not null && string.IsNullOrWhiteSpace(DeviceName);

    /// <summary>Same place to read files from, even if the phone's name changed in between.</summary>
    public bool SameSourceAs(FileSourceOption other) =>
        string.Equals(ClientId, other.ClientId, StringComparison.Ordinal);
}
