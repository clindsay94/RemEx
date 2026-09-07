using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the two halves of the stale-staged-sensor mark (RemEx-lki2r, following up on RemEx-yqpa):
/// the 0.55 opacity a sighted user sees, and the <c>AutomationProperties.HelpText</c> a screen
/// reader gets instead. Both must stay driven off the same <c>IsStale</c> field, because the whole
/// point of routing the hint through <c>NotifyPropertyChangedFor</c> rather than setting it
/// alongside the opacity class at the call site is that the two cannot independently drift.
/// </summary>
/// <remarks>
/// SOURCE-TEXT FOR THE XAML HALF, BEHAVIOUR FOR THE VIEWMODEL HALF - the same split
/// <c>StatusDotPresenceBindingTests</c> uses, for the same reason: there is no headless render here
/// that would notice a binding quietly pointing at the wrong property.
/// </remarks>
public class StaleSensorAccessibilityTests
{
    /// <summary>
    /// The opacity RemEx-lki2r's eyes pass landed on. A SINGLE TOKEN, not a per-theme value - see
    /// the style comment this asserts against for why a colour was rejected in favour of this.
    /// </summary>
    private const string ExpectedStaleOpacity = "0.55";

    [Fact]
    public void BorderStaleStyle_SetsTheOpacityTokenTheEyesPassVerified()
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
    public void StagedCardTemplate_BindsAutomationHelpTextOnTheSameElementAsTheStaleClass()
    {
        var axaml = File.ReadAllText(CanvasViewPath());

        // Whole-element match (Singleline, "<material:Card ... >") so attributes wrapped across
        // lines are still seen together - the same shape StatusDotPresenceBindingTests uses for
        // exactly this reason.
        var cardMatch = Regex.Match(
            axaml,
            @"<material:Card\s+Classes=""interactive""\s+Classes\.stale=""\{Binding IsStale\}""[^>]*>",
            RegexOptions.Singleline);

        cardMatch.Success.Should().BeTrue(
            "the staged card template should still carry Classes.stale=\"{Binding IsStale}\" on the " +
            "material:Card element");
        cardMatch.Value.Should().Contain(
            "AutomationProperties.HelpText=\"{Binding StaleAutomationHint}\"",
            "the non-visual signal must live on the SAME element as the opacity class, bound to " +
            "StaleAutomationHint - a screen reader user must not be able to reach a card whose " +
            "opacity says stale but whose HelpText says nothing, or the reverse");
    }

    [Fact]
    public void IsStale_ChangedRaisesBothItsOwnAndStaleAutomationHintNotifications()
    {
        var card = new CanvasCardViewModel();
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.IsStale = true;

        raised.Should().Contain(nameof(CanvasCardViewModel.IsStale));
        raised.Should().Contain(nameof(CanvasCardViewModel.StaleAutomationHint),
            "StaleAutomationHint is generated off IsStale via NotifyPropertyChangedFor specifically " +
            "so the two cannot go out of step - if this fails, the attribute was removed or the " +
            "field it targets was renamed without updating it");
    }

    /// <summary>
    /// The behavioural half of what LocalizedPropertyRefreshTests checks by source scan: a card
    /// left staged and stale across a language switch must announce the NEW language, not the one
    /// active when it first went stale.
    /// </summary>
    [Fact]
    public void StaleAutomationHint_RefreshesOnALanguageChange()
    {
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            var card = new CanvasCardViewModel { IsStale = true };
            var englishHint = card.StaleAutomationHint;

            var raised = new List<string?>();
            card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            LocalizationService.Instance.SetCulture("fr");

            raised.Should().Contain(nameof(CanvasCardViewModel.StaleAutomationHint),
                "the card must re-raise StaleAutomationHint when the language changes, or a screen " +
                "reader keeps hearing the pre-switch language for as long as the card stays stale");
            card.StaleAutomationHint.Should().NotBe(englishHint,
                "A11y_StagedSensorStale has a real French translation, so the French and English " +
                "hints must differ - if they match here, the translation is missing or the getter " +
                "isn't actually re-resolving the key");
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    [Fact]
    public void StaleAutomationHint_IsNullWhenLiveAndTheLocalizedHintWhenStale()
    {
        var card = new CanvasCardViewModel();

        card.IsStale = false;
        card.StaleAutomationHint.Should().BeNull(
            "a live staged card must add no HelpText at all, not an empty string that would still " +
            "override whatever HelpText the card might otherwise carry");

        card.IsStale = true;
        card.StaleAutomationHint.Should().Be(LocalizationService.Instance["A11y_StagedSensorStale"]);
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
