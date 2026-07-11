using Avalonia;
using Remex.Desktop.Services;

namespace Remex.Desktop.Tests;

/// <summary>
/// Tests for <see cref="LauncherLayoutMath.IndexFromPoint"/> — the pure geometry helper used to
/// map a drag pointer position to a destination index in the fixed 160x160 launcher grid.
/// </summary>
public sealed class LauncherLayoutMathTests
{
    private const double ItemWidth = 160;
    private const double ItemHeight = 160;

    [Fact]
    public void IndexFromPoint_EmptyGrid_ReturnsNegativeOne()
    {
        var result = LauncherLayoutMath.IndexFromPoint(new Point(10, 10), panelWidth: 800, ItemWidth, ItemHeight, itemCount: 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void IndexFromPoint_SingleColumn_MapsRowsSequentially()
    {
        // panelWidth == itemWidth => exactly 1 column; index should just be the row.
        var top = LauncherLayoutMath.IndexFromPoint(new Point(50, 10), panelWidth: ItemWidth, ItemWidth, ItemHeight, itemCount: 5);
        var third = LauncherLayoutMath.IndexFromPoint(new Point(50, 330), panelWidth: ItemWidth, ItemWidth, ItemHeight, itemCount: 5);

        Assert.Equal(0, top);
        Assert.Equal(2, third);
    }

    [Fact]
    public void IndexFromPoint_FiveColumns_MapsRowAndColumn()
    {
        // panelWidth fits exactly 5 columns (5 * 160 = 800).
        var point = new Point(x: 2 * ItemWidth + 10, y: 1 * ItemHeight + 10); // row 1, col 2
        var index = LauncherLayoutMath.IndexFromPoint(point, panelWidth: 5 * ItemWidth, ItemWidth, ItemHeight, itemCount: 20);

        // row 1 * 5 cols + col 2 = 7
        Assert.Equal(7, index);
    }

    [Fact]
    public void IndexFromPoint_PointBeyondPanelWidth_ClampsToLastColumn()
    {
        // 5 columns available (0..4); a point far to the right must clamp to column 4, not overflow.
        var point = new Point(x: 10 * ItemWidth, y: 0);
        var index = LauncherLayoutMath.IndexFromPoint(point, panelWidth: 5 * ItemWidth, ItemWidth, ItemHeight, itemCount: 20);

        Assert.Equal(4, index);
    }

    [Fact]
    public void IndexFromPoint_BeyondLastRow_ClampsToItemCountMinusOne()
    {
        // Only 3 items exist, but the pointer is dropped in the trailing empty space of a much
        // later row — must clamp to the last valid index (append), never throw or overflow.
        var point = new Point(x: 4 * ItemWidth, y: 10 * ItemHeight);
        var index = LauncherLayoutMath.IndexFromPoint(point, panelWidth: 5 * ItemWidth, ItemWidth, ItemHeight, itemCount: 3);

        Assert.Equal(2, index);
    }

    [Fact]
    public void IndexFromPoint_NegativeCoordinates_ClampToOrigin()
    {
        // A pointer slightly outside the panel's top-left (e.g. mid-drag jitter) must not go negative.
        var point = new Point(x: -20, y: -20);
        var index = LauncherLayoutMath.IndexFromPoint(point, panelWidth: 5 * ItemWidth, ItemWidth, ItemHeight, itemCount: 20);

        Assert.Equal(0, index);
    }
}
