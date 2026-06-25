using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Remex.Agent.Services.RemoteDesktop.Linux.Portal;

/// <summary>
/// Well-known D-Bus names and paths used by xdg-desktop-portal.
/// </summary>
[SupportedOSPlatform("linux")]
public static class PortalDbusNames
{
    public const string PortalService = "org.freedesktop.portal.Desktop";
    public const string PortalPath = "/org/freedesktop/portal/desktop";
    public const string RemoteDesktopInterface = "org.freedesktop.portal.RemoteDesktop";
    public const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    public const string RequestInterface = "org.freedesktop.portal.Request";
    public const string SessionInterface = "org.freedesktop.portal.Session";
}

/// <summary>
/// Portal SourceType flags for ScreenCast.SelectSources.
/// </summary>
[Flags]
[SupportedOSPlatform("linux")]
public enum PortalSourceType : uint
{
    None = 0,
    Monitor = 1,
    Window = 2,
    VirtualOutput = 4,
}

/// <summary>
/// Portal CursorMode flags for ScreenCast.SelectSources.
/// </summary>
[Flags]
[SupportedOSPlatform("linux")]
public enum PortalCursorMode : uint
{
    None = 0,
    Hidden = 1,
    Embedded = 2,
    Metadata = 4,
}

/// <summary>
/// Portal persist_mode values for ScreenCast.SelectSources.
/// </summary>
[SupportedOSPlatform("linux")]
public enum PortalPersistMode : uint
{
    DoNotPersist = 0,
    TransientApplication = 1,
    PersistUntilRevoked = 2,
}

/// <summary>
/// Result of a portal session Start call. Contains stream node identifiers.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PortalStartResult
{
    /// <summary>PipeWire node IDs of the selected screen cast streams.</summary>
    public IReadOnlyList<uint> NodeIds { get; init; } = Array.Empty<uint>();

    /// <summary>Streams dictionary — maps node_id to stream options variant dict.</summary>
    public bool Success { get; init; }

    /// <summary>The portal session D-Bus object path (for later Close calls).</summary>
    public string? SessionHandle { get; init; }
}

/// <summary>
/// Represents the device types granted by RemoteDesktop.SelectDevices.
/// </summary>
[Flags]
[SupportedOSPlatform("linux")]
public enum PortalDeviceType : uint
{
    None = 0,
    Keyboard = 1,
    Pointer = 2,
    TouchScreen = 4,
}
