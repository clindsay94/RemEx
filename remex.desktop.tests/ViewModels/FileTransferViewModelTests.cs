using System.IO;
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

    // ─── Folder upload: the remote directory shape (RemEx-0xves) ──────────────
    //
    // Uploading a folder to a PHONE could not work at all. The desktop built remote paths like
    // "<current>/photos/2024/img.jpg" and enqueued a transfer per file, but nothing ever created
    // "photos" or "2024" on the far side. The PC host creates a missing parent on write, so a PC
    // destination hid it; the Android host resolves the parent and refuses when it is not there, so
    // every file in the folder came back "Cannot write to destination folder."

    private static string Local(params string[] segments) =>
        string.Join(Path.DirectorySeparatorChar, segments);

    [Fact]
    public void PlanRemoteFolderCreation_CreatesTheTargetFolderEvenWhenTheTreeIsFlat()
    {
        var plan = FileTransferViewModel.PlanRemoteFolderCreation(
            Local("C:", "src", "photos"),
            [],
            [Local("C:", "src", "photos", "a.jpg"), Local("C:", "src", "photos", "b.jpg")],
            "/shared/photos");

        plan.Should().Equal("/shared/photos");
    }

    [Fact]
    public void PlanRemoteFolderCreation_OrdersParentsBeforeChildren()
    {
        var root = Local("C:", "src", "photos");

        var plan = FileTransferViewModel.PlanRemoteFolderCreation(
            root,
            // Deliberately deepest-first, which is an order a walk can genuinely produce.
            [Local(root, "2024", "may", "raw"), Local(root, "2024", "may"), Local(root, "2024")],
            [],
            "/shared/photos");

        plan.Should().Equal(
            "/shared/photos",
            "/shared/photos/2024",
            "/shared/photos/2024/may",
            "/shared/photos/2024/may/raw");
    }

    /// <summary>
    /// An empty folder is the one part of a tree's shape that no file can imply, which is why the
    /// directory walk is unioned in rather than the file parents being trusted alone.
    /// </summary>
    [Fact]
    public void PlanRemoteFolderCreation_KeepsAnEmptyDirectory()
    {
        var root = Local("C:", "src", "photos");

        var plan = FileTransferViewModel.PlanRemoteFolderCreation(
            root,
            [Local(root, "empty")],
            [Local(root, "a.jpg")],
            "/shared/photos");

        plan.Should().Contain("/shared/photos/empty");
    }

    /// <summary>
    /// The mirror case: a directory the walk could not read, whose files were still reached through
    /// it. Its parents come from the file paths.
    /// </summary>
    [Fact]
    public void PlanRemoteFolderCreation_DerivesDirectoriesFromFilePathsToo()
    {
        var root = Local("C:", "src", "photos");

        var plan = FileTransferViewModel.PlanRemoteFolderCreation(
            root,
            [],
            [Local(root, "2024", "may", "img.jpg")],
            "/shared/photos");

        plan.Should().Equal(
            "/shared/photos",
            "/shared/photos/2024",
            "/shared/photos/2024/may");
    }

    /// <summary>
    /// Both sources name the same folders; asking for each one once is the point of the union.
    /// </summary>
    [Fact]
    public void PlanRemoteFolderCreation_AsksForEachFolderOnce()
    {
        var root = Local("C:", "src", "photos");

        var plan = FileTransferViewModel.PlanRemoteFolderCreation(
            root,
            [Local(root, "2024"), Local(root, "2024", "may")],
            [Local(root, "2024", "may", "a.jpg"), Local(root, "2024", "may", "b.jpg"), Local(root, "2024", "c.jpg")],
            "/shared/photos");

        plan.Should().OnlyHaveUniqueItems();
        plan.Should().Equal(
            "/shared/photos",
            "/shared/photos/2024",
            "/shared/photos/2024/may");
    }
}
