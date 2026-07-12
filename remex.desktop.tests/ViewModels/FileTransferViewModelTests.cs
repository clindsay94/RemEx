using System.Linq;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class FileTransferViewModelTests
{
    private static FileEntry File(string name, long size = 0, long modified = 0) =>
        new() { Name = name, IsDirectory = false, SizeBytes = size, ModifiedUnixMs = modified };

    private static FileEntry Dir(string name) =>
        new() { Name = name, IsDirectory = true };

    private static FileEntry Parent() => new() { Name = "..", IsDirectory = true };

    // ─── Sort ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SortEntries_ByNameAscending_PutsParentFirstThenDirsThenFiles()
    {
        var entries = new[] { File("b"), Dir("z"), File("a"), Parent() };

        var sorted = FileTransferViewModel.SortEntries(entries, FileSortField.Name, descending: false);

        sorted.Select(e => e.Name).Should().ContainInOrder("..", "z", "a", "b");
    }

    [Fact]
    public void SortEntries_ByNameDescending_KeepsParentAndDirsFirst()
    {
        var entries = new[] { File("a"), File("b"), Dir("z"), Parent() };

        var sorted = FileTransferViewModel.SortEntries(entries, FileSortField.Name, descending: true);

        sorted.Select(e => e.Name).Should().ContainInOrder("..", "z", "b", "a");
    }

    [Fact]
    public void SortEntries_BySizeAscending_OrdersFilesBySize()
    {
        var entries = new[] { File("big", size: 300), File("small", size: 10), File("mid", size: 100) };

        var sorted = FileTransferViewModel.SortEntries(entries, FileSortField.Size, descending: false);

        sorted.Select(e => e.Name).Should().ContainInOrder("small", "mid", "big");
    }

    [Fact]
    public void SortEntries_ByModifiedDescending_OrdersFilesNewestFirst()
    {
        var entries = new[] { File("old", modified: 100), File("new", modified: 300), File("mid", modified: 200) };

        var sorted = FileTransferViewModel.SortEntries(entries, FileSortField.Modified, descending: true);

        sorted.Select(e => e.Name).Should().ContainInOrder("new", "mid", "old");
    }

    // ─── Breadcrumbs ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildBreadcrumbSegments_AtRoot_ReturnsOnlyRoot()
    {
        var segments = FileTransferViewModel.BuildBreadcrumbSegments("/", "Home");

        segments.Should().ContainSingle();
        segments[0].Label.Should().Be("Home");
        segments[0].Path.Should().Be("/");
    }

    [Fact]
    public void BuildBreadcrumbSegments_NestedPath_BuildsCumulativeSegments()
    {
        var segments = FileTransferViewModel.BuildBreadcrumbSegments("/docs/img", "Home");

        segments.Select(s => s.Label).Should().ContainInOrder("Home", "docs", "img");
        segments.Select(s => s.Path).Should().ContainInOrder("/", "/docs", "/docs/img");
    }

    [Fact]
    public void BuildBreadcrumbSegments_NormalizesSeparatorsAndLeadingSlash()
    {
        var segments = FileTransferViewModel.BuildBreadcrumbSegments("docs\\img", "Home");

        segments.Select(s => s.Path).Should().ContainInOrder("/", "/docs", "/docs/img");
    }

    // ─── Selection ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetSelectedEntries_WithMultiple_ReportsMultiSelection()
    {
        using var connection = new ConnectionViewModel(null, null, null);
        using var vm = new FileTransferViewModel(connection);

        vm.SetSelectedEntries(new[] { File("a"), File("b") });

        vm.SelectionCount.Should().Be(2);
        vm.HasMultiSelection.Should().BeTrue();
    }

    [Fact]
    public void SetSelectedEntries_ExcludesParentPlaceholder()
    {
        using var connection = new ConnectionViewModel(null, null, null);
        using var vm = new FileTransferViewModel(connection);

        vm.SetSelectedEntries(new[] { Parent(), File("a") });

        vm.SelectionCount.Should().Be(1);
        vm.HasMultiSelection.Should().BeFalse();
    }

    [Fact]
    public void SelectionCount_FallsBackToSingleHighlight_WhenNoMultiSelection()
    {
        using var connection = new ConnectionViewModel(null, null, null);
        using var vm = new FileTransferViewModel(connection);

        vm.SetSelectedEntries(System.Array.Empty<FileEntry>());
        vm.SelectedRemoteEntry = File("solo");

        vm.SelectionCount.Should().Be(1);
    }

    [Fact]
    public void SortByCommand_TogglesDirectionWhenSameFieldReselected()
    {
        using var connection = new ConnectionViewModel(null, null, null);
        using var vm = new FileTransferViewModel(connection);

        vm.SortField.Should().Be(FileSortField.Name);
        vm.SortDescending.Should().BeFalse();

        vm.SortByCommand.Execute("name");
        vm.SortDescending.Should().BeTrue();

        vm.SortByCommand.Execute("size");
        vm.SortField.Should().Be(FileSortField.Size);
        vm.SortDescending.Should().BeFalse();
    }
}
