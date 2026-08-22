using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Guards the assumptions <see cref="ThemeResources"/> rests on.
/// </summary>
/// <remarks>
/// The failure mode these exist for is silent. A theme lookup that misses does not throw and does
/// not log — it returns the caller's fallback, which is the very colour literal the lookup was
/// added to replace. So a mistyped key name, or a key that exists in three theme files and not the
/// fourth, restores the original bug permanently and looks exactly like success. Nothing at runtime
/// would report it, which is why it is asserted here instead.
/// </remarks>
public sealed class ThemeResourcesTests
{
    // Resolved through ThemeDictionary rather than read raw: since RemEx-07jij a preset file holds
    // only its geometry and merges Themes/Shared/FallbackPalette.axaml for the colours, so a raw
    // read would see four keys and report every colour key missing from every theme.
    private static string[] ThemeFiles => ThemeDictionary.PresetNames;

    [Fact]
    public void Brush_FallsBack_WhenThereIsNoApplication()
    {
        // No Avalonia Application in a unit test, which is also the shape of the real degraded case:
        // resolution must never throw where it is called — inside a converter or a Render override.
        var fallback = new SolidColorBrush(Colors.Magenta);

        ThemeResources.Brush("AccentPrimaryBrush", fallback).Should().BeSameAs(fallback);
    }

    [Fact]
    public void Color_FallsBack_WhenThereIsNoApplication()
    {
        ThemeResources.Color("AccentPrimary", Colors.Magenta).Should().Be(Colors.Magenta);
    }

    [Fact]
    public void EveryThemeDefinesTheSameKeys()
    {
        var keysPerTheme = ThemeFiles.ToDictionary(f => f, f => KeysIn(f));

        // The property that makes a fallback-based lookup safe: a key resolving under one theme
        // resolves under all four. Without it, a control would follow the theme on three and
        // silently use its hardcoded literal on the fourth — the exact bug RemEx-fy0a is about,
        // reintroduced in a form that only shows up on one theme.
        var shared = keysPerTheme.Values.Skip(1)
            .Aggregate(new HashSet<string>(keysPerTheme.Values.First()), (acc, next) =>
            {
                acc.IntersectWith(next);
                return acc;
            });

        foreach (var (file, keys) in keysPerTheme)
        {
            keys.Except(shared).Should().BeEmpty(
                $"{file} defines a key the other themes do not, so any code pointing at it would " +
                "quietly fall back everywhere else");
        }
    }

    [Fact]
    public void EveryKeyAskedForByCode_ExistsInAllFourThemes()
    {
        var declared = ThemeFiles.ToDictionary(f => f, KeysIn);
        var requested = RequestedKeys();

        requested.Should().NotBeEmpty("this guard is worthless if the scan matches nothing");

        // Named explicitly because it is reachable ONLY through CanvasMinimap.Plate's key parameter,
        // which is precisely the shape the scanner used to miss. If a refactor makes this key
        // unreachable, this line fails loudly instead of the coverage quietly shrinking.
        requested.Should().Contain("SystemSuccess",
            "keys passed through Plate must be in scope, not just direct ThemeResources calls");

        // Reported as a flat list of "theme: key" rather than with OnlyContain, whose failure
        // message dumps all four key sets and buries the one name that is actually wrong.
        var missing = (from key in requested
                       from theme in declared
                       where !theme.Value.Contains(key)
                       select $"{theme.Key}: {key}").ToList();

        missing.Should().BeEmpty(
            "code asking the theme for a name it does not define resolves to the hardcoded " +
            "fallback forever, silently, which is indistinguishable from working");
    }

    /// <summary>Every key literal passed to <see cref="ThemeResources"/> anywhere in remex.desktop.</summary>
    /// <remarks>
    /// Matches ANY <c>ThemeResources.</c> member rather than naming them, because naming them is how
    /// this guard silently stopped covering a key once already: it was written against
    /// <c>Brush|Color</c>, and adding <c>OpaqueColor</c> as a third entry point moved every call
    /// through it out of scope without failing anything.
    /// <para>
    /// <c>Plate(</c> is matched separately for the same reason. <c>CanvasMinimap.Plate</c> passes its
    /// <c>key</c> PARAMETER through to <c>ThemeResources</c>, so its four literals are invisible to
    /// any pattern anchored on the <c>ThemeResources.</c> prefix — a scanner that only followed the
    /// direct calls would report a confident, and wrong, all-clear for that whole control.
    /// </para>
    /// </remarks>
    private static List<string> RequestedKeys()
    {
        var patterns = new[]
        {
            new Regex(@"ThemeResources\.\w+\(\s*""([^""]+)"""),
            new Regex(@"[^\w]Plate\(\s*""([^""]+)"""),
        };

        return Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => patterns.SelectMany(p => p.Matches(File.ReadAllText(path)))
                                        .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
    }

    private static HashSet<string> KeysIn(string theme) => ThemeDictionary.KeysIn(theme);

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
