namespace Remex.Desktop.Services;

/// <summary>
/// Finds the sample under the pointer for a sparkline's hover crosshair (RemEx-hf90).
/// </summary>
/// <remarks>
/// A 542-line <c>SparklineControl</c> is currently doing wallpaper duty — rendered at opacity 0.2
/// behind the number, with no axis, range or hover readout. Turning it into something readable
/// starts with being able to say which sample the pointer is over.
/// </remarks>
public static class SparklineHitTest
{
    /// <summary>
    /// The index of the sample nearest <paramref name="pointerX"/>, or -1 when there is nothing to
    /// point at.
    /// </summary>
    /// <param name="pointerX">Pointer position in the control's pixel space.</param>
    /// <param name="sampleCount">How many samples are plotted.</param>
    /// <param name="width">Plot width in pixels.</param>
    /// <remarks>
    /// <para>
    /// **NEAREST, NOT FLOOR.** The obvious implementation is <c>(int)(pointerX / step)</c>, and it
    /// is wrong in a way that feels like lag: it biases every lookup LEFT, so the crosshair snaps to
    /// the sample the pointer has just passed rather than the one it is closest to. The user reads
    /// it as the chart trailing their mouse. Rounding to the nearest gridline instead makes the
    /// snap symmetric, which is what makes it feel attached to the pointer.
    /// </para>
    /// <para>
    /// A pointer outside the plot CLAMPS to the first or last sample rather than returning -1.
    /// Hovering a pixel beyond the right edge of a chart should read the last value, not blank the
    /// readout — a readout that flickers empty at the edges looks like the chart has holes in it.
    /// </para>
    /// </remarks>
    public static int NearestIndex(double pointerX, int sampleCount, double width)
    {
        if (sampleCount <= 0) return -1;
        if (sampleCount == 1) return 0;

        // A zero-width plot has no gridlines to be near. Dividing by it yields infinity, and the
        // cast that follows is undefined rather than merely wrong.
        if (width <= 0) return 0;

        var step = width / (sampleCount - 1);
        var raw = pointerX / step;

        // Math.Round rather than a cast: the cast truncates toward zero, which is the left bias.
        // MidpointRounding.AwayFromZero so a pointer exactly between two samples resolves the same
        // way every time rather than by banker's rounding, which would alternate and make the
        // crosshair jitter on a slow drag.
        var index = (int)Math.Round(raw, MidpointRounding.AwayFromZero);

        return Math.Clamp(index, 0, sampleCount - 1);
    }

    /// <summary>
    /// The x position the crosshair should be drawn at for <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// Returned rather than reusing the pointer's own x, so the crosshair sits ON the sample it is
    /// reporting. Drawing it at the pointer would show a line between two points while the readout
    /// names one of them, which invites the user to believe the value belongs to where the line is.
    /// </remarks>
    public static double SnappedX(int index, int sampleCount, double width)
    {
        if (sampleCount <= 1 || width <= 0) return 0;

        var step = width / (sampleCount - 1);
        return Math.Clamp(index, 0, sampleCount - 1) * step;
    }
}
