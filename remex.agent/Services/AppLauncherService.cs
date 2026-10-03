using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Services;

public class AppLauncherService : IAppLauncherService
{
    private readonly ILogger<AppLauncherService> _logger;
    private readonly ILauncherStorageService _launcherStorage;
    private readonly IUnelevatedLauncher _unelevatedLauncher;

    public AppLauncherService(
        ILogger<AppLauncherService> logger,
        ILauncherStorageService launcherStorage,
        IUnelevatedLauncher unelevatedLauncher)
    {
        _logger = Guard.NotNull(logger);
        _launcherStorage = Guard.NotNull(launcherStorage);
        _unelevatedLauncher = Guard.NotNull(unelevatedLauncher);
    }

    /// <summary>
    /// Test seam for the standard (elevated) fallback launch, so a test can prove the fallback ran
    /// without starting a real process. <c>null</c> - always, in production - means the real
    /// <c>Process.Start</c>.
    /// </summary>
    internal Action<ProcessStartInfo>? StandardStartOverride { get; init; }

    public async Task LaunchAppAsync(string targetPath)
    {
        string fullPath;
        try
        {
            // Normalize first so every later check (network-path guard, allowlist comparison,
            // existence check) operates on the same canonical form the client can't smuggle
            // past with "..", trailing separators, or mixed slash styles.
            fullPath = Path.GetFullPath(targetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Launch request rejected: target path could not be normalized.");
            throw new UnauthorizedAccessException("Launch target path is invalid.");
        }

        // VULN-3 hard guard #1: reject UNC / remote / mapped-network-drive paths outright, before
        // the allowlist is even consulted. A paired client must never be able to make the elevated
        // host reach out to a remote share and execute whatever is sitting there.
        if (IsRejectedNetworkPath(fullPath))
        {
            _logger.LogWarning("Launch request rejected: target resolves to a network/UNC path, which is never a valid launch target.");
            throw new UnauthorizedAccessException("Network paths are not permitted as launch targets.");
        }

        // VULN-3 hard guard #2: the requested path must match a persisted launcher entry
        // (Settings > App Launcher on the desktop UI, synced to the phone). This is the actual
        // allowlist — a client can only ever launch something the PC's owner already added
        // themselves, never an arbitrary path supplied over the wire.
        //
        // "added themselves" is enforced elsewhere and was not always true: until RemEx-q6xt a
        // paired client could send launcher_add, or replace the whole list with launcher_sync, and
        // then launch what it had just added — making this check measure a list the caller
        // controlled. PingPongHandler.RequiresLoopback now refuses those three message types from
        // any non-loopback connection, which is what this sentence depends on. If that gate is
        // ever relaxed, this comment becomes false again.
        var entries = await _launcherStorage.LoadEntriesAsync();
        if (!IsLaunchAllowed(fullPath, entries))
        {
            _logger.LogWarning("Launch request rejected: target is not a persisted launcher entry.");
            throw new UnauthorizedAccessException("Launch target is not in the allowed launcher list.");
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            _logger.LogWarning("Launch target does not exist: {targetPath}", fullPath);
            throw new FileNotFoundException("Launch target not found.", fullPath);
        }

        try
        {
            // RemEx runs inside the signed-in user's interactive session, so the app lands on the
            // user's desktop on every platform. The old Session-0 CreateProcessAsUser bridge (via
            // WindowsActiveSession) is gone. (RemEx-aep Phase 4)
            //
            // De-elevation happens HERE, after every check above has passed (RemEx-pp4cm.2): a path
            // the allowlist or the network guard rejected never reaches the launcher at all.
            Launch(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch app.");
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="requestedFullPath"/> matches the normalized <see cref="AppEntry.TargetPath"/>
    /// of one of the persisted launcher entries. Both sides are re-normalized with
    /// <see cref="Path.GetFullPath(string)"/> so storage-side formatting quirks (trailing separators,
    /// relative segments) can't cause a false negative. Comparison is ordinal — case-insensitive on
    /// Windows (where the filesystem is case-insensitive) and case-sensitive elsewhere, matching this
    /// repo's cross-platform (Windows / CachyOS-Linux) parity requirement.
    /// </summary>
    internal static bool IsLaunchAllowed(string requestedFullPath, IEnumerable<AppEntry> allowedEntries)
    {
        if (string.IsNullOrEmpty(requestedFullPath) || allowedEntries is null)
            return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var entry in allowedEntries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.TargetPath))
                continue;

            string normalizedEntryPath;
            try
            {
                normalizedEntryPath = Path.GetFullPath(entry.TargetPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
            {
                // A malformed persisted entry can't match anything; skip rather than fault the
                // whole allowlist check.
                continue;
            }

            if (string.Equals(requestedFullPath, normalizedEntryPath, comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is a UNC path, a bare <c>\\server\share</c> /
    /// <c>//server/share</c> string, or (on Windows) a drive letter mapped to a network share.
    /// This check is deliberately independent of the allowlist: even if a UNC path were somehow
    /// present in the persisted launcher list, remote execution as the elevated host process is
    /// never acceptable, so this guard runs first and unconditionally rejects it.
    /// </summary>
    internal static bool IsRejectedNetworkPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return true;

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            return true;

        if (Uri.TryCreate(fullPath, UriKind.Absolute, out var uri) && uri.IsUnc)
            return true;

        if (OperatingSystem.IsWindows() && IsMappedNetworkDrive(fullPath))
            return true;

        return false;
    }

    private static bool IsMappedNetworkDrive(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return false;

            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unresolvable/unmapped root isn't a network drive as far as this guard cares;
            // the existence check further down will reject it on its own merits.
            return false;
        }
    }

    private void Launch(string targetPath)
    {
        _logger.LogInformation("Launching app normally: {targetPath}", targetPath);
        var appDir = Path.GetDirectoryName(targetPath);

        // UserLauncher tries the user's normal permissions first and falls back to the old
        // shell-execute launch (administrator rights, from this elevated host) only when the target
        // itself needs elevation, no desktop shell is running, or the de-elevated attempt failed -
        // so nothing that launched before stops launching. Shell execute stays the launch style in
        // both routes: launcher entries are added via a picker that allows "All Files", so an entry
        // can legitimately be a document that only opens through its file association (and folders
        // need it too). The allowlist check above is the actual security boundary: by the time we
        // reach this line the path has been proven to be a network-path-free path that matches an
        // entry the PC's own owner persisted.
        var route = UserLauncher.Launch(_unelevatedLauncher, targetPath, appDir, StandardStartOverride);
        if (route == UserLaunchRoute.Standard)
            _logger.LogInformation("App launched with RemEx's own permissions (the normal-permission route was not available).");
    }
}
