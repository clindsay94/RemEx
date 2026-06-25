using Avalonia.Media;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;

namespace Remex.Desktop.Services;

/// <summary>
/// Generates a full Material 3 tonal scheme from a single seed color using
/// the MaterialColorUtilities library (albi005 port).
/// </summary>
public static class DynamicColorGenerator
{
    public record M3Palette(
        Color Primary,
        Color OnPrimary,
        Color PrimaryContainer,
        Color OnPrimaryContainer,
        Color Secondary,
        Color OnSecondary,
        Color SecondaryContainer,
        Color OnSecondaryContainer,
        Color Tertiary,
        Color OnTertiary,
        Color Surface,
        Color SurfaceVariant,
        Color SurfaceContainerLow,
        Color SurfaceContainer,
        Color SurfaceContainerHigh,
        Color OnSurface,
        Color OnSurfaceVariant,
        Color Outline,
        Color OutlineVariant,
        Color Error,
        Color OnError);

    public static M3Palette Generate(Color seed, string variant = "TonalSpot", bool isDark = true, double contrast = 0.0)
    {
        uint argb = ((uint)seed.A << 24) | ((uint)seed.R << 16) | ((uint)seed.G << 8) | seed.B;

        var style = variant switch
        {
            "Vibrant"    => Style.Vibrant,
            "Expressive" => Style.Expressive,
            "Rainbow"    => Style.Rainbow,
            "FruitSalad" => Style.FruitSalad,
            "Content"    => Style.Content,
            "Spritz"     => Style.Spritz,
            _            => Style.TonalSpot,
        };

        var core = new CorePalette();
        core.Fill(argb, style);

        Scheme<uint> scheme = isDark
            ? new DarkSchemeMapper().Map(core)
            : new LightSchemeMapper().Map(core);

        return new M3Palette(
            Primary:              ToColor(scheme.Primary),
            OnPrimary:            ToColor(scheme.OnPrimary),
            PrimaryContainer:     ToColor(scheme.PrimaryContainer),
            OnPrimaryContainer:   ToColor(scheme.OnPrimaryContainer),
            Secondary:            ToColor(scheme.Secondary),
            OnSecondary:          ToColor(scheme.OnSecondary),
            SecondaryContainer:   ToColor(scheme.SecondaryContainer),
            OnSecondaryContainer: ToColor(scheme.OnSecondaryContainer),
            Tertiary:             ToColor(scheme.Tertiary),
            OnTertiary:           ToColor(scheme.OnTertiary),
            Surface:              ToColor(scheme.Surface),
            SurfaceVariant:       ToColor(scheme.SurfaceVariant),
            SurfaceContainerLow:  ToColor(scheme.SurfaceContainerLow),
            SurfaceContainer:     ToColor(scheme.SurfaceContainer),
            SurfaceContainerHigh: ToColor(scheme.SurfaceContainerHigh),
            OnSurface:            ToColor(scheme.OnSurface),
            OnSurfaceVariant:     ToColor(scheme.OnSurfaceVariant),
            Outline:              ToColor(scheme.Outline),
            OutlineVariant:       ToColor(scheme.OutlineVariant),
            Error:                ToColor(scheme.Error),
            OnError:              ToColor(scheme.OnError));
    }

    private static Color ToColor(uint argb) =>
        Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8)  & 0xFF),
            (byte)(argb         & 0xFF));
}
