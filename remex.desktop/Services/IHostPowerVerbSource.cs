namespace Remex.Desktop.Services;

/// <summary>
/// The power actions this PC can actually carry out, as the embedded host probed them once at start
/// (<c>HostCapabilities.RoutinePowerVerbs</c>: logind on Linux, <c>GetPwrCapabilities</c> /
/// <c>GetFirmwareType</c> on Windows). The phone receives the same list in <c>host_info</c>, so the
/// Commands page on both apps hides the same actions (RemEx-kq10x.3).
/// </summary>
/// <remarks>
/// Defined here and implemented in <c>remex.agent</c>, the same direction as
/// <see cref="IPairedDeviceSource"/>: the agent references this project, never the reverse.
/// </remarks>
public interface IHostPowerVerbSource
{
    /// <summary>
    /// The advertised verbs (<c>Remex.Core.Routines.RoutinePowerVerbs</c> names), or null when the
    /// host did not probe them. Null means "show everything".
    /// </summary>
    IReadOnlyCollection<string>? GetPowerVerbs();
}

/// <summary>The one rule for showing a power action against <see cref="IHostPowerVerbSource"/>. Pure.</summary>
public static class HostPowerVerbs
{
    /// <summary>
    /// True when <paramref name="verb"/> is advertised, or when nothing is: hiding a button the PC
    /// might well support is worse than showing one it refuses. The phone applies the same rule
    /// (<c>CommandsLayout.isOffered</c>).
    /// </summary>
    public static bool IsOffered(IReadOnlyCollection<string>? advertised, string verb)
        => advertised is null || advertised.Contains(verb, StringComparer.Ordinal);
}
