using Avalonia;

namespace Remex.Desktop.Services;

/// <summary>
/// Where a tray-anchored window sits, in the screen's own units (RemEx-q7ak).
/// </summary>
/// <remarks>
/// <para>
/// **WorkingArea IS PHYSICAL PIXELS AND Width IS LOGICAL UNITS.** Subtracting a logical size from a
/// physical edge and scaling the RESULT lands correctly at 100% and drifts further off the
/// bottom-right corner the higher the display scaling goes — the window size has to be scaled INTO
/// the screen's space instead. The flyout did it the wrong way round; the balloon, written later
/// against the same corner, did it correctly, and two copies of arithmetic where one is already
/// wrong is the case for having one copy.
/// </para>
/// <para>
/// A pure function because that is the only way to test it: both callers are Avalonia windows that a
/// headless assembly cannot construct, and the failure is invisible at 100% scaling — which is the
/// setting a developer's own machine is most likely to be on.
/// </para>
/// </remarks>
public static class TrayPlacement
{
    /// <summary>Parks a window just inside the bottom-right of a work area.</summary>
    /// <param name="workingArea">The screen's work area, in PHYSICAL pixels.</param>
    /// <param name="widthLogical">The window's width, in LOGICAL units.</param>
    /// <param name="heightLogical">The window's height, in LOGICAL units.</param>
    /// <param name="scaling">Physical pixels per logical unit — 1.0 at 100%, 1.5 at 150%.</param>
    /// <param name="marginLogical">The gap from the corner, in LOGICAL units, so it looks the
    /// same size at every scaling rather than shrinking as the display gets denser.</param>
    public static PixelPoint BottomRight(
        PixelRect workingArea, double widthLogical, double heightLogical,
        double scaling, double marginLogical) =>
        new(
            (int)(workingArea.Right - ((widthLogical + marginLogical) * scaling)),
            (int)(workingArea.Bottom - ((heightLogical + marginLogical) * scaling)));
}
