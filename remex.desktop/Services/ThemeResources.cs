using Avalonia;
using Avalonia.Media;

namespace Remex.Desktop.Services;

/// <summary>
/// Looks a theme resource up in the ACTIVE theme, degrading to a caller-supplied fallback when the
/// key is absent. The one place code-built UI should get its colours from.
/// </summary>
/// <remarks>
/// <para>
/// The app ships four themes with very different contrast and background treatments (CyberNOC,
/// Monolith, SolarFlare, BaseDarkGlass), so a colour literal compiled into a control is only ever
/// right for one of them — a dark slab drawn on SolarFlare being the usual symptom. All 49 theme
/// keys are defined in all four theme files, so a lookup that succeeds in one succeeds in all.
/// </para>
/// <para>
/// Three details are load-bearing and were established by
/// <c>LogLevelToBrushConverter</c> / <c>SensorColorConverter</c> before this helper existed:
/// <list type="bullet">
/// <item><description>
/// <c>TryGetResource</c> rather than the indexer, so a missing key degrades to the fallback instead
/// of throwing somewhere a throw is unrecoverable — inside a converter or a <c>Render</c> override.
/// (<c>TryFindResource</c> does not exist on <see cref="Application"/> in this Avalonia version.)
/// </description></item>
/// <item><description>
/// The variant is passed explicitly, so the lookup follows the app's ACTUAL theme variant rather
/// than whatever the resource host would default to.
/// </description></item>
/// <item><description>
/// Resolved PER CALL, never cached in a static field. A static readonly brush is captured once at
/// type-initialisation and then survives every theme switch for the life of the process, which is
/// exactly the bug this helper exists to remove.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <see cref="Color"/> exists alongside <see cref="Brush"/> because several call sites apply their
/// own opacity at construction (a minimap plate at 0.92, a viewport fill at 0.10). Those need the
/// raw colour so they can re-apply that alpha themselves; handing them a brush would either lose
/// the opacity or force the theme to ship a pre-alphaed variant of every colour.
/// </para>
/// </remarks>
public static class ThemeResources
{
    /// <summary>Resolves <paramref name="key"/> as an <see cref="IBrush"/>, or <paramref name="fallback"/>.</summary>
    public static IBrush Brush(string key, IBrush fallback)
        => TryGet(key) is IBrush brush ? brush : fallback;

    /// <summary>
    /// Resolves <paramref name="key"/> as a <see cref="Avalonia.Media.Color"/>, or
    /// <paramref name="fallback"/>. Accepts a key that resolves to either a raw colour or a
    /// <see cref="ISolidColorBrush"/>, since the theme files define both forms of most colours
    /// (<c>AccentPrimary</c> and <c>AccentPrimaryBrush</c>) and callers should not have to care.
    /// </summary>
    public static Color Color(string key, Color fallback)
        => TryGet(key) switch
        {
            Color color => color,
            ISolidColorBrush brush => brush.Color,
            _ => fallback,
        };

    /// <summary>
    /// Resolves <paramref name="key"/> and discards whatever alpha the theme's own colour carries,
    /// so the caller can apply its own opacity — or none — from a known-opaque base.
    /// </summary>
    /// <remarks>
    /// Necessary because several theme colours are already translucent: <c>GlassBaseDark</c> is
    /// <c>#A00A0A10</c> on BaseDarkGlass and <c>#D90A0A10</c> on CyberNOC, but opaque on Monolith and
    /// SolarFlare. <see cref="Brush.Opacity"/> MULTIPLIES with the colour's alpha channel,
    /// so handing one of those to a site that then applies 0.92 yields 0.58 — quietly more
    /// transparent than the hardcoded colour it replaced, on two themes out of four and not the
    /// other two. Sites that mean "this plate is 92% opaque" have to start from an opaque colour.
    /// </remarks>
    public static Color OpaqueColor(string key, Color fallback)
    {
        var resolved = Color(key, fallback);
        return Avalonia.Media.Color.FromRgb(resolved.R, resolved.G, resolved.B);
    }

    /// <summary>
    /// Black or white, whichever is legible on <paramref name="background"/>.
    /// </summary>
    /// <remarks>
    /// For text sitting on a THEME-RESOLVED fill, where a hardcoded foreground cannot be right for
    /// every theme. The colour picker's confirm button is the worked example: its background became
    /// the theme accent, which is a deep blue on CyberNOC but amber (<c>#FFB800</c>) on SolarFlare,
    /// and the white label it kept from the old fixed blue drops to roughly 1.9:1 against that —
    /// unreadable. Making the fill theme-aware without the foreground is its own bug.
    /// <para>
    /// None of the 49 theme keys is an "on-accent" colour, so this is computed rather than looked
    /// up: sRGB relative luminance per WCAG, thresholded at the value where black and white have
    /// equal contrast. That yields a legible pair for any accent a theme picks, including any the
    /// user sets through customisation, without asking four theme files to define a new key.
    /// </para>
    /// </remarks>
    public static Color ForegroundOn(Color background)
    {
        static double Channel(byte raw)
        {
            var v = raw / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Channel(background.R))
                      + (0.7152 * Channel(background.G))
                      + (0.0722 * Channel(background.B));

        // 0.179 is where contrast against black equals contrast against white.
        return luminance > 0.179 ? Colors.Black : Colors.White;
    }

    private static object? TryGet(string key)
    {
        var app = Application.Current;
        if (app is null)
            return null;

        return app.TryGetResource(key, app.ActualThemeVariant, out var found) ? found : null;
    }
}
