using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the readable floor on popup surfaces (ComboBox dropdown, ContextMenu, MenuFlyout) — no
/// bead, reported live by Connor 2026-08-31 alongside the title-bar overlap and the command
/// palette modal beep.
/// </summary>
/// <remarks>
/// <para>
/// Card Opacity (<c>GlassOpacity</c>) at 0% used to take every dropdown to fully transparent along
/// with the cards, because ComboBox.axaml wraps its dropdown in an un-Themed
/// <c>controls:Card</c> (resolving App.axaml's app-wide <c>{x:Type material:Card}</c> override)
/// and ContextMenu.axaml / MenuFlyoutPresenter.axaml both read
/// <c>MaterialCardBackgroundBrush</c>, which App.axaml now points at the same popup brush. The fix
/// is a separate <c>PopupSurfaceBrush</c>, floored at <see cref="ThemeService.PopupOpacityFloor"/>
/// so it still tracks the slider above the floor but never drops below readable.
/// </para>
/// <para>
/// ASSERTED PARTLY ON THE SOURCE, same reason as <c>ThemeKeyCoverageTests</c> and
/// <c>CardSurfaceTests</c>: <c>ThemeService.ApplyCustomization</c> needs a live Avalonia
/// <c>Application</c> to run, and there is none in a unit test. The floor constant itself needs no
/// such thing — it is read directly via <c>InternalsVisibleTo</c> — so that part is a real value
/// assertion, not a regex.
/// </para>
/// </remarks>
public class PopupOpacityFloorTests
{
    [Fact]
    public void TheFloorConstantIsFortyPercent()
    {
        // A real field read, not a source scan: PopupOpacityFloor is `internal const double`, and
        // Remex.Desktop.Tests has InternalsVisibleTo, so this fails the instant the number moves —
        // no rebuild-and-hope needed the way a regex on a literal would require.
        ThemeService.PopupOpacityFloor.Should().Be(0.40,
            "Connor asked for popups to read at 30-50% opacity always; 40% is the floor this "
            + "implementation chose and every other assertion in this file assumes");
    }

    [Fact]
    public void ThemeServiceClampsThePopupBrushToTheFloor_NotJustDeclaresIt()
    {
        // THE SILENT ONE. A constant that exists but is never read by the alpha computation would
        // pass every other check in this file while leaving popups exactly as transparent as cards
        // — no exception, no log line, just an unreadable dropdown the next time someone turns
        // Card Opacity down. Math.Max against the floor is what actually clamps; without it,
        // PopupSurfaceBrush is CardBackgroundBrush with a new name.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        source.Should().MatchRegex(
            @"Math\.Max\(\s*cardAlpha\s*,\s*\(byte\)Math\.Round\(\s*PopupOpacityFloor\s*\*\s*255\s*\)\s*\)",
            "the popup alpha has to be clamped UP to the floor, not just computed alongside it");

        source.Should().Contain("SetResourceOverrideInternal(\"PopupSurfaceBrush\"",
            "the clamped colour has to actually reach a resource popups can bind to");
    }

    [Fact]
    public void PopupSurfacesReadTheDedicatedBrush_NotTheUnfflooredCardBrush()
    {
        // THE OTHER HALF. The floor is worthless if ComboBox/ContextMenu/MenuFlyout keep pointing
        // at CardBackgroundBrush instead of the new PopupSurfaceBrush — a future refactor that
        // "simplifies" App.axaml by deleting the popup-specific selectors would silently put
        // popups back under the card slider with no floor at all.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var contextMenuStyle = Regex.Match(app,
            @"<Style Selector=""ContextMenu,\s*MenuFlyoutPresenter"">\s*<Setter Property=""Background""\s*Value=""([^""]+)""",
            RegexOptions.Singleline);
        contextMenuStyle.Success.Should().BeTrue(
            "ContextMenu/MenuFlyoutPresenter must have their own Background Style in App.axaml");
        contextMenuStyle.Groups[1].Value.Should().Be("{DynamicResource PopupSurfaceBrush}");

        var comboBoxDropdownStyle = Regex.Match(app,
            @"<Style Selector=""ComboBox /template/ Popup#PART_Popup > material\|Card#PART_Card"">\s*<Setter Property=""Background""\s*Value=""([^""]+)""",
            RegexOptions.Singleline);
        comboBoxDropdownStyle.Success.Should().BeTrue(
            "ComboBox's dropdown Card resolves the app-wide material:Card ControlTheme unless "
            + "something more specific overrides it — that selector is the override");
        comboBoxDropdownStyle.Groups[1].Value.Should().Be("{DynamicResource PopupSurfaceBrush}");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
