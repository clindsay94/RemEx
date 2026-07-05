using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

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
/// Geometry of a single portal ScreenCast stream (one monitor). Coordinates are in
/// the compositor's virtual-desktop space; <see cref="Width"/>/<see cref="Height"/>
/// are the stream's pixel dimensions. Values are 0 when the portal omitted them.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PortalStreamInfo
{
    public uint NodeId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
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

    /// <summary>
    /// Per-stream geometry (one entry per granted monitor), parsed from the portal
    /// Start Response 'streams' properties. Used to build the per-monitor display
    /// catalog and to crop the full-desktop PipeWire frame to a selected monitor.
    /// </summary>
    public IReadOnlyList<PortalStreamInfo> Streams { get; init; } = Array.Empty<PortalStreamInfo>();

    /// <summary>
    /// Portal-scoped PipeWire remote fd obtained via ScreenCast.OpenPipeWireRemote on
    /// the SAME D-Bus connection that owns the session. Owned by this result; the
    /// native bridge dup()s it. Null when the call failed (native fd fallback is used).
    /// </summary>
    public SafeFileHandle? PipeWireFd { get; init; }
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
