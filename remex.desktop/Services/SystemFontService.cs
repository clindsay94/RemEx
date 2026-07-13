using System;
using System.Collections.Generic;
using System.Linq;
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
        new("JetBrains Mono", "avares://Remex.Desktop/Assets#JetBrains Mono", true),
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

    private static IEnumerable<string> GetSystemFontNames()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Font enumeration is best-effort; fall back to the bundled fonts only.
            return Array.Empty<string>();
        }
    }
}
