using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>Task Manager sort direction, the dismissable kill error, the no-matches line, and the file-path actions (PC-3.1, PC-3.2).</summary>
public class TaskManagerSortTests
{
    private static ProcessInfo Proc(int id, string name, double cpu = 0, long mem = 0, string path = "") =>
        new() { Id = id, Name = name, CpuUsage = cpu, MemoryUsage = mem, FilePath = path };

    private static TaskManagerViewModel Make(params ProcessInfo[] processes)
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());
        vm.ApplyReceivedProcessList(new List<ProcessInfo>(processes));
        return vm;
    }

    [Fact]
    public void ChoosingCpu_SortsHighestFirst_AndAgainFlipsIt()
    {
        var vm = Make(Proc(1, "a", cpu: 1), Proc(2, "b", cpu: 9));

        vm.SortCommand.Execute("Cpu");

        vm.Processes[0].Name.Should().Be("b");
        vm.SortDescending.Should().BeTrue();
        vm.IsSortedByCpu.Should().BeTrue();
        vm.IsSortedByName.Should().BeFalse();

        vm.SortCommand.Execute("Cpu");

        vm.SortDescending.Should().BeFalse();
        vm.Processes[0].Name.Should().Be("a");
    }

    [Fact]
    public void ChoosingMemory_SortsLargestFirst()
    {
        var vm = Make(Proc(1, "a", mem: 10), Proc(2, "b", mem: 99));

        vm.SortCommand.Execute("Memory");

        vm.Processes[0].Name.Should().Be("b");
        vm.IsSortedByMemory.Should().BeTrue();
    }

    [Fact]
    public void ChoosingNameAfterCpu_GoesBackToAToZ()
    {
        var vm = Make(Proc(1, "b", cpu: 1), Proc(2, "a", cpu: 9));
        vm.SortCommand.Execute("Cpu");

        vm.SortCommand.Execute("Name");

        vm.SortDescending.Should().BeFalse();
        vm.Processes[0].Name.Should().Be("a");
        vm.IsSortedByName.Should().BeTrue();
    }

    [Fact]
    public void DismissKillError_ClearsTheBanner()
    {
        var vm = Make();
        vm.KillError = "Could not end it.";

        vm.DismissKillErrorCommand.Execute(null);

        vm.KillError.Should().BeNull();
    }

    [Fact]
    public void ShowNoMatches_FollowsTheSearchAndLoadingState()
    {
        var vm = new TaskManagerViewModel(new ConnectionViewModel());
        vm.SearchText = "zzz";
        vm.ShowNoMatches.Should().BeFalse("the first list has not arrived yet");

        vm.ApplyReceivedProcessList(new List<ProcessInfo> { Proc(1, "notepad") });
        vm.ShowNoMatches.Should().BeTrue();

        vm.SearchText = "";
        vm.ShowNoMatches.Should().BeFalse();
    }

    [Fact]
    public void OpenFileLocation_IsDisabledWithoutAPath_AndRevealsTheFilePath()
    {
        var vm = Make();
        string? revealed = null;
        vm.RevealInFileManager = p => revealed = p;

        vm.OpenFileLocationCommand.CanExecute(Proc(1, "idle")).Should().BeFalse();

        var notepad = Proc(2, "notepad", path: @"C:\Windows\notepad.exe");
        vm.OpenFileLocationCommand.CanExecute(notepad).Should().BeTrue();
        vm.OpenFileLocationCommand.Execute(notepad);

        revealed.Should().Be(@"C:\Windows\notepad.exe");
    }

    [Fact]
    public async Task CopyFilePath_PutsThePathOnTheClipboard()
    {
        var vm = Make();
        string? copied = null;
        vm.CopyToClipboardAsync = text => { copied = text; return Task.CompletedTask; };

        await vm.CopyFilePathCommand.ExecuteAsync(Proc(2, "notepad", path: @"C:\Windows\notepad.exe"));

        copied.Should().Be(@"C:\Windows\notepad.exe");
    }
}
