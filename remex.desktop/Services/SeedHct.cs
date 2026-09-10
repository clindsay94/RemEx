using Avalonia.Media;
using MaterialColorUtilities.ColorAppearance;

namespace Remex.Desktop.Services;

/// <summary>
/// The seed colour expressed in HCT — hue, chroma and tone — which is the space the Palette Studio
/// edits in and the space <see cref="DynamicColorGenerator"/> derives every other role from.
/// </summary>
/// <remarks>
/// <para>
/// A SEPARATE, DEPENDENCY-FREE TYPE ON PURPOSE. <c>CustomizationViewModel</c> cannot be constructed
/// in a test — it wants a shell, a layout service and a theme service — so any colour maths living
/// on it can only ever be asserted by reading its source text. Everything here is static and takes
/// primitives, which is what lets <c>SeedHctTests</c> actually run the conversion.
/// </para>
/// <para>
/// CHROMA IS A REQUEST, NOT A GUARANTEE. Most hue/tone pairs cannot reach high chroma in sRGB, and
/// <see cref="Hct.From"/> answers with the closest colour it can render rather than failing. So
/// <see cref="FromColor"/> of a <see cref="ToColor"/> result returns the ACHIEVED chroma, which may
/// be lower than the one asked for. That is the same behaviour Android's picker has
/// (<c>Theme.kt:511</c>) and it is why the round-trip test asserts stability across a second pass
/// rather than equality on the first.
/// </para>
/// </remarks>
public static class SeedHct
{
    /// <summary>
    /// The top of the chroma axis the studio offers. No sRGB colour reaches much past this, so a
    /// higher ceiling would be a slider region where dragging changes nothing.
    /// </summary>
    public const double MaxChroma = 120.0;

    /// <summary>Splits a seed colour into its hue (0–360), chroma and tone (0–100).</summary>
    public static (double Hue, double Chroma, double Tone) FromColor(Color seed)
    {
        var hct = Hct.FromInt(ToArgb(seed));
        return (NormalizeHue(hct.Hue), hct.Chroma, hct.Tone);
    }

    /// <summary>
    /// Builds the closest opaque sRGB colour to the requested hue, chroma and tone. Inputs outside
    /// their ranges are clamped rather than rejected — this sits under a slider drag, where a throw
    /// would take the window with it.
    /// </summary>
    public static Color ToColor(double hue, double chroma, double tone)
    {
        var hct = Hct.From(
            NormalizeHue(hue),
            Math.Clamp(chroma, 0.0, MaxChroma),
            Math.Clamp(tone, 0.0, 100.0));

        var argb = hct.ToInt();
        return Color.FromRgb((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
    }

    /// <summary>The <c>#RRGGBB</c> form of <see cref="ToColor"/>, which is how the seed is persisted.</summary>
    public static string ToHex(double hue, double chroma, double tone)
    {
        var color = ToColor(hue, chroma, tone);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// The chroma of a persisted seed string, or <paramref name="fallback"/> when it will not parse.
    /// </summary>
    /// <remarks>
    /// WHAT MAKES <c>CustomizationSettings.ThemeSeedChroma</c> TRUE RATHER THAN STALE. The field is
    /// Android's vibrancy axis and had no desktop writer at all, so it kept whatever was last loaded
    /// while the seed moved underneath it. Writing the seed's own chroma means
    /// <c>Hct.From(hue, ThemeSeedChroma, tone)</c> reproduces the seed exactly on either platform,
    /// which is the parity RemEx-ndhlv asks for.
    /// </remarks>
    public static double ChromaOf(string? hex, double fallback)
        => Color.TryParse(hex, out var color) ? FromColor(color).Chroma : fallback;

    /// <summary>Wraps a hue into [0, 360) so a wheel that has been dragged past the seam still reads sanely.</summary>
    public static double NormalizeHue(double hue)
    {
        if (double.IsNaN(hue) || double.IsInfinity(hue)) return 0.0;
        var wrapped = hue % 360.0;
        return wrapped < 0 ? wrapped + 360.0 : wrapped;
    }

    private static uint ToArgb(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
