using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Remex.Desktop.Models;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Reads the theme dictionaries the way Avalonia does: a preset plus everything it merges.
/// </summary>
/// <remarks>
/// <para>
/// The four presets used to be four self-contained 100-line files, so a test could answer "does
/// CyberNOC define AccentForegroundBrush?" with <c>File.ReadAllText</c>. Since RemEx-07jij they are
/// geometry plus a <c>ResourceInclude</c> of <c>Themes/Shared/FallbackPalette.axaml</c>, and a raw
/// read of the preset file now sees four keys where the app sees fifty-three.
/// </para>
/// <para>
/// <see cref="ResolvedText"/> throws when an include does not resolve on disk rather than returning
/// what it managed to read. NOT because Avalonia would be quiet about it — measured, a mistyped
/// <c>Source</c> is a hard build error, <c>AVLN2000 Unable to resolve XAML resource</c>, so the app
/// cannot ship with one. It throws because these tests read the FILES, not the built assembly: a
/// resolver that silently returned only the preset's four geometry keys would leave every "is this
/// value readable" guard with no value to measure, and a guard with nothing to measure passes.
/// </para>
/// </remarks>
internal static class ThemeDictionary
{
    /// <summary>
    /// The selectable presets: the <c>.axaml</c> files directly in <c>Themes/</c>.
    /// </summary>
    /// <remarks>
    /// FROM DISK, NOT A HARDCODED LIST — a fifth preset added without the tokens must be caught, and
    /// it would not be if the list had to be remembered. Non-recursive on purpose: <c>Themes/Shared/</c>
    /// holds the common base, which is merged BY every preset and is not itself selectable, so
    /// counting it as a theme would make "every theme declares X" true of a file no user can pick.
    /// </remarks>
    public static string[] PresetNames =>
        Directory.EnumerateFiles(ThemesDirectory, "*.axaml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    public static string ThemesDirectory =>
        Path.Combine(RepoRoot(), "remex.desktop", "Themes");

    public static string PresetPath(string preset) =>
        Path.Combine(ThemesDirectory, preset + ".axaml");

    /// <summary>The shared base every preset merges.</summary>
    public static string FallbackPalettePath =>
        Path.Combine(ThemesDirectory, "Shared", "FallbackPalette.axaml");

    /// <summary>
    /// A preset's own text followed by the text of every dictionary it merges, transitively.
    /// </summary>
    public static string ResolvedText(string preset) =>
        ResolvedTextOfFile(PresetPath(preset));

    /// <summary>Every <c>x:Key</c> a preset resolves, its merged dictionaries included.</summary>
    public static HashSet<string> KeysIn(string preset) =>
        new(Regex.Matches(ResolvedText(preset), @"x:Key=""([^""]+)""").Select(m => m.Groups[1].Value));

    /// <summary>One entry of <c>SeedPresetCatalog.All</c>, in the shape the older tests expect.</summary>
    public readonly record struct PresetCase(SeedPreset Definition)
    {
        /// <summary>The persisted id, which is also the display key's suffix.</summary>
        public string Preset => Definition.Id;

        /// <summary>Whether this preset pins a seed. <c>Dynamic</c> deliberately does not.</summary>
        public bool IsSeeded => Definition.Seed is not null;

        public string Seed => Definition.Seed ?? string.Empty;

        public string Variant => Definition.SchemeVariant ?? "TonalSpot";

        public double Contrast => Definition.Contrast ?? 0.0;

        public bool WritesMode => Definition.IsLight.HasValue;

        public bool IsLight => Definition.IsLight == true;
    }

    /// <summary>
    /// Every preset, read off the catalog.
    /// </summary>
    /// <remarks>
    /// THIS USED TO BE A REGEX OVER <c>SelectTheme</c>'s SWITCH ARMS, because until RemEx-2gjwn the
    /// switch WAS the preset list — there was nowhere else to read a seed from. The catalog is that
    /// place now, so the scan is a plain reference and cannot decay into matching nothing. What the
    /// scan protected against instead moves to <see cref="AssertSelectThemeReadsTheCatalog"/>: the
    /// risk was never the regex, it was a second hardcoded copy of the presets drifting from the
    /// first, and a switch reappearing in SelectTheme is exactly that copy coming back.
    /// </remarks>
    public static PresetCase[] SelectThemeCases()
    {
        var cases = SeedPresetCatalog.All.Select(p => new PresetCase(p)).ToArray();

        // ANTI-VACUITY. Every count-based assertion built on this is trivially satisfied by an empty
        // list, and the four homages plus Dynamic are the floor the catalog may never drop below.
        Assert.True(cases.Length > 4, "the preset catalog lost entries");
        foreach (var required in new[] { "BaseDarkGlass", "CyberNOC", "SolarFlare", "Monolith", "Dynamic" })
        {
            Assert.Contains(cases, c => c.Preset == required);
        }

        return cases;
    }

    /// <summary>
    /// Fails if <c>SelectTheme</c> stops going through the catalog, or grows a preset switch again.
    /// </summary>
    public static void AssertSelectThemeReadsTheCatalog()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));

        var body = Regex.Match(source, @"private void SelectTheme\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(body.Success, "SelectTheme moved or changed shape - this guard cannot see it");

        Assert.Contains("SeedPresetCatalog.TryGet", body.Value, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"case AppTheme\.", body.Value);
    }

    private static string ResolvedTextOfFile(string path, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A dictionary merged twice down two paths is legal; re-reading it is merely wasteful, but a
        // cycle would hang the test run, so both are handled by refusing to visit a file twice.
        if (!seen.Add(Path.GetFullPath(path))) return string.Empty;

        Assert.True(File.Exists(path), $"theme dictionary {path} does not exist");
        var text = File.ReadAllText(path);

        var builder = new StringBuilder(text);
        foreach (Match include in Regex.Matches(text, @"<ResourceInclude\b[^>]*Source=""([^""]+)"""))
        {
            builder.Append('\n').Append(ResolvedTextOfFile(ResolveAvares(include.Groups[1].Value), seen));
        }

        return builder.ToString();
    }

    /// <summary>Maps <c>avares://Remex.Desktop/Themes/Shared/X.axaml</c> to its path on disk.</summary>
    private static string ResolveAvares(string source)
    {
        const string prefix = "avares://Remex.Desktop/";
        Assert.StartsWith(prefix, source, StringComparison.Ordinal);

        var relative = source[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(RepoRoot(), "remex.desktop", relative);
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
