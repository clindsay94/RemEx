using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Services;
using Remex.Desktop.Configuration;
using Remex.Desktop.Services;
using Moq;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.Services.Security;

namespace Remex.Desktop.Tests;

/// <summary>
/// Proves that no test in this assembly can reach the developer's real per-user state (RemEx-ln0k).
///
/// <para>
/// The sibling file in <c>remex.agent.tests</c> covers the HOST stores that RemEx-4u29 moved into
/// <c>C:\ProgramData\RemEx</c>. This one covers the four client-side stores that review of that
/// bead listed as knowingly out of its scope, because they never lived in ProgramData: the
/// certificate-pin store, the dashboard layout, the recent-activity feed and the configured path
/// defaults. They are per-user rather than machine-wide, which made them a smaller problem, not a
/// different one — <c>pinned_hosts.json</c> records which host certificates this user has decided to
/// trust, so a fixture entry there is a pinned-certificate record, and two of the others are the
/// user's own saved arrangement and history. A test run that rewrites them is both a trust-store
/// edit and the destruction of real settings.
/// </para>
///
/// <para>
/// These assert on the DEFAULT paths — the ones a service resolves when nothing is injected — for
/// the same reason the agent-side tests do: injecting a temp path was never the failing case. The
/// assertions are on paths rather than on writes on purpose. A behavioural test would have to
/// perform the write to observe it, and the defect injection that proves these tests can fail would
/// then perform that write against the real store, which is the exact event the bead exists to
/// prevent. Each service resolves its default through the same expression the property exposes, so
/// there is nothing for the two to drift apart on.
/// </para>
/// </summary>
public sealed class HostStateRedirectionTests
{
    private static string OverrideDirectory =>
        RemexDataPaths.HostStateDirectoryOverride
        ?? throw new InvalidOperationException(
            "The host-state redirect is not active. build/TestHostStateRedirect.cs should have run as "
            + "a module initializer before any test; without it this assembly writes to the real store.");

    /// <summary>
    /// The per-user directory the redirect exists to keep tests out of. Computed here rather than
    /// read from <see cref="RemexDataPaths"/> so a change that redefines that class cannot quietly
    /// make the negative assertions below vacuous.
    /// </summary>
    private static string RealPerUserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Remex");

    private static void AssertRedirected(string resolvedPath, string what)
    {
        Assert.True(
            resolvedPath.StartsWith(OverrideDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"{what} resolved to '{resolvedPath}', which is outside the per-run test directory '{OverrideDirectory}'.");

        // The negative half, which is the one that names the actual failure. The trailing separator
        // matters: without it a sibling directory called RemexFoo would trip this and report a leak
        // into the real profile that had not happened.
        Assert.False(
            resolvedPath.StartsWith(
                RealPerUserDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"{what} resolved into the real per-user store at '{RealPerUserDirectory}'.");
    }

    [Fact]
    public void PerUserDirectory_IsRedirected()
        => Assert.Equal(OverrideDirectory, RemexDataPaths.PerUserDirectory);

    /// <summary>
    /// The sharpest of the four: a pin says "this SPKI is the host I paired with", so a fixture that
    /// can add one can teach the client to trust a certificate, and a fixture that can delete one
    /// silently downgrades a real host back to trust-on-first-use.
    /// </summary>
    [Fact]
    public void PinnedCertStore_DefaultPath_IsRedirected()
        => AssertRedirected(PinnedCertStore.DefaultStorePathForTests, "The certificate-pin store");

    /// <summary>
    /// The constructed instance, not just the static default — the seam and the constructor have to
    /// agree, and this is what pins that they do.
    /// </summary>
    [Fact]
    public void PinnedCertStore_ConstructedWithNoPath_UsesTheRedirectedStore()
    {
        var store = new PinnedCertStore(NullLogger<PinnedCertStore>.Instance);

        AssertRedirected(store.StorePathForTests, "A default-constructed certificate-pin store");
    }

    [Fact]
    public void DashboardLayout_DefaultPath_IsRedirected()
        => AssertRedirected(DashboardLayoutService.DefaultFilePath, "The dashboard layout");

    /// <summary>
    /// Two tests in this assembly already construct this service with the real constructor, so
    /// before the redirect covered it they created and overwrote the developer's own saved dashboard
    /// on every run.
    /// </summary>
    [Fact]
    public void DashboardLayout_ConstructedService_UsesTheRedirectedFile()
    {
        var service = new DashboardLayoutService(new ThemeService());

        AssertRedirected(service.FilePathForTests, "A constructed dashboard layout service");
    }

    /// <summary>
    /// The fifth per-user store, missed by RemEx-ln0k's list of four (RemEx-mz9f).
    /// </summary>
    /// <remarks>
    /// Constructed with the real constructor, because the defect was IN the constructor: it resolved
    /// the backups directory from SpecialFolder before any test could redirect it. Asserting a static
    /// default would not have covered the thing that was wrong.
    /// </remarks>
    [Fact]
    public void Backups_ConstructedService_IsRedirected()
    {
        var service = new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        AssertRedirected(service.BackupsDirectory, "The savefile backups directory");
    }

    [Fact]
    public void RecentActivity_DefaultPath_IsRedirected()
        => AssertRedirected(ActivityService.DefaultFilePathForTests, "The recent-activity feed");

    /// <summary>
    /// Through the singleton, because that is the only way production reaches it: the agent's
    /// message handlers call <c>ActivityService.Instance.Record</c>, so any test that drove a
    /// transfer or a ping appended fixture entries to the real feed.
    /// </summary>
    [Fact]
    public void RecentActivity_Singleton_UsesTheRedirectedFile()
        => AssertRedirected(ActivityService.Instance.FilePathForTests, "The activity singleton");

    [Fact]
    public void PathSettings_Defaults_AreRedirected()
    {
        var paths = new PathSettings();

        Assert.Equal(OverrideDirectory, paths.AppDataDirectory);
        AssertRedirected(paths.DashboardProfilesDirectory, "The dashboard-profiles directory");
        AssertRedirected(paths.LogsDirectory, "The logs directory");
    }

    /// <summary>
    /// Exactly which production files still resolve the per-user directory for themselves
    /// (RemEx-mzbn).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE DEFECT IS A SECOND RESOLVER, NOT A WRONG PATH. App read the dashboard layout before the
    /// window is shown and hand-built the path from SpecialFolder.LocalApplicationData, so that one
    /// read did not honour the redirect while the service's did. Both pointed at the same file in
    /// production, which is why it was invisible: a test that redirected one still had the other
    /// reading, and writing, the developer's own saved dashboard. That is what RemEx-ln0k existed to
    /// stop, reappearing one caller over.
    /// </para>
    /// <para>
    /// AN EXACT SET, NOT A CEILING. A "no more than N" form goes quiet as soon as somebody fixes one
    /// and adds another; requiring the set to match means a NEW hand-built path fails, and so does a
    /// FIXED one left in this list — which is what keeps the list honest rather than decorative.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheKnownStragglersResolveThePerUserDirectoryThemselves()
    {
        // One left, and its name is what makes the guard honest: FileTransferRootSettingsService is
        // RemEx-dnn2q, filed off the back of this test. RemexSavefileService came off this list under
        // RemEx-mz9f — the exact-set assertion is what FORCED the deletion rather than leaving a
        // stale entry vouching for a fix nobody made.
        string[] known = ["FileTransferRootSettingsService.cs"];

        var offenders = Directory
            .GetFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadLines(f).Any(line =>
                !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && line.Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(known.OrderBy(f => f, StringComparer.Ordinal).ToArray(), offenders);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    /// <summary>
    /// The three <see cref="PathSettings"/> defaults must stay distinct directories. Redirecting
    /// them by replacing the whole path rather than its base would collapse profiles and logs onto
    /// the app-data directory, which no assertion above would notice.
    /// </summary>
    [Fact]
    public void PathSettings_Defaults_RemainThreeDistinctDirectories()
    {
        var paths = new PathSettings();

        Assert.Equal(
            3,
            new[] { paths.AppDataDirectory, paths.DashboardProfilesDirectory, paths.LogsDirectory }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }
}
