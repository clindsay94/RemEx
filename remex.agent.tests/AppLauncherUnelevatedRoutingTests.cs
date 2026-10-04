using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Tests;

/// <summary>
/// RemEx-pp4cm.2: apps launched from the phone or the Apps page must start with the user's normal
/// permissions, not the elevated host's administrator token. These pin that
/// <see cref="AppLauncherService"/> hands every allowed launch to <see cref="IUnelevatedLauncher"/>,
/// falls back to the old shell-execute launch only when that route declines, and that validation
/// still runs FIRST - a rejected path never reaches either launcher.
/// </summary>
/// <remarks>
/// No real process is started: the de-elevated route is a fake, and the fallback goes through
/// <see cref="AppLauncherService.StandardStartOverride"/>.
/// </remarks>
public sealed class AppLauncherUnelevatedRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remex-pp4cm2-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly FakeUnelevatedLauncher _unelevated = new();
    private readonly List<ProcessStartInfo> _standardStarts = [];

    public AppLauncherUnelevatedRoutingTests()
    {
        Directory.CreateDirectory(_dir);
        _target = Path.Combine(_dir, "tool.exe");
        File.WriteAllText(_target, string.Empty);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AppLauncherService Service(params string[] allowed) => new(
        NullLogger<AppLauncherService>.Instance,
        new Storage(allowed),
        _unelevated)
    {
        StandardStartOverride = _standardStarts.Add,
    };

    [Fact]
    public async Task AnAllowedLaunchGoesThroughTheUnelevatedLauncherWithTheAppsOwnFolder()
    {
        _unelevated.NextResult = UnelevatedLaunchResult.Launched;

        await Service(_target).LaunchAppAsync(_target);

        var call = Assert.Single(_unelevated.Calls);
        Assert.Equal(Path.GetFullPath(_target), call.Target);
        Assert.Null(call.Arguments);
        Assert.Equal(_dir, call.WorkingDirectory);
        Assert.Empty(_standardStarts); // launched de-elevated: no administrator-token launch at all
    }

    [Theory]
    [InlineData(UnelevatedLaunchResult.ElevationRequired)]
    [InlineData(UnelevatedLaunchResult.NoShell)]
    [InlineData(UnelevatedLaunchResult.NotNeeded)]
    [InlineData(UnelevatedLaunchResult.ProgramRouteUnavailable)]
    public async Task WhenTheUnelevatedRouteDeclinesTheOldLaunchStillRuns(UnelevatedLaunchResult declined)
    {
        // "Nothing that works today stops working": an app whose manifest needs administrator
        // rights, or a PC with no Explorer running, still launches the way it always did.
        _unelevated.NextResult = declined;

        await Service(_target).LaunchAppAsync(_target);

        Assert.Single(_unelevated.Calls);
        var psi = Assert.Single(_standardStarts);
        Assert.Equal(Path.GetFullPath(_target), psi.FileName);
        Assert.True(psi.UseShellExecute);
        Assert.Equal(_dir, psi.WorkingDirectory);
    }

    [Fact]
    public async Task AFailedDocumentOrFolderOpenIsReportedAndNeverRetriedElevated()
    {
        // Failed is the shell route giving up (Explorer unreachable after the retry and the
        // explorer.exe last resort). Falling back here would run the target as administrator, which
        // is the bug RemEx-pp4cm.2 fixes, so the failure goes back to the caller instead.
        _unelevated.NextResult = UnelevatedLaunchResult.Failed;

        await Assert.ThrowsAsync<UserLaunchFailedException>(() => Service(_target).LaunchAppAsync(_target));

        Assert.Single(_unelevated.Calls);
        Assert.Empty(_standardStarts);
    }

    [Fact]
    public async Task APathNotOnTheAllowlistNeverReachesEitherLauncher()
    {
        _unelevated.NextResult = UnelevatedLaunchResult.Launched;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Service(/* nothing allowed */).LaunchAppAsync(_target));

        Assert.Empty(_unelevated.Calls);
        Assert.Empty(_standardStarts);
    }

    [WindowsOnlyFact("a backslash UNC path is only a network path on Windows; on Linux '\\\\attacker\\share\\evil.exe' "
        + "is an ordinary relative file name, and a Linux share is just a mounted directory")]
    public async Task ANetworkPathNeverReachesEitherLauncherEvenWhenAllowlisted()
    {
        const string unc = @"\\attacker\share\evil.exe";
        _unelevated.NextResult = UnelevatedLaunchResult.Launched;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(unc).LaunchAppAsync(unc));

        Assert.Empty(_unelevated.Calls);
        Assert.Empty(_standardStarts);
    }

    [Fact]
    public async Task AMissingTargetNeverReachesEitherLauncher()
    {
        var missing = Path.Combine(_dir, "gone.exe");
        _unelevated.NextResult = UnelevatedLaunchResult.Launched;

        await Assert.ThrowsAsync<FileNotFoundException>(() => Service(missing).LaunchAppAsync(missing));

        Assert.Empty(_unelevated.Calls);
        Assert.Empty(_standardStarts);
    }

    [Fact]
    public void TheHostRegistersTheLauncherAppLauncherServiceNeeds()
    {
        // The constructor now requires IUnelevatedLauncher, so a missing registration would only
        // surface when the first launch resolved the service. Pin the registration in source, the
        // same way other HostBootstrapper wiring is pinned.
        var bootstrapper = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "HostBootstrapper.cs"));

        Assert.Contains("AddSingleton<Remex.Desktop.Services.Launching.IUnelevatedLauncher>", bootstrapper);
        Assert.Contains("new Remex.Agent.Services.Launching.WindowsUnelevatedLauncher(", bootstrapper);
        Assert.Contains("new Remex.Agent.Services.Launching.NoOpUnelevatedLauncher()", bootstrapper);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remex.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Remex.sln not found above the test output.");
    }

    private sealed class FakeUnelevatedLauncher : IUnelevatedLauncher
    {
        public UnelevatedLaunchResult NextResult { get; set; }

        public List<(string Target, string? Arguments, string? WorkingDirectory)> Calls { get; } = [];

        public UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory)
        {
            Calls.Add((target, arguments, workingDirectory));
            return NextResult;
        }
    }

    private sealed class Storage(string[] allowed) : ILauncherStorageService
    {
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(
            allowed.Select((path, i) => new AppEntry(Guid.NewGuid(), "App", path, "#4A3AFF", null, Order: i)).ToList());

        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }
}
