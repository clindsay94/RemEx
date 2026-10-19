using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services.Launching;
using Xunit;

namespace Remex.Desktop.Tests.Services;

public class FileManagerLauncherTests
{
    [Fact]
    public void TheWindowsSelectArguments_QuoteThePath()
    {
        FileManagerLauncher.WindowsSelectArguments(@"C:\My Files\a,b.txt").Should().Be("/select,\"C:\\My Files\\a,b.txt\"");
    }

    [Fact]
    public void TheLinuxShowItemsUri_PercentEncodesSpacesAndHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "my files", "a#b.txt");

        var uri = FileManagerLauncher.LinuxShowItemsUri(path);

        uri.Should().StartWith("file:///");
        uri.Should().Contain("my%20files").And.Contain("a%23b.txt");
        FileManagerLauncher.LinuxShowItemsArguments(path).Should().Contain("array:string:" + uri)
            .And.Contain("org.freedesktop.FileManager1.ShowItems");
    }

    [WindowsOnlyFact("RevealOnWindows builds a Windows explorer.exe command line")]
    public void WhenTheUnelevatedLauncherCannotHelp_TheFolderOpensAndAnElevatedExplorerIsNeverStarted()
    {
        var path = @"C:\Users\Someone\file.txt";
        foreach (var result in new[]
        {
            UnelevatedLaunchResult.ProgramRouteUnavailable, UnelevatedLaunchResult.ElevationRequired,
            UnelevatedLaunchResult.NoShell, UnelevatedLaunchResult.Failed,
        })
        {
            var launcher = new FakeLauncher(result);
            var opened = new List<string>();
            var started = new List<ProcessStartInfo>();

            FileManagerLauncher.RevealOnWindows(path, launcher, opened.Add, psi => { started.Add(psi); return null; });

            launcher.Calls.Should().ContainSingle().Which.Arguments.Should().Be(FileManagerLauncher.WindowsSelectArguments(path));
            opened.Should().Equal(@"C:\Users\Someone");
            started.Should().BeEmpty($"{result} must never fall back to an elevated explorer.exe");
        }
    }

    [WindowsOnlyFact("RevealOnWindows builds a Windows explorer.exe command line")]
    public void WhenTheUnelevatedLauncherSucceeds_NothingElseIsStarted()
    {
        var launcher = new FakeLauncher(UnelevatedLaunchResult.Launched);
        var opened = new List<string>();
        var started = new List<ProcessStartInfo>();

        FileManagerLauncher.RevealOnWindows(@"C:\a\b.txt", launcher, opened.Add, psi => { started.Add(psi); return null; });

        opened.Should().BeEmpty();
        started.Should().BeEmpty();
    }

    [WindowsOnlyFact("RevealOnWindows builds a Windows explorer.exe command line")]
    public void WithNoLauncherOrNothingToDrop_ExplorerIsStartedDirectlyWithTheSelectArguments()
    {
        var path = @"C:\a\b.txt";
        foreach (IUnelevatedLauncher? launcher in new IUnelevatedLauncher?[] { null, new FakeLauncher(UnelevatedLaunchResult.NotNeeded) })
        {
            var started = new List<ProcessStartInfo>();

            FileManagerLauncher.RevealOnWindows(path, launcher, _ => { }, psi => { started.Add(psi); return null; });

            started.Should().ContainSingle().Which.Arguments.Should().Be(FileManagerLauncher.WindowsSelectArguments(path));
        }
    }

    [Fact]
    public void OnLinux_ACouldNotStartDbusSend_FallsBackToTheFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "x", "a.txt");
        var opened = new List<string>();

        FileManagerLauncher.RevealOnLinux(path, opened.Add, _ => null);

        opened.Select(Path.GetFullPath).Should().Equal(Path.GetFullPath(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task OnLinux_DbusSendsExitCode_DecidesWhetherTheFolderOpensInstead(int exitCode, bool opensFolder)
    {
        // Non-zero is dbus-send with no session bus or no FileManager1 service: the reveal did nothing.
        var parent = Path.Combine(Path.GetTempPath(), "x");
        var opened = new List<string>();

        await FileManagerLauncher.WaitForReplyAsync(StartExiting(exitCode), parent, opened.Add);

        if (opensFolder)
            opened.Should().Equal(parent);
        else
            opened.Should().BeEmpty("a file manager that answered has already shown the file");
    }

    private static Process StartExiting(int code)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", $"exit {code}" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", $"exit {code}" } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }

    private sealed class FakeLauncher(UnelevatedLaunchResult result) : IUnelevatedLauncher
    {
        public List<(string Target, string? Arguments)> Calls { get; } = new();

        public UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory)
        {
            Calls.Add((target, arguments));
            return result;
        }
    }
}
