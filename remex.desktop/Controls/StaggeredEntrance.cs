using System.Collections.Generic;

namespace Remex.Desktop.Controls;

/// <summary>
/// Once-per-process gate for a view's staggered entrance animation (RemEx-dnfq0).
/// </summary>
/// <remarks>
/// PageHost's <c>DataTemplate</c>s rebuild the view instance on every navigation
/// (ShellView.axaml:829), so a new <see cref="Views.HomeView"/> is constructed each time the
/// dashboard is opened. Without this gate the entrance would replay on every visit, which the
/// bead calls out as reading like repetition rather than polish, not just first paint. Keying by
/// a caller-supplied string (typically <c>nameof(HomeView)</c>) lets one process-lifetime gate
/// serve every view that reuses this helper.
/// </remarks>
internal static class StaggeredEntrance
{
    /// <summary>Style class applied to a container to opt its children into the entrance styles.</summary>
    internal const string Class = "entrance";

    private static readonly HashSet<string> Played = new();

    // Production callers are UI-thread only, but xunit runs test classes in parallel and every
    // view that adopts this gate brings its own test class that resets and asserts on it. The
    // lock costs nothing on the UI thread and keeps that harness from becoming a load-sensitive
    // flake once RemEx-alwfa.2 adds the drawer-nav, Remote and Settings callers.
    private static readonly object Gate = new();

    /// <summary>
    /// Returns true the first time <paramref name="key"/> is asked for in this process and
    /// records it as played; returns false on every later call for that key. Under
    /// <paramref name="reducedMotion"/> it always returns false and never records the key, so a
    /// later call once motion is no longer reduced still gets to play once.
    /// </summary>
    internal static bool ShouldPlay(string key, bool reducedMotion)
    {
        if (reducedMotion)
        {
            return false;
        }

        lock (Gate)
        {
            return Played.Add(key);
        }
    }

    /// <summary>Clears played-key state. Test-only.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Played.Clear();
        }
    }
}
