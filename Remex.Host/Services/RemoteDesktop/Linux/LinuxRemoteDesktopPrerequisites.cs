using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Remex.Host.Services.RemoteDesktop.Linux;

/// <summary>
/// Evaluates all Linux remote desktop prerequisites and produces a
/// <see cref="LinuxPrerequisiteReport"/> and an optional
/// <see cref="LinuxPrerequisiteRepairPlan"/>.
///
/// All detection is non-blocking when possible; blocking probes run on the thread pool.
/// This class is stateless — callers may cache the report themselves.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxRemoteDesktopPrerequisites
{
    private readonly ILogger<LinuxRemoteDesktopPrerequisites> _logger;

    // Known SONAME candidate lists — tried in order via NativeLibrary.TryLoad.
    private static readonly string[] PipeWireLibCandidates =
        ["libpipewire-0.3.so.0", "libpipewire-0.3.so"];
    private static readonly string[] LibeiCandidates =
        ["libei-1.0.so.0", "libei.so"];
    private static readonly string[] LibevdevCandidates =
        ["libevdev.so.2", "libevdev.so"];

    public LinuxRemoteDesktopPrerequisites(ILogger<LinuxRemoteDesktopPrerequisites>? logger = null)
    {
        _logger = logger ?? NullLogger<LinuxRemoteDesktopPrerequisites>.Instance;
    }

    /// <summary>
    /// Performs a full prerequisite evaluation synchronously.
    /// For use at startup where async is inconvenient.
    /// </summary>
    public LinuxPrerequisiteReport Evaluate()
        => EvaluateAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Performs a full prerequisite evaluation asynchronously.
    /// </summary>
    public async Task<LinuxPrerequisiteReport> EvaluateAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Starting Linux remote desktop prerequisite evaluation.");
        var issues = new List<string>();

        // ── 1. Session environment ──────────────────────────────────────────
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? string.Empty;
        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var x11Display = Environment.GetEnvironmentVariable("DISPLAY");

        var isWayland = !string.IsNullOrWhiteSpace(waylandDisplay) ||
                        sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase);
        var isX11 = !string.IsNullOrWhiteSpace(x11Display) ||
                    sessionType.Equals("x11", StringComparison.OrdinalIgnoreCase);

        // ── 2. Session bus ─────────────────────────────────────────────────
        var sessionBus = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        if (!sessionBus)
            issues.Add("D-Bus session bus is not available ($DBUS_SESSION_BUS_ADDRESS is unset). The host must run inside a graphical session.");

        // ── 3. Portal availability (probe via D-Bus introspection) ─────────
        bool portalRd = false, portalSc = false;
        string? portalReason = null;
        if (sessionBus)
        {
            (portalRd, portalSc, portalReason) = await ProbePortalAsync(ct);
            if (!portalRd)
                issues.Add(portalReason ?? "xdg-desktop-portal RemoteDesktop interface is unavailable.");
        }

        // ── 4. PipeWire runtime ─────────────────────────────────────────────
        var pwRunning = await IsUserServiceActiveAsync("pipewire", ct);
        var wpRunning = await IsUserServiceActiveAsync("wireplumber", ct);
        var pwLib = TryLoadLibrary(PipeWireLibCandidates);
        string? pwReason = null;
        if (!pwRunning)
        {
            issues.Add("PipeWire user service is not running. Start it with: systemctl --user start pipewire");
            pwReason = "PipeWire service not running";
        }
        if (!wpRunning)
            issues.Add("WirePlumber session manager is not running. Start it with: systemctl --user start wireplumber");
        if (pwLib is null)
            issues.Add("libpipewire-0.3 shared library not found. Install pipewire development libraries.");

        // ── 5. EIS / libei ─────────────────────────────────────────────────
        var eisLib = TryLoadLibrary(LibeiCandidates);
        if (eisLib is null)
            issues.Add("libei-1.0 not found. Install libei for Wayland-native input injection.");

        // ── 6. libevdev ─────────────────────────────────────────────────────
        var evdevLib = TryLoadLibrary(LibevdevCandidates);
        if (evdevLib is null)
            issues.Add("libevdev not found. Install libevdev for uinput virtual device support.");

        // ── 7. uinput ─────────────────────────────────────────────────────
        const string uinputPath = "/dev/uinput";
        var uinputExists = File.Exists(uinputPath);
        bool uinputWritable = false;
        string? uinputReason = null;
        if (uinputExists)
        {
            try
            {
                using var fs = new FileStream(uinputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                uinputWritable = true;
            }
            catch (UnauthorizedAccessException)
            {
                uinputReason = "Current user does not have write access to /dev/uinput. Add user to the 'input' group or install a udev rule.";
                issues.Add(uinputReason);
            }
            catch (Exception ex)
            {
                uinputReason = $"/dev/uinput is not writable: {ex.Message}";
                issues.Add(uinputReason);
            }
        }
        else
        {
            uinputReason = "/dev/uinput does not exist. Load the uinput kernel module: sudo modprobe uinput";
            issues.Add(uinputReason);
        }

        // ── 8. Encoder stack ───────────────────────────────────────────────
        var ffmpegPath = FindExecutable("ffmpeg");
        var nvencAvailable = CheckNvencAvailable();
        var vaapiAvailable = File.Exists("/dev/dri/renderD128");  // heuristic; refined at encode time

        if (ffmpegPath is null)
            issues.Add("ffmpeg not found. Install ffmpeg for MJPEG encode fallback.");

        // ── 9. Determine selected tier ─────────────────────────────────────
        var (tier, degradedReason) = DetermineTier(
            isWayland, isX11, sessionBus, portalRd, pwRunning, pwLib is not null,
            eisLib is not null, uinputWritable);

        if (tier == LinuxRemoteDesktopTier.Unsupported)
            issues.Add(degradedReason ?? "Remote desktop is not supported in this environment.");

        var report = new LinuxPrerequisiteReport
        {
            SessionType = string.IsNullOrWhiteSpace(sessionType) ? null : sessionType,
            WaylandDisplay = waylandDisplay,
            X11Display = x11Display,
            IsWaylandSession = isWayland,
            IsX11Session = isX11,
            SessionBusAvailable = sessionBus,
            PortalRemoteDesktopAvailable = portalRd,
            PortalScreenCastAvailable = portalSc,
            PortalUnavailableReason = portalReason,
            PipeWireRunning = pwRunning,
            WirePlumberRunning = wpRunning,
            PipeWireLibraryAvailable = pwLib is not null,
            PipeWireLibraryPath = pwLib,
            PipeWireUnavailableReason = pwReason,
            LibeiAvailable = eisLib is not null,
            LibeiLibraryPath = eisLib,
            LibevdevAvailable = evdevLib is not null,
            LibevdevLibraryPath = evdevLib,
            UinputNodeExists = uinputExists,
            UinputWritable = uinputWritable,
            UinputUnavailableReason = uinputReason,
            FfmpegAvailable = ffmpegPath is not null,
            FfmpegPath = ffmpegPath,
            NvencAvailable = nvencAvailable,
            VaapiAvailable = vaapiAvailable,
            SelectedTier = tier,
            DegradedReason = degradedReason,
            Issues = issues.AsReadOnly(),
            CollectedAt = DateTimeOffset.UtcNow,
        };

        _logger.LogInformation(
            "Linux prerequisites: tier={Tier}, portalRD={PortalRD}, pipewire={PW}, libei={EIS}, uinput={Uinput}, issues={IssueCount}",
            tier, portalRd, pwRunning, eisLib is not null, uinputWritable, issues.Count);

        return report;
    }

    /// <summary>
    /// Builds an ordered repair plan from the given report tailored to Arch-family hosts.
    /// On non-Arch distros, only service restarts and udev rules are automated.
    /// </summary>
    public LinuxPrerequisiteRepairPlan BuildRepairPlan(LinuxPrerequisiteReport report)
    {
        var isArch = IsArchFamily();
        var actions = new List<LinuxRepairAction>();

        // Service restarts always come before package installs
        if (!report.PipeWireRunning)
            actions.Add(new LinuxRepairAction(
                LinuxRepairActionKind.RestartUserService,
                "Start the PipeWire user service",
                "systemctl --user start pipewire"));

        if (!report.WirePlumberRunning)
            actions.Add(new LinuxRepairAction(
                LinuxRepairActionKind.RestartUserService,
                "Start the WirePlumber session manager",
                "systemctl --user start wireplumber"));

        // Package installs — Arch only
        if (isArch)
        {
            if (!report.PipeWireLibraryAvailable)
                actions.Add(new LinuxRepairAction(
                    LinuxRepairActionKind.InstallPackage,
                    "Install pipewire",
                    "sudo pacman -S --noconfirm pipewire", RequiresElevation: true));

            if (!report.LibeiAvailable)
                actions.Add(new LinuxRepairAction(
                    LinuxRepairActionKind.InstallPackage,
                    "Install libei",
                    "sudo pacman -S --noconfirm libei", RequiresElevation: true));

            if (!report.LibevdevAvailable)
                actions.Add(new LinuxRepairAction(
                    LinuxRepairActionKind.InstallPackage,
                    "Install libevdev",
                    "sudo pacman -S --noconfirm libevdev", RequiresElevation: true));

            if (!report.FfmpegAvailable)
                actions.Add(new LinuxRepairAction(
                    LinuxRepairActionKind.InstallPackage,
                    "Install ffmpeg",
                    "sudo pacman -S --noconfirm ffmpeg", RequiresElevation: true));

            if (!report.PortalRemoteDesktopAvailable || !report.PortalScreenCastAvailable)
            {
                // Distinguish KDE vs generic
                var isKde = ContainsToken(
                    Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "KDE");
                var pkg = isKde ? "xdg-desktop-portal-kde" : "xdg-desktop-portal-gnome";
                actions.Add(new LinuxRepairAction(
                    LinuxRepairActionKind.InstallPackage,
                    $"Install the portal backend ({pkg})",
                    $"sudo pacman -S --noconfirm xdg-desktop-portal {pkg}", RequiresElevation: true));
            }
        }

        // uinput access
        if (report.UinputNodeExists && !report.UinputWritable)
        {
            actions.Add(new LinuxRepairAction(
                LinuxRepairActionKind.AddUserToInputGroup,
                "Add current user to the 'input' group for /dev/uinput access (requires re-login)",
                $"sudo usermod -aG input {Environment.UserName}", RequiresElevation: true));

            actions.Add(new LinuxRepairAction(
                LinuxRepairActionKind.AddUinputUdevRule,
                "Install udev rule granting 'input' group write access to /dev/uinput",
                "echo 'KERNEL==\"uinput\", GROUP=\"input\", MODE=\"0660\"' | sudo tee /etc/udev/rules.d/99-uinput-remex.rules && sudo udevadm control --reload-rules",
                RequiresElevation: true));
        }

        if (!report.UinputNodeExists)
            actions.Add(new LinuxRepairAction(
                LinuxRepairActionKind.Manual,
                "Load the uinput kernel module. Add 'uinput' to /etc/modules-load.d/ for persistence.",
                "sudo modprobe uinput", RequiresElevation: true));

        return new LinuxPrerequisiteRepairPlan
        {
            Actions = actions.AsReadOnly(),
            HasAutomatedRepair = isArch && actions.Count > 0,
        };
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static (LinuxRemoteDesktopTier Tier, string? Reason) DetermineTier(
        bool isWayland, bool isX11, bool sessionBus, bool portalRd,
        bool pwRunning, bool pwLib, bool libei, bool uinputWritable)
    {
        if (!isWayland && !isX11)
            return (LinuxRemoteDesktopTier.Unsupported,
                "No display server detected. Run the host from inside a graphical session.");

        if (!sessionBus)
            return (LinuxRemoteDesktopTier.Unsupported,
                "D-Bus session bus is unavailable. Start the host inside a user session.");

        if (!isWayland)
        {
            // X11-only path
            return (LinuxRemoteDesktopTier.X11Degraded,
                "Running in X11 degraded mode — PipeWire portal requires a Wayland session.");
        }

        if (!portalRd || !pwRunning || !pwLib)
        {
            if (isX11)
                return (LinuxRemoteDesktopTier.X11Degraded,
                    "Portal or PipeWire unavailable; falling back to X11 shell-tool capture.");
            return (LinuxRemoteDesktopTier.Unsupported,
                "Portal and PipeWire unavailable in Wayland session — cannot stream.");
        }

        // Portal + PipeWire available
        if (libei && uinputWritable)
            return (LinuxRemoteDesktopTier.WaylandNative, null);

        return (LinuxRemoteDesktopTier.PortalNoPen,
            uinputWritable
                ? "libei unavailable — using portal Notify* input methods instead of EIS."
                : "uinput not writable — full stylus pen mode unavailable.");
    }

    private static async Task<(bool RemoteDesktop, bool ScreenCast, string? Reason)> ProbePortalAsync(
        CancellationToken ct)
    {
        // Use busctl to check if portal interfaces are exported — lightweight and always available.
        try
        {
            var result = await RunProcessOutputAsync(
                "busctl",
                "--user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop",
                ct);

            if (result is null)
                return (false, false, "busctl introspect timed out or failed");

            var rd = result.Contains("org.freedesktop.portal.RemoteDesktop");
            var sc = result.Contains("org.freedesktop.portal.ScreenCast");

            if (!rd)
                return (false, sc, "org.freedesktop.portal.RemoteDesktop not found — install xdg-desktop-portal and a backend (e.g. xdg-desktop-portal-kde).");

            return (rd, sc, null);
        }
        catch (Exception ex)
        {
            return (false, false, $"Portal probe failed: {ex.Message}");
        }
    }

    private static async Task<bool> IsUserServiceActiveAsync(string serviceName, CancellationToken ct)
    {
        try
        {
            var result = await RunProcessOutputAsync(
                "systemctl",
                $"--user is-active {serviceName}",
                ct);
            return result?.Trim().Equals("active", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryLoadLibrary(string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (NativeLibrary.TryLoad(candidate, out var handle) && handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                    return candidate;
                }
            }
            catch { /* continue */ }
        }
        return null;
    }

    private static bool CheckNvencAvailable()
    {
        // Heuristic: nvidia-smi must exist and /dev/nvidia0 must be present
        return FindExecutable("nvidia-smi") is not null && File.Exists("/dev/nvidia0");
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(path) ? path : null;
        }
        catch { return null; }
    }

    private static async Task<string?> RunProcessOutputAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch { return null; }
    }

    private static bool IsArchFamily()
    {
        try
        {
            if (File.Exists("/etc/arch-release")) return true;
            if (File.Exists("/etc/os-release"))
            {
                var content = File.ReadAllText("/etc/os-release");
                return content.Contains("arch", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("cachyos", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("endeavour", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("manjaro", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static bool ContainsToken(string? value, string token)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
