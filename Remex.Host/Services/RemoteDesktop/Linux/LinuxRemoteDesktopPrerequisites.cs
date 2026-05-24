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

    /// <summary>
    /// Shell script body invoked by <see cref="LinuxRepairActionKind.RestartPortalFrontend"/>.
    /// Sources the live desktop env from a known compositor/shell process via
    /// <c>/proc/$pid/environ</c> (because the host's own env may be stale),
    /// pushes the needed variables into <c>systemd --user</c>, then restarts
    /// <c>xdg-desktop-portal.service</c> so the frontend re-reads <c>.portal</c>
    /// files with the correct <c>XDG_CURRENT_DESKTOP</c>. Passed as a single
    /// argument to <c>sh -c</c> via <see cref="LinuxRepairAction.ArgumentList"/>
    /// so embedded spaces and quoting reach the shell intact.
    /// </summary>
    internal const string RestartPortalFrontendScript = """
        set -e
        for p in kwin_wayland plasmashell gnome-shell sway Hyprland kded6 kded5; do
          pid=$(pgrep -x "$p" | head -1)
          [ -n "$pid" ] && break
        done
        [ -n "$pid" ] || { echo "no compositor process found"; exit 1; }
        for k in XDG_CURRENT_DESKTOP XDG_SESSION_TYPE WAYLAND_DISPLAY DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS XDG_RUNTIME_DIR; do
          v=$(tr "\0" "\n" < /proc/$pid/environ | grep "^$k=" | head -1 | cut -d= -f2-)
          [ -n "$v" ] && systemctl --user set-environment "$k=$v"
        done
        systemctl --user restart xdg-desktop-portal.service
        """;

    private static readonly IReadOnlyList<string> RestartPortalFrontendArgv =
        new[] { "/bin/sh", "-c", RestartPortalFrontendScript };

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
        bool portalBackendInstalled = false, portalBackendImplementsRd = false;
        string? portalBackendPackage = null;
        string? portalReason = null;
        if (sessionBus)
        {
            (portalRd, portalSc, portalBackendInstalled, portalBackendImplementsRd, portalBackendPackage, portalReason)
                = await ProbePortalAsync(ct);
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
            PortalBackendInstalled = portalBackendInstalled,
            PortalBackendImplementsRemoteDesktop = portalBackendImplementsRd,
            PortalBackendPackageName = portalBackendPackage,
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

        // Portal-frontend restart wins when the backend is installed but the
        // frontend's interface table is frozen on a stale environment. This
        // runs without elevation; the helper script re-imports the env from a
        // live desktop process (plasmashell/gnome-shell/sway/Hyprland) before
        // restarting xdg-desktop-portal, since the host itself may be running
        // under the same stale env.
        if (report.PortalBackendInstalled &&
            report.PortalBackendImplementsRemoteDesktop &&
            !report.PortalRemoteDesktopAvailable)
        {
            actions.Add(new LinuxRepairAction(
                LinuxRepairActionKind.RestartPortalFrontend,
                "Restart xdg-desktop-portal frontend with current session environment",
                Command: null,
                RequiresElevation: false,
                ArgumentList: RestartPortalFrontendArgv));
        }

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

            // Suggest installing the portal backend only when our disk-scan
            // shows it's not present. If the backend is installed and the
            // frontend is stale, the RestartPortalFrontend action above is
            // what actually fixes the problem — installing more packages
            // would not help.
            if ((!report.PortalRemoteDesktopAvailable || !report.PortalScreenCastAvailable) &&
                !report.PortalBackendImplementsRemoteDesktop)
            {
                // Prefer the disk-scan suggestion; fall back to a desktop-aware default.
                var pkg = report.PortalBackendPackageName;
                if (string.IsNullOrEmpty(pkg))
                {
                    var isKde = ContainsToken(
                        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "KDE");
                    pkg = isKde ? "xdg-desktop-portal-kde" : "xdg-desktop-portal-gnome";
                }
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

        // RestartPortalFrontend is non-elevated and distro-agnostic — its presence
        // alone makes the plan automatable even on non-Arch systems.
        var hasNonArchAutomatable = actions.Exists(a =>
            a.Kind == LinuxRepairActionKind.RestartPortalFrontend ||
            a.Kind == LinuxRepairActionKind.RestartUserService);

        return new LinuxPrerequisiteRepairPlan
        {
            Actions = actions.AsReadOnly(),
            HasAutomatedRepair = actions.Count > 0 && (isArch || hasNonArchAutomatable),
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

    private static async Task<(
        bool RemoteDesktop,
        bool ScreenCast,
        bool BackendInstalled,
        bool BackendImplementsRemoteDesktop,
        string? BackendPackageName,
        string? Reason)> ProbePortalAsync(CancellationToken ct)
    {
        // Stage A — busctl introspect: what the running frontend currently exposes.
        bool rd = false, sc = false;
        try
        {
            var result = await RunProcessOutputAsync(
                "busctl",
                "--user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop",
                ct);

            if (result is null)
                return (false, false, false, false, null, "busctl introspect timed out or failed");

            rd = result.Contains("org.freedesktop.portal.RemoteDesktop");
            sc = result.Contains("org.freedesktop.portal.ScreenCast");

            if (rd)
                return (true, sc, true, true, null, null);
        }
        catch (Exception ex)
        {
            return (false, false, false, false, null, $"Portal probe failed: {ex.Message}");
        }

        // Stage B — RemoteDesktop missing from frontend. Inspect installed .portal
        // files to distinguish (a) "backend not installed" (suggest pacman install)
        // from (b) "backend installed but frontend frozen with stale env" (suggest
        // restart of xdg-desktop-portal). The frontend reads .portal files once at
        // startup and locks its interface table; importing env later does nothing
        // until it restarts.
        var currentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        var backendMatch = ProbePortalBackendsFromDisk(currentDesktop);

        if (backendMatch.BackendImplementsRemoteDesktop)
        {
            var reason = string.IsNullOrEmpty(currentDesktop)
                ? "Portal backend " + (backendMatch.PackageName ?? "(unknown)") +
                  " is installed but the xdg-desktop-portal frontend does not expose " +
                  "RemoteDesktop. The user-systemd manager has no XDG_CURRENT_DESKTOP, " +
                  "so the frontend cannot route to the backend. Run: " +
                  "systemctl --user import-environment XDG_CURRENT_DESKTOP XDG_SESSION_TYPE " +
                  "WAYLAND_DISPLAY DISPLAY DBUS_SESSION_BUS_ADDRESS && " +
                  "systemctl --user restart xdg-desktop-portal.service"
                : "Portal backend " + (backendMatch.PackageName ?? "(unknown)") +
                  " is installed but the xdg-desktop-portal frontend does not expose " +
                  "RemoteDesktop. Frontend has a stale environment; restart it with: " +
                  "systemctl --user restart xdg-desktop-portal.service";
            return (false, sc, true, true, backendMatch.PackageName, reason);
        }

        // No matching backend implements RemoteDesktop on disk → genuinely missing.
        return (false, sc, backendMatch.AnyBackendInstalled, false, backendMatch.PackageName,
            "org.freedesktop.portal.RemoteDesktop not found and no installed portal " +
            "backend declares it for this desktop. Install xdg-desktop-portal and a " +
            "backend (e.g. xdg-desktop-portal-kde for KDE, -gnome for GNOME, -wlr for sway/Hyprland).");
    }

    /// <summary>
    /// Scans installed <c>.portal</c> files in
    /// <c>/usr/share/xdg-desktop-portal/portals/</c> and
    /// <c>/etc/xdg-desktop-portal/portals/</c> and decides which (if any) backend
    /// is "expected" for the current desktop based on <c>UseIn=</c>/<c>NotInUse=</c>
    /// matching.
    /// </summary>
    /// <param name="currentDesktop">
    /// Value of <c>$XDG_CURRENT_DESKTOP</c>. May be empty (e.g. when the host
    /// inherits a stripped systemd-user env), in which case no UseIn match
    /// succeeds and the function returns the first RemoteDesktop-capable backend
    /// as a best-effort suggestion.
    /// </param>
    internal static PortalBackendMatch ProbePortalBackendsFromDisk(string currentDesktop)
        => ProbePortalBackendsFromDisk(currentDesktop, new[]
        {
            "/usr/share/xdg-desktop-portal/portals",
            "/etc/xdg-desktop-portal/portals",
        });

    /// <summary>Overload that takes explicit search directories. Used by tests.</summary>
    internal static PortalBackendMatch ProbePortalBackendsFromDisk(
        string currentDesktop, IReadOnlyList<string> portalDirs)
    {
        var desktopTokens = SplitDesktopTokens(currentDesktop);
        var bestPackage = (string?)null;
        bool bestImplementsRd = false;
        bool anyInstalled = false;

        foreach (var dir in portalDirs)
        {
            if (!Directory.Exists(dir)) continue;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.portal"); }
            catch { continue; }

            foreach (var file in files)
            {
                anyInstalled = true;
                PortalFileEntry parsed;
                try { parsed = ParsePortalFile(file); }
                catch { continue; }

                var implementsRd = parsed.Interfaces.Contains(
                    "org.freedesktop.impl.portal.RemoteDesktop", StringComparer.Ordinal);
                if (!implementsRd) continue;

                var matches = MatchesDesktop(parsed, desktopTokens);
                var pkgName = "xdg-desktop-portal-" +
                              Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                if (matches)
                {
                    // Matching backend wins immediately — frontend should pick this one.
                    return new PortalBackendMatch(
                        AnyBackendInstalled: true,
                        BackendImplementsRemoteDesktop: true,
                        PackageName: pkgName);
                }

                // Backend on disk does NOT match the current desktop. Remember
                // it as a fallback in two distinct senses:
                //   * If XDG_CURRENT_DESKTOP is empty (stale systemd-user env),
                //     any RD-capable backend is a legitimate candidate — the
                //     frontend would pick it once the env is restored. We mark
                //     it as implementing RemoteDesktop for *this* session so
                //     the caller's stale-frontend repair path triggers.
                //   * Otherwise XDG_CURRENT_DESKTOP is set but doesn't match
                //     this backend's UseIn — e.g. GNOME session with only the
                //     KDE backend installed. The right repair is install the
                //     correct backend, NOT restart the portal. Keep PackageName
                //     as a (weak) suggestion but DO NOT claim it implements
                //     RemoteDesktop for this session.
                if (bestPackage is null)
                {
                    bestPackage = pkgName;
                    if (desktopTokens.Count == 0)
                        bestImplementsRd = true;
                }
            }
        }

        return new PortalBackendMatch(
            AnyBackendInstalled: anyInstalled,
            BackendImplementsRemoteDesktop: bestImplementsRd,
            PackageName: bestPackage);
    }

    /// <summary>
    /// Result of a portal-file scan: whether any .portal file was found, whether
    /// any of them declare the RemoteDesktop impl interface, and a suggested
    /// package name (for installer hints or user-facing messages).
    /// </summary>
    internal sealed record PortalBackendMatch(
        bool AnyBackendInstalled,
        bool BackendImplementsRemoteDesktop,
        string? PackageName);

    private sealed record PortalFileEntry(
        IReadOnlySet<string> Interfaces,
        IReadOnlyList<string> UseIn,
        IReadOnlyList<string> NotInUse);

    /// <summary>
    /// Parses an xdg-desktop-portal <c>.portal</c> INI-style file. We only care
    /// about the <c>[portal]</c> group keys <c>Interfaces=</c>, <c>UseIn=</c>,
    /// and <c>NotInUse=</c>; values are semicolon-separated and may have trailing
    /// semicolons. Unknown keys are ignored.
    /// </summary>
    private static PortalFileEntry ParsePortalFile(string path)
    {
        var interfaces = new HashSet<string>(StringComparer.Ordinal);
        var useIn = new List<string>();
        var notInUse = new List<string>();
        bool inPortalSection = false;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inPortalSection = line.Equals("[portal]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inPortalSection) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            void AddTokens(List<string> target)
            {
                foreach (var t in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    target.Add(t);
            }

            if (key.Equals("Interfaces", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var t in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    interfaces.Add(t);
            }
            else if (key.Equals("UseIn", StringComparison.OrdinalIgnoreCase))
            {
                AddTokens(useIn);
            }
            else if (key.Equals("NotInUse", StringComparison.OrdinalIgnoreCase))
            {
                AddTokens(notInUse);
            }
        }

        return new PortalFileEntry(interfaces, useIn, notInUse);
    }

    /// <summary>
    /// Returns true when the given .portal file is selectable for the current
    /// desktop, following xdg-desktop-portal matching rules: UseIn (if present)
    /// must overlap with the current desktop tokens; otherwise NotInUse must not
    /// match; if both are absent the backend is considered desktop-agnostic.
    /// </summary>
    private static bool MatchesDesktop(PortalFileEntry entry, IReadOnlyCollection<string> desktopTokens)
    {
        if (desktopTokens.Count == 0)
        {
            // No XDG_CURRENT_DESKTOP at all — only a backend with no UseIn restriction matches.
            return entry.UseIn.Count == 0;
        }

        if (entry.UseIn.Count > 0)
        {
            foreach (var u in entry.UseIn)
                if (desktopTokens.Contains(u, StringComparer.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        if (entry.NotInUse.Count > 0)
        {
            foreach (var n in entry.NotInUse)
                if (desktopTokens.Contains(n, StringComparer.OrdinalIgnoreCase))
                    return false;
        }

        return true;
    }

    /// <summary>
    /// Splits <c>$XDG_CURRENT_DESKTOP</c> into individual tokens. The value can
    /// be either colon- or semicolon-separated (e.g. <c>KDE:Plasma</c>,
    /// <c>GNOME;Unity</c>) per the freedesktop spec.
    /// </summary>
    private static IReadOnlyCollection<string> SplitDesktopTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value.Split(new[] { ':', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
