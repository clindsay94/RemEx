using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// A chip's icon must stay visible in every state it can be in (RemEx-xpfls).
/// </summary>
/// <remarks>
/// <para>
/// THE PIN GLYPH DISAPPEARED THE MOMENT THE FLYOUT WAS PINNED, leaving a plain filled square. That
/// is not cosmetic: pinned and transient differ in whether the window can be resized and whether its
/// geometry persists, and the pin is the only thing that says which mode you are in. Connor reported
/// the resize behaviour backwards because of it, and a bead (RemEx-2j58q) was opened and closed as
/// not-a-bug on the strength of that confusion.
/// </para>
/// <para>
/// IT WAS A PRIORITY COLLISION, NOT A MISSING FILL, and the first diagnosis of it — mine — was
/// wrong. Avalonia's frame order puts an app style with an activator at <c>StyleTrigger</c>, a
/// ControlTheme style with an activator at <c>StyleTriggerTheme</c>, and a plain app style at
/// <c>Style</c>. On <c>:checked</c> our accent Foreground beat Fluent's, while Fluent's accent
/// Background beat the plain <c>Background="Transparent"</c> on the base chip style — accent on
/// accent. Setting the background at the same activator priority is what fixes it.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST, the idiom this project already uses for bindings that fail silently: there is
/// no headless render here, and an invisible icon throws nothing.
/// </para>
/// <para>
/// THE STYLES MOVED IN RemEx-x3vom and this guard followed them. The tray chips are
/// <c>tertiary icon-button compact</c> from the shared vocabulary now, so the checked rule lives in
/// <c>App.axaml</c> and applies to every toggling button rather than to three chips in one window.
/// The invariant is unchanged and the reason it is load-bearing is unchanged; only its blast radius
/// grew, which is an argument for the guard rather than against it.
/// </para>
/// </remarks>
public class TrayChipIconVisibilityTests
{
    [Fact]
    public void EveryCheckedChipStateSetsItsOwnBackgroundAsWellAsItsForeground()
    {
        // SCOPED TO :checked, DELIBERATELY, AND THE FIRST VERSION OF THIS WAS NOT. Asserting the rule
        // for every chip state flagged the plain :pointerover, which is fine: the theme's hover fill
        // is a neutral wash, so an accent glyph still reads against it. Only the CHECKED fill is
        // accent-derived, and only there does recolouring the glyph accent collide with it.
        //
        // Writing the broader rule would have been writing a rule that is not true, and the stated
        // rule is what the next person reuses.
        var offenders = ToggleChipStateStyles()
            .Where(s => s.Selector.Contains(":checked", StringComparison.Ordinal))
            .Where(s => s.Body.Contains("Property=\"Foreground\"", StringComparison.Ordinal)
                     && !s.Body.Contains("Property=\"Background\"", StringComparison.Ordinal))
            .Select(s => s.Selector)
            .ToArray();

        offenders.Should().BeEmpty(
            "a checked chip that recolours the glyph but lets the theme supply the fill draws an "
            + "accent icon on Fluent's accent checked background — which is how the pin vanished the "
            + "moment the flyout was pinned");
    }

    [Fact]
    public void TheCheckedPinChipIsStyledAtAll()
    {
        // Guards the fix from being deleted wholesale rather than merely weakened. Without a
        // :checked style the chip falls entirely to the theme's accent-on-accent default.
        ToggleChipStateStyles()
            .Select(s => s.Selector)
            .Should().Contain(s => s.Contains(":checked", StringComparison.Ordinal),
                "the pinned state needs its own styling; Fluent's default is the bug");
    }

    [Fact]
    public void TheCheckedChipUsesThemeBrushesRatherThanLiterals()
    {
        // Four-theme safety. A literal that reads on CyberNOC dies on SolarFlare, and this is a
        // contrast-critical pairing — the whole point is that the glyph stands off its own fill.
        var checkedStyles = ToggleChipStateStyles()
            .Where(s => s.Selector.Contains(":checked", StringComparison.Ordinal));

        foreach (var style in checkedStyles)
        {
            Regex.Matches(style.Body, @"Value=""(#[0-9A-Fa-f]{3,8})""")
                .Select(m => m.Groups[1].Value)
                .Should().BeEmpty($"{style.Selector} must use DynamicResource theme brushes, not literals");
        }
    }

    /// <summary>
    /// The application's <c>ToggleButton</c> styles that carry a state pseudo-class — where the
    /// tray chips' checked rule lives since RemEx-x3vom folded them into the button vocabulary.
    /// </summary>
    private static (string Selector, string Body)[] ToggleChipStateStyles()
    {
        var flattened = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml")),
            @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var styles = Regex.Matches(flattened, @"<Style Selector=""([^""]+)"">(.*?)</Style>", RegexOptions.Singleline)
            .Select(m => (Selector: m.Groups[1].Value, Body: m.Groups[2].Value))
            .Where(s => s.Selector.Contains("ToggleButton", StringComparison.Ordinal)
                     && s.Selector.Contains(":checked", StringComparison.Ordinal))
            .ToArray();

        styles.Should().NotBeEmpty(
            "if this finds nothing the assertions above are vacuous — the selector or the file moved");
        return styles;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
