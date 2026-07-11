using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Tests;

/// <summary>
/// Tests for the app launcher's ordering logic. <see cref="LauncherOrdering"/> (sort + reindex)
/// was extracted from <c>AppLauncherViewModel.PersistOrderAsync</c>/<c>SortByNameAsync</c>
/// specifically so it is testable without constructing the full view-model graph, which needs a
/// live <c>ConnectionViewModel</c> and <c>ShellViewModel</c>. <see cref="AddProgramViewModel"/>'s
/// edit-mode Id/Order preservation IS light enough to construct directly — its only dependency is
/// <see cref="IIconExtractionService"/>.
/// </summary>
public sealed class AppLauncherOrderingTests
{
    private static AppEntry MakeEntry(string name, int order, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, $@"C:\apps\{name}.exe", "#4A3AFF", null, order);

    // ═══════════════ LauncherOrdering.SortByName ═══════════════

    [Fact]
    public void SortByName_Ascending_OrdersAlphabetically()
    {
        var entries = new List<AppEntry>
        {
            MakeEntry("Zebra", 0),
            MakeEntry("apple", 1),
            MakeEntry("Mango", 2),
        };

        var sorted = LauncherOrdering.SortByName(entries, "asc");

        Assert.Equal(new[] { "apple", "Mango", "Zebra" }, sorted.Select(e => e.DisplayName));
    }

    [Fact]
    public void SortByName_Descending_OrdersReverseAlphabetically()
    {
        var entries = new List<AppEntry>
        {
            MakeEntry("Zebra", 0),
            MakeEntry("apple", 1),
            MakeEntry("Mango", 2),
        };

        var sorted = LauncherOrdering.SortByName(entries, "desc");

        Assert.Equal(new[] { "Zebra", "Mango", "apple" }, sorted.Select(e => e.DisplayName));
    }

    [Fact]
    public void SortByName_IsCultureInsensitive_MixedCaseSortsTogether()
    {
        // "apple" (lowercase) must sort next to "Apricot" (uppercase A), not after every
        // uppercase-first entry the way an ordinal comparison would place it.
        var entries = new List<AppEntry>
        {
            MakeEntry("banana", 0),
            MakeEntry("Apricot", 1),
            MakeEntry("apple", 2),
        };

        var sorted = LauncherOrdering.SortByName(entries, "asc");

        Assert.Equal(new[] { "apple", "Apricot", "banana" }, sorted.Select(e => e.DisplayName));
    }

    // ═══════════════ LauncherOrdering.Reindex ═══════════════

    [Fact]
    public void Reindex_SetsOrderToPositionalIndex()
    {
        var entries = new List<AppEntry>
        {
            MakeEntry("First", 99),
            MakeEntry("Second", 3),
            MakeEntry("Third", -1),
        };

        var reindexed = LauncherOrdering.Reindex(entries);

        Assert.Equal(new[] { 0, 1, 2 }, reindexed.Select(e => e.Order));

        // Reindex must only touch Order — Id and DisplayName must survive untouched.
        Assert.Equal(entries[0].Id, reindexed[0].Id);
        Assert.Equal("First", reindexed[0].DisplayName);
    }

    [Fact]
    public void Reindex_EmptyList_ReturnsEmptyList()
    {
        var reindexed = LauncherOrdering.Reindex(new List<AppEntry>());

        Assert.Empty(reindexed);
    }

    // ═══════════════ AddProgramViewModel edit-mode preservation ═══════════════

    [Fact]
    public async Task SaveAsync_InEditMode_PreservesIdAndOrder_ButUpdatesEditedFields()
    {
        var original = MakeEntry("Original Name", order: 4);
        var vm = new AddProgramViewModel(new IconExtractionService());

        AppEntry? saved = null;
        vm.OnSaveRequested = entry =>
        {
            saved = entry;
            return Task.CompletedTask;
        };

        vm.LoadForEdit(original);

        // Simulate the user renaming and recoloring, but NOT touching TargetPath.
        vm.DisplayName = "Renamed";
        vm.HexColor = "#00FF00";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(original.Id, saved!.Id);
        Assert.Equal(original.Order, saved.Order);
        Assert.Equal(original.TargetPath, saved.TargetPath);
        // SaveAsync coalesces a null IconBase64 to string.Empty (see AddProgramViewModel.SaveAsync) —
        // that's the documented contract, not a bug, so the expectation must match it.
        Assert.Equal(original.IconBase64 ?? string.Empty, saved.IconBase64);
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal("#00FF00", saved.HexColor);
    }

    [Fact]
    public void LoadForEdit_SeedsFieldsWithoutReExtractingIcon()
    {
        // OnTargetPathChanged would normally try to extract an icon from disk; LoadForEdit must
        // suppress that (via the _isSeeding guard) since the path points to nothing on this test
        // machine and the existing icon should be preserved as-is.
        var original = MakeEntry("Existing", order: 1) with { IconBase64 = "preserved-icon-base64" };
        var vm = new AddProgramViewModel(new IconExtractionService());

        vm.LoadForEdit(original);

        Assert.True(vm.IsEditMode);
        Assert.Equal(original.DisplayName, vm.DisplayName);
        Assert.Equal(original.TargetPath, vm.TargetPath);
        Assert.Equal(original.HexColor, vm.HexColor);
        Assert.Equal("preserved-icon-base64", vm.IconBase64);
    }
}
