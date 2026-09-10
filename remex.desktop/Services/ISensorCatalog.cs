using System.Diagnostics.CodeAnalysis;

namespace Remex.Desktop.Services;

/// <summary>
/// Every sensor the canvas has seen this session, independent of whether it currently has a
/// configured alert. Backs the Settings "Sensor alerts" card and the copy-to picker (RemEx-8wpvr.5),
/// neither of which may reference <c>CanvasDashboardViewModel</c> directly.
/// </summary>
public interface ISensorCatalog
{
    /// <summary>Every sensor the canvas has seen this session, distinct by name.</summary>
    IReadOnlyList<SensorInfo> Known { get; }

    /// <summary>Looks up a known sensor by name, case-insensitively.</summary>
    bool TryResolve(string name, [MaybeNullWhen(false)] out SensorInfo info);
}

/// <summary>
/// A sensor known to the canvas: its raw name (the key used everywhere else — alerts, cards,
/// pins), a display name (the user's custom title when set), its last-seen unit, and whether it is
/// currently reporting.
/// </summary>
public record SensorInfo(string Name, string DisplayName, string? Unit, bool IsConnected);
