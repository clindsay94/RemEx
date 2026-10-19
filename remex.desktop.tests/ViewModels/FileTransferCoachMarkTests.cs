using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>The File Transfer first-visit tips card: shown once per profile, replayable from the header's "?".</summary>
public sealed class FileTransferCoachMarkTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-files-coach-").FullName;
    private readonly DashboardLayoutService _layout;

    public FileTransferCoachMarkTests()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        _layout = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), theme);
    }

    public Task InitializeAsync() => _layout.LoadAsync();

    public Task DisposeAsync()
    {
        _layout.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private FileTransferViewModel Create(DashboardLayoutService? layout) =>
        new(new ConnectionViewModel(), layoutService: layout);

    [Fact]
    public void AProfileThatHasNotSeenTheTips_ShowsThem()
    {
        using var vm = Create(_layout);

        vm.ShowCoachMark.Should().BeTrue();
    }

    [Fact]
    public void GotIt_HidesTheTips_AndSavesThemAsSeen()
    {
        using var vm = Create(_layout);

        vm.DismissCoachMarkCommand.Execute(null);

        vm.ShowCoachMark.Should().BeFalse();
        _layout.CurrentProfile.SeenCoachMarks.Should().Contain("files");

        using var again = Create(_layout);
        again.ShowCoachMark.Should().BeFalse("seen once is seen");
    }

    [Fact]
    public void TheQuestionMark_ShowsTheTipsAgain()
    {
        using var vm = Create(_layout);
        vm.DismissCoachMarkCommand.Execute(null);

        vm.ReplayCoachMarkCommand.Execute(null);

        vm.ShowCoachMark.Should().BeTrue();
    }

    [Fact]
    public void WithoutAProfileStore_TheTipsNeverShow()
    {
        using var vm = Create(null);

        vm.ShowCoachMark.Should().BeFalse();
    }
}
