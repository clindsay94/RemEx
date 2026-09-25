using Avalonia.Media;

namespace Remex.Desktop.Services;

/// <summary>
/// A selectable font for the personalization font picker. <see cref="Value"/> is what gets persisted
/// and handed to <see cref="FontFamily"/>: an <c>avares://…#Family</c> URI for a bundled display font,
/// or a plain family name for a font installed on the host system.
/// </summary>
public sealed record FontOption(string DisplayName, string Value, bool IsBundled)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Enumerates fonts for the typography picker: the app's bundled display fonts first (so they render
/// identically everywhere), then every font installed on the current system — Windows or Linux — so a
/// user who installs a new font sees it appear here. Desktop-only; not compiled into the NativeAOT core.
/// </summary>
public static class SystemFontService
{
    // Bundled display fonts. The Value strings MUST match the FontFamily resources in App.axaml.
    private static readonly FontOption[] Bundled =
    {
        new("Inter", "avares://Avalonia.Fonts.Inter/Assets#Inter", true),
        new("Orbitron", "avares://Remex.Desktop/Assets/Fonts#Orbitron", true),
        new("Bungee Shade", "avares://Remex.Desktop/Assets/Fonts/BungeeShade-Regular.ttf#Bungee Shade", true),
        new("Sixtyfour", "avares://Remex.Desktop/Assets/Fonts/Sixtyfour-Regular.ttf#Sixtyfour", true),
        new("Nabla", "avares://Remex.Desktop/Assets/Fonts/Nabla-Regular.ttf#Nabla", true),
        // JetBrains Mono is NOT embedded as a font file — it is only referenced via a fallback chain
        // elsewhere. Use plain family names (installed JetBrains Mono, else a monospace fallback) so
        // selecting it can never resolve to an empty avares asset and freeze the render thread.
        new("JetBrains Mono", "JetBrains Mono, Consolas, DejaVu Sans Mono, monospace", true),
        new("Victor Mono", "avares://Remex.Desktop/Assets/Fonts/victor_mono_bold.ttf#Victor Mono", true),
    };

    /// <summary>Bundled display fonts followed by the installed system fonts (alphabetical).</summary>
    public static IReadOnlyList<FontOption> GetHeaderFonts()
    {
        var fonts = new List<FontOption>(Bundled);
        foreach (var name in GetSystemFontNames())
            fonts.Add(new FontOption(name, name, false));
        return fonts;
    }

    /// <summary>
    /// The sorted installed-font names, enumerated once per process (perf audit P3-58). Avalonia's
    /// <c>SystemFontCollection</c> snapshots the installed family names when it is created, so a
    /// re-enumeration per Personalize build could never pick up a font installed mid-session anyway -
    /// it only re-sorted the same few hundred names. A FAILED enumeration is not cached: it is the
    /// "platform not ready" shape, and pinning it would hide every system font until a restart.
    /// </summary>
    private static IReadOnlyList<string>? s_systemFontNames;

    private static IReadOnlyList<string> GetSystemFontNames()
    {
        if (s_systemFontNames is { } cached) return cached;
        try
        {
            var names = FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            s_systemFontNames = names;
            return names;
        }
        catch
        {
            // Font enumeration is best-effort; fall back to the bundled fonts only.
            return Array.Empty<string>();
        }
    }

    /// <summary>Probe answers per font string (perf audit P3-64) - see <see cref="FontResolutionCache"/>.</summary>
    private static readonly FontResolutionCache s_resolved = new(ProbeFont);

    /// <summary>
    /// The real probe. <see cref="FontManager.Current"/> is read OUTSIDE the try on purpose: before the
    /// platform is initialised it throws, and that must propagate so the cache does not record it.
    /// </summary>
    private static FontFamily? ProbeFont(string value)
    {
        var fontManager = FontManager.Current;
        try
        {
            var candidate = new FontFamily(value);
            // Probe the actual glyph typeface. This is the same resolution the renderer performs, so if
            // it can be resolved here without throwing, rendering with it is safe too.
            return fontManager.TryGetGlyphTypeface(new Typeface(candidate), out var glyph) && glyph is not null
                ? candidate
                : null;
        }
        catch
        {
            // Malformed URI, missing embedded asset, or a font the platform can't load — reject it.
            return null;
        }
    }

    /// <summary>
    /// Tries to turn a persisted font value (an <c>avares://…#Family</c> URI or a plain family name)
    /// into a <see cref="FontFamily"/> that will actually render. It forces the font manager to load a
    /// glyph typeface <em>now</em>, so an unresolvable value (e.g. an avares URI pointing at a folder
    /// with no usable font) fails here — returning <c>false</c> — instead of throwing during rendering,
    /// which freezes the UI thread. Returns <c>true</c> and the family only when it materializes.
    /// </summary>
    /// <remarks>The probe runs once per distinct value per process; later calls reuse its answer and
    /// the same <see cref="FontFamily"/> instance (perf audit P3-64).</remarks>
    public static bool TryResolveFont(string? value, out FontFamily family)
    {
        try
        {
            return s_resolved.TryResolve(value, out family);
        }
        catch
        {
            // No font platform yet (see ProbeFont) — reject for now, uncached, so a later call retries.
            family = FontFamily.Default;
            return false;
        }
    }

    /// <summary>
    /// Resolves <paramref name="requested"/> to a renderable <see cref="FontFamily"/>, falling back to
    /// <paramref name="fallbackUri"/> (then the platform default) if it can't be materialized. Guarantees
    /// the returned family is safe to assign to a live resource without risking a render-thread freeze.
    /// </summary>
    public static FontFamily ResolveFontOrDefault(string? requested, string fallbackUri)
    {
        if (TryResolveFont(requested, out var resolved))
            return resolved;
        if (TryResolveFont(fallbackUri, out var fallback))
            return fallback;
        return FontFamily.Default;
    }
}

/// <summary>
/// Remembers a font probe's answer per value string (perf audit P3-64). Each theme apply resolves three
/// typography fonts, and every one used to force a glyph-typeface load - the bundled Nabla asset is
/// 1.6 MB. Both answers are kept: a resolved family (the same instance every time) and "unresolvable"
/// (null). A probe that THROWS is not an answer and is never cached; the exception propagates.
/// </summary>
internal sealed class FontResolutionCache
{
    private readonly Func<string, FontFamily?> _probe;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FontFamily?> _answers =
        new(StringComparer.Ordinal);

    public FontResolutionCache(Func<string, FontFamily?> probe) => _probe = probe;

    public bool TryResolve(string? value, out FontFamily family)
    {
        family = FontFamily.Default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!_answers.TryGetValue(value, out var answer))
        {
            answer = _probe(value);
            _answers[value] = answer;
        }

        if (answer is null)
            return false;
        family = answer;
        return true;
    }
}
