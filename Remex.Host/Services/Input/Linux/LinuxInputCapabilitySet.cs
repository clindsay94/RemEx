using System;
using System.Runtime.Versioning;
using Remex.Host.Services.RemoteDesktop.Linux;

namespace Remex.Host.Services.Input.Linux;

/// <summary>
/// Immutable snapshot of which input injection mechanisms are available
/// on the current Linux host. Derived from <see cref="LinuxPrerequisiteReport"/>
/// at startup (or on demand for diagnostics).
///
/// The input router uses this to select the best available backend:
///   WaylandNative + libei     → EIS sender (preferred Wayland path)
///   WaylandNative (no libei)  → portal NotifyPointerMotion/NotifyKey fallback
///   WaylandNative + uinput    → uinput tablet (pen/stylus events only)
///   X11Degraded               → xdotool / legacy shell tool
///   PortalNoPen               → portal notify (no pen/stylus)
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record LinuxInputCapabilitySet
{
    public required LinuxRemoteDesktopTier Tier { get; init; }

    // ── libei / EIS ─────────────────────────────────────────────────
    /// <summary>Whether libei is loaded and an EIS socket is available.</summary>
    public bool EisAvailable { get; init; }

    // ── Portal notify ────────────────────────────────────────────────
    /// <summary>
    /// Whether the xdg-desktop-portal RemoteDesktop session is active and
    /// NotifyPointerMotion / NotifyKeyboardKeycode can be called.
    /// </summary>
    public bool PortalNotifyAvailable { get; init; }

    // ── uinput tablet ────────────────────────────────────────────────
    /// <summary>Whether /dev/uinput is writable (pen events can be injected).</summary>
    public bool UinputTabletAvailable { get; init; }

    // ── Legacy shell tool (X11 fallback) ─────────────────────────────
    /// <summary>Path to xdotool, or null if not found.</summary>
    public string? XdotoolPath { get; init; }

    // ── Computed routing decisions ───────────────────────────────────

    public bool HasAnyInput =>
        EisAvailable || PortalNotifyAvailable || UinputTabletAvailable || XdotoolPath is not null;

    public bool CanInjectKeyboard => EisAvailable || PortalNotifyAvailable || XdotoolPath is not null;
    public bool CanInjectPointer => EisAvailable || PortalNotifyAvailable || XdotoolPath is not null;
    public bool CanInjectPen => UinputTabletAvailable;

    // ── Factory ──────────────────────────────────────────────────────

    [SupportedOSPlatform("linux")]
    public static LinuxInputCapabilitySet FromReport(LinuxPrerequisiteReport report)
    {
        return new LinuxInputCapabilitySet
        {
            Tier = report.SelectedTier,
            EisAvailable = report.LibeiAvailable,
            PortalNotifyAvailable = report.PortalRemoteDesktopAvailable,
            UinputTabletAvailable = report.UinputNodeExists && report.UinputWritable,
            XdotoolPath = report.SelectedTier == LinuxRemoteDesktopTier.X11Degraded
                                      ? FindXdotool() : null,
        };
    }

    private static string? FindXdotool()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("which", "xdotool")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(path) ? path : null;
        }
        catch { return null; }
    }
}
