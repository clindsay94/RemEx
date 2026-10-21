using System;

namespace Remex.Desktop.Views;

/// <summary>
/// Where the drawer's nav list should be scrolled when it opens (RemEx-172ba): the first item pins
/// the list to the very top, any other item is revealed with the smallest move.
/// </summary>
internal static class NavListScroll
{
    public static double OffsetToReveal(double itemTop, double itemHeight, double viewportHeight, double currentOffset, bool isFirstItem)
    {
        if (isFirstItem || itemTop <= 0)
            return 0;

        if (itemTop < currentOffset)
            return itemTop;

        var itemBottom = itemTop + itemHeight;
        if (itemBottom > currentOffset + viewportHeight)
            return Math.Max(0, itemBottom - viewportHeight);

        return currentOffset;
    }
}
