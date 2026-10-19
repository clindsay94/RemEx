using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>"Open file location" on a launcher tile (PC-3.3).</summary>
public class LauncherOpenFileLocationTests
{
    private sealed class FakeStorage : ILauncherStorageService
    {
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>());
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private static AppEntry Entry(string target) => new(Guid.NewGuid(), "x", target, "#000000", null);

    [Fact]
    public void ShellUrlAndNetworkTargets_AreDisabled()
    {
        var vm = new AppLauncherViewModel(new ConnectionViewModel(), null!, new FakeStorage());

        vm.OpenFileLocationCommand.CanExecute(Entry(@"shell:AppsFolder\x")).Should().BeFalse();
        vm.OpenFileLocationCommand.CanExecute(Entry("https://example.com")).Should().BeFalse();
        vm.OpenFileLocationCommand.CanExecute(Entry(@"\\server\share\a.exe")).Should().BeFalse();
    }

    [Fact]
    public void AMappedNetworkDriveOrMount_CountsAsNetwork_ByItsLongestMatchingRoot()
    {
        var windows = OperatingSystem.IsWindows();
        var local = windows ? @"C:\" : "/";
        var share = windows ? @"Z:\" : "/mnt/Shared";
        var drives = new[] { (local, DriveType.Fixed), (share, DriveType.Network) };
        var onShare = windows ? @"Z:\Tools\app.exe" : "/mnt/Shared/Tools/app";
        var onLocal = windows ? @"C:\Tools\app.exe" : "/mnt/SharedNot/app";

        AppLauncherViewModel.IsOnNetworkDrive(onShare, drives).Should().BeTrue(
            "a dead mapped share would block the UI thread when the menu stats it");
        AppLauncherViewModel.IsOnNetworkDrive(onLocal, drives).Should().BeFalse(
            "only the drive or mount that actually holds the path decides");
        AppLauncherViewModel.IsOnNetworkDrive(onShare, []).Should().BeFalse();
    }

    [Fact]
    public void AnExistingFile_IsEnabled_AndTheSeamReceivesItsPath()
    {
        var vm = new AppLauncherViewModel(new ConnectionViewModel(), null!, new FakeStorage());
        string? revealed = null;
        vm.RevealInFileManager = p => revealed = p;
        var file = Path.GetTempFileName();
        try
        {
            var entry = Entry(file);

            vm.OpenFileLocationCommand.CanExecute(entry).Should().BeTrue();
            vm.OpenFileLocationCommand.Execute(entry);

            revealed.Should().Be(file);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
