using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the two halves of the stale-staged-sensor mark (RemEx-lki2r, following up on RemEx-yqpa):
/// the 0.55 opacity a sighted user sees, and the <c>AutomationProperties.HelpText</c> a screen
/// reader gets instead. Both are pure source-text pins - there is no headless render here that
/// would notice a binding quietly pointing at the wrong property, the same reason
/// <c>StatusDotPresenceBindingTests</c> works this way.
/// </summary>
/// <remarks>
/// NO VIEW-MODEL PROPERTY HERE, AND THAT WAS A REVIEW CORRECTION, NOT THE ORIGINAL DESIGN. A first
/// version added <c>CanvasCardViewModel.StaleAutomationHint</c>, generated off <c>IsStale</c> and
/// requiring the view model to subscribe to <c>LocalizationService.Instance.PropertyChanged</c> so
/// the hint refreshed on a language switch. Review caught that this app never disposes a removed
/// <c>CanvasCardViewModel</c> - <c>StagedCards.Remove</c>/<c>Cards.Remove</c> just drop the
/// reference - so with roughly 470 staged cards in a session, that subscription rooted every
/// removed card, and its <c>SensorViewModel</c> history buffers, for the process lifetime. The
/// HelpText now comes from a plain XAML style instead: zero per-item state, and
/// <c>{local:Localize}</c>/the converter below both refresh (or accept not refreshing, deliberately
/// - see <c>StaleSensorHelpTextConverter</c>'s own remarks) without a subscription to leak.
/// <para>
/// HELPTEXT LIVES ON THE LISTBOXITEM CONTAINER, NOT THE material:Card (also review). Verified live
/// on the hot-reload host: a hardcoded HelpText set directly on a material:Card never surfaced via
/// UIA - not on the Card, not on any descendant - while the identical value on the containing
/// ListBoxItem read back correctly. Material.Styles.Controls.Card simply does not route
/// AutomationProperties.HelpText to its automation peer. UIA also reads HelpText from the FOCUSED
/// element, which for a list is the item container, not a descendant - a second, independent
/// reason it belongs there. So the opacity and the HelpText are necessarily two different Style
/// elements (different TargetType), not one - both keyed off the same <c>IsStale</c> binding.
/// </para>
/// </remarks>
public class StaleSensorAccessibilityTests
{
    /// <summary>
    /// The opacity RemEx-lki2r's eyes pass landed on. A SINGLE TOKEN, not a per-theme value - see
    /// the style comment this asserts against for why a colour was rejected in favour of this.
    /// </summary>
    private const string ExpectedStaleOpacity = "0.55";

    [Fact]
    public void CardStaleStyle_SetsTheOpacityTokenTheEyesPassVerified()
    {
        var axaml = File.ReadAllText(CanvasViewPath());

        // Anchored to the Style element itself, not just "Opacity Value=" anywhere in the file -
        // a coincidental match elsewhere in CanvasView.axaml would otherwise pass this vacuously.
        //
        // material|Card, NOT Border (RemEx-lki2r): Material.Styles.Controls.Card derives from
        // ContentControl/TemplatedControl, not Border - a Selector="Border.stale" type-matches
        // nothing a Card ever is, so the original selector here never fired in any theme. Pinned
        // to the corrected selector so a regression back to "Border.stale" fails this test instead
        // of silently reintroducing dead styling.
        var match = Regex.Match(
            axaml,
            @"<Style\s+Selector=""material\|Card\.stale"">\s*<Setter\s+Property=""Opacity""\s+Value=""([^""]+)""\s*/>",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            "CanvasView.axaml should define a material|Card.stale style setting Opacity - a " +
            "Border.stale selector type-matches nothing (Card is not a Border) and is dead styling " +
            "(RemEx-lki2r) - if the selector or property name changed, update this test alongside it");
        match.Groups[1].Value.Should().Be(ExpectedStaleOpacity,
            "the stale opacity is a single token everywhere (no per-theme value); if the eyes pass " +
            "moved it, this pin and the remark above it must move together");
    }

    [Fact]
    public void StagedCardTemplate_BindsAutomationHelpTextOnTheListBoxItemContainer()
    {
        var axaml = File.ReadAllText(CanvasViewPath());

        // Whole-element match (Singleline) so attributes wrapped across lines are still seen
        // together - the same shape StatusDotPresenceBindingTests uses for exactly this reason.
        // TargetType ListBoxItem, not material:Card (see the class remarks for why): a Card's
        // automation peer does not surface HelpText via UIA at all, verified live.
        var itemStyleMatch = Regex.Match(
            axaml,
            @"<Style\s+Selector=""ListBoxItem""[^>]*>\s*<Setter\s+Property=""AutomationProperties\.HelpText""[^/]*/>",
            RegexOptions.Singleline);

        itemStyleMatch.Success.Should().BeTrue(
            "CanvasView.axaml should style ListBoxItem (the staged-card container) with an " +
            "AutomationProperties.HelpText setter - HelpText set on the inner material:Card does " +
            "not reach a screen reader (RemEx-lki2r)");
        itemStyleMatch.Value.Should().Contain("IsStale",
            "the HelpText setter must be bound off the SAME IsStale field the opacity style " +
            "reacts to (via Classes.stale), so the two cannot disagree about a given row");
        itemStyleMatch.Value.Should().Contain("StaleSensorHelpTextConverter",
            "the localized/null resolution belongs in the converter, not duplicated inline");
    }

    /// <summary>Every locale must define the key this bead added, not just the neutral resx.</summary>
    [Fact]
    public void A11yStagedSensorStale_IsDefinedInAllNineResxFiles()
    {
        var localeDirectory = Path.Combine(RepoRoot(), "remex.desktop", "Localization");
        var resxFiles = Directory.GetFiles(localeDirectory, "Strings*.resx");
        resxFiles.Should().HaveCountGreaterOrEqualTo(9, "the base resx plus 8 locale variants must all be on disk");

        var missing = new List<string>();
        foreach (var path in resxFiles)
        {
            var defined = XDocument.Load(path)
                .Root!
                .Elements("data")
                .Select(d => (string?)d.Attribute("name"))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);

            if (!defined.Contains("A11y_StagedSensorStale"))
                missing.Add(Path.GetFileName(path));
        }

        missing.Should().BeEmpty(
            "A11y_StagedSensorStale missing from: " + string.Join(", ", missing) +
            " - a key missing from even one locale file renders as its own name in that language");
    }

    private static string CanvasViewPath([CallerFilePath] string thisSourceFile = "")
        => Path.Combine(RepoRoot(thisSourceFile), "remex.desktop", "Views", "CanvasView.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var directory = Path.GetDirectoryName(thisSourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
    }
}
