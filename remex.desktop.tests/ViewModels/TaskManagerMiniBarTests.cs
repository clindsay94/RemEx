using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-kq10x.5: the Processes page's CPU and memory mini bars are scaled to the busiest and
/// largest LISTED process, the same rule as the phone's <c>ProcessRows.scaleMax</c>.
/// </summary>
public class TaskManagerMiniBarTests
{
    [Fact]
    public void BarsScaleToTheBusiestAndLargestListedProcess()
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());

        vm.ApplyReceivedProcessList(new List<ProcessInfo>
        {
            new() { Id = 1, Name = "game", CpuUsage = 40, MemoryUsage = 4_000_000_000 },
            new() { Id = 2, Name = "browser", CpuUsage = 10, MemoryUsage = 1_000_000_000 },
        });

        vm.MaxCpuUsage.Should().Be(40);
        vm.MaxMemoryUsage.Should().Be(4_000_000_000);
    }

    [Fact]
    public void SearchNarrowsTheScaleToWhatIsShown()
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());
        vm.ApplyReceivedProcessList(new List<ProcessInfo>
        {
            new() { Id = 1, Name = "game", CpuUsage = 40, MemoryUsage = 4_000_000_000 },
            new() { Id = 2, Name = "browser", CpuUsage = 10, MemoryUsage = 1_000_000_000 },
        });

        vm.SearchText = "browser";

        vm.MaxCpuUsage.Should().Be(10);
        vm.MaxMemoryUsage.Should().Be(1_000_000_000);
    }

    [Fact]
    public void AnIdleOrEmptyListScalesToOneSoBarsStayEmpty()
    {
        TaskManagerViewModel.ScaleMax(new double[] { 0, 0 }).Should().Be(1);
        TaskManagerViewModel.ScaleMax(new double[0]).Should().Be(1);
    }
}
