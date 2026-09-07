using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The shared busy affordance's skeleton-rows mode over the process list's first fill (RemEx-kjdi
/// §2) — before this bead a fresh Task Manager view showed an empty list with no explanation while
/// waiting for the host's first <c>ProcessListSync</c>.
/// </summary>
public class TaskManagerBusyFlagTests
{
    [Fact]
    public void IsLoading_StartsTrueUntilTheFirstProcessListArrives()
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());

        vm.IsLoading.Should().BeTrue("nothing has arrived yet for a freshly-opened view");

        vm.ApplyReceivedProcessList(new List<ProcessInfo>());

        vm.IsLoading.Should().BeFalse("the first fill (even an empty one) answers the question");
    }

    [Fact]
    public async Task RefreshCommand_ShowsTheSkeletonAgain_WhenTheListIsStillEmpty()
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());
        vm.ApplyReceivedProcessList(new List<ProcessInfo>());
        vm.IsLoading.Should().BeFalse();

        var command = (IAsyncRelayCommand)vm.RefreshCommand;
        var task = command.ExecuteAsync(null);

        vm.IsLoading.Should().BeTrue("a manual refresh with nothing on screen yet is still a first fill");

        await task;
    }

    [Fact]
    public async Task RefreshCommand_DoesNotReintroduceTheSkeleton_OverAnAlreadyPopulatedList()
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());
        vm.ApplyReceivedProcessList(new List<ProcessInfo> { new() { Id = 1, Name = "notepad" } });
        vm.IsLoading.Should().BeFalse();

        var command = (IAsyncRelayCommand)vm.RefreshCommand;
        await command.ExecuteAsync(null);

        vm.IsLoading.Should().BeFalse(
            "a routine poll refresh must not blank an already-populated list back to the skeleton");
    }

    // NOTE ON THE EXCEPTION PATH: ConnectionViewModel.RequestProcessListAsync routes through
    // SendGuardedAsync, which ConnectionViewModelTests already pins as never throwing even when
    // disconnected (RequestProcessListAsync_WhenNotConnected_ShouldNotThrow) — there is no seam here
    // to force it to fail, so RefreshProcessesAsync's catch/rethrow is defensive rather than
    // independently exercised by a test.
}
