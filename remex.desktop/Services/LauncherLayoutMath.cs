using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// Pure geometry helpers for the app launcher's pointer-capture drag-to-rearrange grid.
/// Deliberately has zero Avalonia visual-tree dependencies (only <see cref="Point"/>, a value
/// type) so it is unit-testable independent of any live UI.
/// </summary>
public static class LauncherLayoutMath
{
    /// <summary>
    /// Maps a pointer position (relative to the WrapPanel hosting the launcher grid) to the
    /// destination item index, assuming a fixed <paramref name="itemWidth"/> x
    /// <paramref name="itemHeight"/> grid (no hit-testing needed). Clamps to the last valid index
    /// so dropping in the trailing empty space of the final row appends instead of throwing.
    /// </summary>
    public static int IndexFromPoint(Point p, double panelWidth, double itemWidth, double itemHeight, int itemCount)
    {
        if (itemCount <= 0) return -1;

        var cols = Math.Max(1, (int)(panelWidth / itemWidth));
        var col = Math.Clamp((int)(p.X / itemWidth), 0, cols - 1);
        var row = Math.Max(0, (int)(p.Y / itemHeight));
        return Math.Min(row * cols + col, itemCount - 1);
    }
}

/// <summary>
/// Pure reordering logic extracted from <c>AppLauncherViewModel</c> (sort + reindex) so it can be
/// unit tested without constructing the full view-model graph, which requires a live
/// <c>ConnectionViewModel</c> and <c>ShellViewModel</c>.
/// </summary>
public static class LauncherOrdering
{
    /// <summary>Returns a new list with each entry's <see cref="AppEntry.Order"/> set to its index.</summary>
    public static List<AppEntry> Reindex(IList<AppEntry> entries)
    {
        var result = new List<AppEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            result.Add(entries[i] with { Order = i });
        }

        return result;
    }

    /// <summary>
    /// Sorts by <see cref="AppEntry.DisplayName"/>, culture-insensitively. <paramref name="direction"/>
    /// is <c>"desc"</c> for Z→A; anything else (including <c>"asc"</c>) sorts A→Z.
    /// </summary>
    public static List<AppEntry> SortByName(IEnumerable<AppEntry> entries, string direction)
    {
        var ordered = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase)
            ? entries.OrderByDescending(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            : entries.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        return ordered.ToList();
    }
}
