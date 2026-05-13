using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Remex.Host.Services.RemoteDesktop.Linux;

/// <summary>
/// Enumeration of Linux remote desktop backend tiers, ordered by capability.
/// </summary>
public enum LinuxRemoteDesktopTier
{
    /// <summary>Host cannot provide remote desktop at all in the current environment.</summary>
    Unsupported = 0,

    /// <summary>X11 degraded mode — shell-tool capture and xdotool input only.</summary>
    X11Degraded = 1,

    /// <summary>Portal capture without pen mode — PipeWire capture but uinput unavailable.</summary>
    PortalNoPen = 2,

    /// <summary>Full Wayland-native mode — PipeWire capture, EIS/libei input, uinput pen support.</summary>
    WaylandNative = 3,
}

/// <summary>
/// The result of evaluating all Linux remote desktop prerequisites for this host session.
/// Immutable snapshot captured at startup or on explicit refresh.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record LinuxPrerequisiteReport
{
    // ── Session environment ───────────────────────────────────────────────
    public string? SessionType { get; init; }
    public string? WaylandDisplay { get; init; }
    public string? X11Display { get; init; }
    public bool IsWaylandSession { get; init; }
    public bool IsX11Session { get; init; }

    // ── D-Bus and portal ─────────────────────────────────────────────────
    public bool SessionBusAvailable { get; init; }
    public bool PortalRemoteDesktopAvailable { get; init; }
    public bool PortalScreenCastAvailable { get; init; }
    public string? PortalUnavailableReason { get; init; }

    // ── PipeWire ─────────────────────────────────────────────────────────
    public bool PipeWireRunning { get; init; }
    public bool WirePlumberRunning { get; init; }
    public bool PipeWireLibraryAvailable { get; init; }
    public string? PipeWireLibraryPath { get; init; }
    public string? PipeWireUnavailableReason { get; init; }

    // ── EIS / libei ──────────────────────────────────────────────────────
    public bool LibeiAvailable { get; init; }
    public string? LibeiLibraryPath { get; init; }

    // ── libevdev ─────────────────────────────────────────────────────────
    public bool LibevdevAvailable { get; init; }
    public string? LibevdevLibraryPath { get; init; }

    // ── uinput ───────────────────────────────────────────────────────────
    public bool UinputNodeExists { get; init; }
    public bool UinputWritable { get; init; }
    public string? UinputUnavailableReason { get; init; }

    // ── Encoder stack ────────────────────────────────────────────────────
    public bool FfmpegAvailable { get; init; }
    public string? FfmpegPath { get; init; }
    public bool NvencAvailable { get; init; }
    public bool VaapiAvailable { get; init; }

    // ── Selected tier ────────────────────────────────────────────────────
    public LinuxRemoteDesktopTier SelectedTier { get; init; }
    public string? DegradedReason { get; init; }

    // ── Issues aggregation ───────────────────────────────────────────────
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the host can offer at least degraded remote desktop (X11 or better).
    /// </summary>
    public bool CanStream => SelectedTier >= LinuxRemoteDesktopTier.X11Degraded;

    /// <summary>
    /// True when the full Wayland-native backend tier is available.
    /// </summary>
    public bool IsWaylandNative => SelectedTier == LinuxRemoteDesktopTier.WaylandNative;

    /// <summary>
    /// True when the portal + PipeWire stack is available (with or without pen mode).
    /// </summary>
    public bool HasPortalCapture => SelectedTier >= LinuxRemoteDesktopTier.PortalNoPen;

    /// <summary>Timestamp when this report was collected (UTC).</summary>
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
}
