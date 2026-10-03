using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services.Launching;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-pp4cm.2: links and folders the PC app opens must start with the user's normal permissions,
/// not the elevated host's administrator token. <see cref="UserLauncher"/> owns the rule (de-elevated
/// first, the old shell-execute launch only as a fallback), and every desktop call site has to go
/// through it.
/// </summary>
/// <remarks>
/// No real process is started. The fallback runs through <see cref="UserLauncher.Launch"/>'s
/// <c>standardStart</c> seam, and the call-site tests install a fake launcher that answers
/// <see cref="UnelevatedLaunchResult.Launched"/>, so nothing falls through to <c>Process.Start</c>.
/// <see cref="App.EmbeddedHostServices"/> is saved and restored; parallel execution is disabled
/// assembly-wide.
/// </remarks>
public sealed class UserLauncherTests : IDisposable
{
    private readonly IServiceProvider? _savedHost = App.EmbeddedHostServices;
    private readonly FakeUnelevatedLauncher _fake = new();
    private readonly List<ProcessStartInfo> _standardStarts = [];

    public void Dispose() => App.EmbeddedHostServices = _savedHost;

    [Fact]
    public void WhenTheUnelevatedRouteLaunchesTheOldLaunchNeverRuns()
    {
        _fake.Result = UnelevatedLaunchResult.Launched;

        var route = UserLauncher.Launch(_fake, @"C:\Apps\tool.exe", @"C:\Apps", _standardStarts.Add);

        route.Should().Be(UserLaunchRoute.Unelevated);
        _fake.Calls.Should().ContainSingle().Which.Should().Be((@"C:\Apps\tool.exe", (string?)null, (string?)@"C:\Apps"));
        _standardStarts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UnelevatedLaunchResult.ElevationRequired)]
    [InlineData(UnelevatedLaunchResult.NoShell)]
    [InlineData(UnelevatedLaunchResult.NotNeeded)]
    [InlineData(UnelevatedLaunchResult.Failed)]
    public void WhenTheUnelevatedRouteDeclinesTheOldShellExecuteLaunchRuns(UnelevatedLaunchResult declined)
    {
        _fake.Result = declined;

        var route = UserLauncher.Launch(_fake, "https://example.org/", null, _standardStarts.Add);

        route.Should().Be(UserLaunchRoute.Standard);
        var psi = _standardStarts.Should().ContainSingle().Subject;
        psi.FileName.Should().Be("https://example.org/");
        psi.UseShellExecute.Should().BeTrue("documents, folders and links only open through their association");
        psi.WorkingDirectory.Should().BeEmpty();
    }

    [Fact]
    public void WithNoLauncherAvailableTheOldLaunchRuns()
    {
        // Client-only mode: the embedded host failed to start, so there is nothing to resolve.
        var route = UserLauncher.Launch(null, @"C:\Logs", null, _standardStarts.Add);

        route.Should().Be(UserLaunchRoute.Standard);
        _standardStarts.Should().ContainSingle().Which.FileName.Should().Be(@"C:\Logs");
    }

    [Fact]
    public void HomeLinksGoThroughTheUnelevatedLauncher()
    {
        InstallFake();
        var home = new HomeViewModel(new ConnectionViewModel(), null!);
        try
        {
            home.OpenGitHubCommand.Execute(null);
            home.OpenPlayStoreCommand.Execute(null);
            home.OpenHwInfoCommand.Execute(null);
        }
        finally
        {
            home.Dispose();
        }

        _fake.Calls.Select(c => c.Target).Should().Equal(
            "https://github.com/clindsay94/remex",
            "https://play.google.com/store/apps/details?id=com.clindsay94.remex",
            "https://www.hwinfo.com/download/");
    }

    [Fact]
    public void AboutLinksGoThroughTheUnelevatedLauncher()
    {
        InstallFake();
        var about = new AboutViewModel(new ConnectionViewModel(), null!);
        try
        {
            about.OpenGitHubCommand.Execute(null);
            about.DownloadUpdateCommand.Execute(null);
        }
        finally
        {
            about.Dispose();
        }

        _fake.Calls.Should().HaveCount(2);
        _fake.Calls[0].Target.Should().Be("https://github.com/clindsay94/remex");
        _fake.Calls[1].Target.Should().StartWith("https://");
    }

    [WindowsOnlyFact("the Windows folder launcher shell-executes the folder; Linux keeps xdg-open")]
    public void TheLogsFolderGoesThroughTheUnelevatedLauncherOnWindows()
    {
        InstallFake();
        var folder = Path.GetTempPath();

        DiagnosticLogsViewModel.LaunchFolder(folder);

        _fake.Calls.Should().ContainSingle().Which.Target.Should().Be(folder);
    }

    private void InstallFake()
    {
        _fake.Result = UnelevatedLaunchResult.Launched;
        App.EmbeddedHostServices = new ServiceCollection()
            .AddSingleton<IUnelevatedLauncher>(_fake)
            .BuildServiceProvider();
    }

    private sealed class FakeUnelevatedLauncher : IUnelevatedLauncher
    {
        public UnelevatedLaunchResult Result { get; set; }

        public List<(string Target, string? Arguments, string? WorkingDirectory)> Calls { get; } = [];

        public UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory)
        {
            Calls.Add((target, arguments, workingDirectory));
            return Result;
        }
    }
}
