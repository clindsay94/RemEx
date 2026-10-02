namespace Remex.Desktop.Services;

/// <summary>
/// Picks the font a page title or subtitle is drawn in for the current UI language (RemEx-kq10x.4).
/// </summary>
/// <remarks>
/// Every bundled display font (Orbitron, Bungee Shade, Nabla, Sixtyfour) is Latin-only. Left alone,
/// a Ukrainian or Hindi title would fall back glyph by glyph, so a single word could mix two fonts.
/// The choice is made once per language instead: Ukrainian (Cyrillic) titles use Victor Mono Bold,
/// which is bundled and covers Cyrillic, and Hindi (Devanagari) titles use the platform's default
/// sans. A font the user installed on the system is never second-guessed, because RemEx can't know
/// which scripts it covers. The phone applies the same rule in <c>DisplayFonts.kt</c>.
/// </remarks>
public static class PageDisplayFontRule
{
    /// <summary>Bundled Victor Mono Bold, the title font for languages written in Cyrillic.</summary>
    public const string VictorMonoUri = "avares://Remex.Desktop/Assets/Fonts/victor_mono_bold.ttf#Victor Mono";

    /// <summary>Returned when the title should use the platform's default sans (<c>FontFamily.Default</c>).</summary>
    public const string PlatformDefault = "$Default";

    /// <summary>The default page-title font on both apps: Bungee Shade (RemEx-kq10x.4).</summary>
    public const string DefaultTitleFont = "avares://Remex.Desktop/Assets/Fonts/BungeeShade-Regular.ttf#Bungee Shade";

    /// <summary>The default page-subtitle font on both apps: Orbitron.</summary>
    public const string DefaultSubtitleFont = "avares://Remex.Desktop/Assets/Fonts#Orbitron";

    private const string BundledFontPrefix = "avares://Remex.Desktop/Assets/Fonts";

    /// <summary>
    /// The subtitle font a profile asks for. An unset subtitle follows the title font (RemEx-n6csl), so a
    /// profile with a custom title font keeps matching subtitles, except that the default Bungee Shade
    /// title pairs with Orbitron subtitles, the shared default on both apps.
    /// </summary>
    public static string SubtitleFont(string? configuredSubtitle, string configuredTitle) =>
        configuredSubtitle
        ?? (configuredTitle == DefaultTitleFont ? DefaultSubtitleFont : configuredTitle);

    /// <summary>
    /// Returns the font to use for a page title or subtitle.
    /// </summary>
    /// <param name="cultureTag">The UI culture, e.g. <c>uk</c>, <c>uk-UA</c> or <c>pt-BR</c>.</param>
    /// <param name="configuredFont">The font picked in Personalize (or the default, Orbitron).</param>
    /// <returns><paramref name="configuredFont"/>, <see cref="VictorMonoUri"/> or <see cref="PlatformDefault"/>.</returns>
    public static string ForLanguage(string? cultureTag, string configuredFont)
    {
        if (!configuredFont.StartsWith(BundledFontPrefix, StringComparison.Ordinal))
            return configuredFont;

        var language = (cultureTag ?? string.Empty).Split('-', '_')[0].ToLowerInvariant();
        return language switch
        {
            "uk" when !IsVictorMono(configuredFont) => VictorMonoUri,
            "hi" => PlatformDefault,
            _ => configuredFont,
        };
    }

    private static bool IsVictorMono(string font) =>
        font.Contains("victor_mono", StringComparison.OrdinalIgnoreCase);
}
