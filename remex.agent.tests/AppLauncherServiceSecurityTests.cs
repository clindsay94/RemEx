using Remex.Agent.Services;
using Remex.Core.Models;

namespace Remex.Agent.Tests;

/// <summary>
/// Security regression tests for <see cref="AppLauncherService"/>.
///
/// VULN-3 (RemEx-s032.3): the LAUNCHAPP command passed the client-supplied TargetPath straight to
/// <c>ProcessStartInfo</c> with only a <see cref="Path.GetFullPath(string)"/> + existence check —
/// no comparison against the persisted launcher allowlist, and no rejection of UNC/remote paths.
/// A paired client could launch ANY file on the host, including a remote UNC executable
/// (<c>\\attacker\share\evil.exe</c>), as the elevated <c>remex.agent</c> process.
///
/// These tests exercise the two pure guard methods directly rather than launching real processes:
/// <see cref="AppLauncherService.IsLaunchAllowed"/> (the allowlist gate) and
/// <see cref="AppLauncherService.IsRejectedNetworkPath"/> (the hard UNC/network guard).
/// </summary>
public sealed class AppLauncherServiceSecurityTests
{
    private static AppEntry MakeEntry(string targetPath) =>
        new(Guid.NewGuid(), "Test App", targetPath, "#4A3AFF", null, Order: 0);

    [Fact]
    public void IsLaunchAllowed_PathMatchingPersistedEntry_ReturnsTrue()
    {
        var allowedPath = Path.Combine(Path.GetTempPath(), "remex-launcher-tests", "notepad.exe");
        var entries = new List<AppEntry> { MakeEntry(allowedPath) };

        // Requested path arrives pre-normalized (as it would from Path.GetFullPath in LaunchAppAsync).
        var requested = Path.GetFullPath(allowedPath);

        Assert.True(AppLauncherService.IsLaunchAllowed(requested, entries));
    }

    [Fact]
    public void IsLaunchAllowed_PathNotInAllowlist_ReturnsFalse()
    {
        var allowedPath = Path.Combine(Path.GetTempPath(), "remex-launcher-tests", "notepad.exe");
        var requestedPath = Path.Combine(Path.GetTempPath(), "remex-launcher-tests", "evil.exe");
        var entries = new List<AppEntry> { MakeEntry(allowedPath) };

        Assert.False(AppLauncherService.IsLaunchAllowed(Path.GetFullPath(requestedPath), entries));
    }

    [Fact]
    public void IsLaunchAllowed_EmptyAllowlist_ReturnsFalse()
    {
        var requestedPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "anything.exe"));

        Assert.False(AppLauncherService.IsLaunchAllowed(requestedPath, new List<AppEntry>()));
    }

    [Fact]
    public void IsLaunchAllowed_EntryWithDifferentPathFormatting_StillMatchesAfterNormalization()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "remex-launcher-tests");
        // Persisted entry has redundant path segments; the requested path is the clean form.
        // Both must normalize to the same canonical path via Path.GetFullPath.
        var storedPath = Path.Combine(baseDir, "sub", "..", "notepad.exe");
        var requestedPath = Path.GetFullPath(Path.Combine(baseDir, "notepad.exe"));
        var entries = new List<AppEntry> { MakeEntry(storedPath) };

        Assert.True(AppLauncherService.IsLaunchAllowed(requestedPath, entries));
    }

    [WindowsOnlyFact("the SANITY half asserts a UNC path matches the allowlist verbatim, which needs Windows path semantics — IsLaunchAllowed compares OrdinalIgnoreCase on Windows and Ordinal elsewhere, and a backslash is not a separator on Linux. The GUARD itself is platform-independent and is covered on every platform by IsRejectedNetworkPath_UncStyleInput_ReturnsTrue below")]
    public void UncPath_EvenIfSomehowPersistedInAllowlist_IsStillRejectedByNetworkGuard()
    {
        // Defense-in-depth: LaunchAppAsync checks IsRejectedNetworkPath BEFORE IsLaunchAllowed, so a
        // UNC path is refused unconditionally regardless of allowlist membership.
        const string uncPath = @"\\attacker\share\evil.exe";
        var entries = new List<AppEntry> { MakeEntry(uncPath) };

        // Sanity: without the network guard, the path WOULD match the allowlist verbatim — proving
        // the network guard is doing real, non-redundant work and isn't just duplicating the allowlist.
        Assert.True(AppLauncherService.IsLaunchAllowed(uncPath, entries));

        // The independent network guard rejects it regardless.
        Assert.True(AppLauncherService.IsRejectedNetworkPath(uncPath));
    }

    [Theory]
    [InlineData(@"\\server\share\file.exe")]
    [InlineData("//server/share/file.exe")]
    public void IsRejectedNetworkPath_UncStyleInput_ReturnsTrue(string uncPath)
    {
        Assert.True(AppLauncherService.IsRejectedNetworkPath(uncPath));
    }

    [Fact]
    public void IsRejectedNetworkPath_LocalAbsolutePath_ReturnsFalse()
    {
        var localPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "notepad.exe"));

        Assert.False(AppLauncherService.IsRejectedNetworkPath(localPath));
    }

    [Fact]
    public void IsRejectedNetworkPath_EmptyPath_ReturnsTrue()
    {
        Assert.True(AppLauncherService.IsRejectedNetworkPath(string.Empty));
    }
}
