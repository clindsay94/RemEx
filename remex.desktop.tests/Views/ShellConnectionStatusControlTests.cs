using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the nav-drawer status control's move from an unfocusable <c>Border</c> to a real
/// <c>Button</c> carrying a tooltip and a details flyout (RemEx-44gc6). A source scan, matching
/// <see cref="ShellDrawerHeaderTests"/> and friends — there is no headless Avalonia render in this
/// suite, so these tests read <c>ShellView.axaml</c> as text.
/// </summary>
public class ShellConnectionStatusControlTests
{
    [Fact]
    public void TheStatusControlIsAButtonRatherThanABorder()
    {
        // THE REPORTED DEFECT'S ROOT CAUSE. A Border cannot be focused or carry a ToolTip.Tip — and
        // the whole point of this bead is that the collapsed drawer needs both.
        ConnectionStatusButtonElement().Should().Contain("<Button",
            "the status control has to be a real Button so it is focusable and can carry a tooltip");
    }

    [Fact]
    public void TheStatusButtonCarriesAToolTip()
    {
        // THE COLLAPSED DRAWER'S ONLY INFORMATION CHANNEL. With the text column hidden, this is the
        // entire fix — the state the whole complaint (SPEC §1) is about.
        ConnectionStatusButtonElement().Should().Contain("ToolTip.Tip",
            "the tooltip is what makes the collapsed drawer carry the full picture");
    }

    [Fact]
    public void TheStatusButtonNamesItselfForAccessibility()
    {
        ConnectionStatusButtonElement().Should().MatchRegex(
            @"AutomationProperties\.Name=""\{conv:Localize A11y_ConnectionStatusButton\}""");
    }

    [Fact]
    public void TheStatusButtonOpensAFlyout()
    {
        ConnectionStatusButtonElement().Should().Contain("Button.Flyout");
    }

    [Fact]
    public void TheDotInsideTheButtonCarriesNoAutomationName()
    {
        // SPEC §7 / RemEx-x12a: the accessible name moved ONTO the Button and OFF the dot. A dot
        // that still carries AutomationProperties.Name would double-announce the same fact — once
        // from the Button's own name, once from the dot inside it.
        foreach (Match ellipse in Regex.Matches(ConnectionStatusButtonElement(), @"<Ellipse\b[^>]*/?>"))
        {
            ellipse.Value.Should().NotContain("AutomationProperties.Name",
                "the dot's accessible name moved to the Button; a name here would double-announce it");
        }
    }

    [Fact]
    public void TheDotIsAPresenceBadgeThatTracksPhoneAttachment()
    {
        // RemEx-d7xj8: the Panel-hosted Ellipse dot became a Material Badged badge. Placement/paint
        // live in App.axaml's `material|Badged.presence` styles, not at the call site — this test
        // only checks that the Badged carries the three bindings that drive it.
        ConnectionStatusButtonElement().Should().MatchRegex(
            @"<material:Badged\b[^>]*Classes=""presence""[^>]*Classes\.connected=""\{Binding Presence\.IsPhoneAttached\}""[^>]*Classes\.pulse=""\{Binding ShowPresencePulse\}""[^>]*BadgeDisplayContent=""False""");
    }

    [Fact]
    public void TheButtonNoLongerHostsAPanelEllipseDotOutsideTheFlyout()
    {
        // The old Panel/Ellipse dot moved into a Badged (RemEx-d7xj8). The flyout header keeps its
        // own separate Ellipse dot — see the class doc comment — so this only checks the button's
        // own content Grid (everything up to Button.Flyout), not the flyout body.
        var buttonElement = ConnectionStatusButtonElement();
        var flyoutIndex = buttonElement.IndexOf("<Button.Flyout>", StringComparison.Ordinal);
        flyoutIndex.Should().BeGreaterThan(0, "the button has to declare a flyout");
        var beforeFlyout = buttonElement[..flyoutIndex];

        beforeFlyout.Should().NotContain("<Ellipse",
            "the presence dot is a Badged now, not an Ellipse overlaid on a Panel");
    }

    // ─── Localization parity (spec §9): the 6 new keys exist, with matching placeholders, in all 9 files ───

    private static readonly string[] NewKeys =
    [
        "Shell_StatusTooltipAddressLine",
        "Shell_StatusFlyoutAddress",
        "Shell_StatusFlyoutHostLink",
        "Shell_StatusFlyoutLatency",
        "Shell_StatusFlyoutHostRuntime",
        "A11y_ConnectionStatusButton",
    ];

    [Theory]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.fr.resx")]
    [InlineData("Strings.hi.resx")]
    [InlineData("Strings.id.resx")]
    [InlineData("Strings.pl.resx")]
    [InlineData("Strings.pt-BR.resx")]
    [InlineData("Strings.tr.resx")]
    [InlineData("Strings.uk.resx")]
    public void EveryLocaleDeclaresTheNewKeysWithMatchingPlaceholders(string fileName)
    {
        var english = LoadResx("Strings.resx");
        var localized = LoadResx(fileName);

        foreach (var key in NewKeys)
        {
            english.Should().ContainKey(key, $"Strings.resx has to declare {key}");
            localized.Should().ContainKey(key, $"{fileName} does not declare {key}");
            Assert.False(string.IsNullOrWhiteSpace(localized[key]), $"{fileName}: {key} is blank");

            // {0}/{1} placeholders have to survive translation — a locale that drops one silently
            // renders the format string literally, or throws a FormatException at runtime.
            for (var i = 0; i < 2; i++)
            {
                var placeholder = "{" + i + "}";
                if (english[key].Contains(placeholder))
                {
                    localized[key].Should().Contain(placeholder,
                        $"{fileName}: {key} drops the {placeholder} placeholder");
                }
            }
        }
    }

    // ─── THE ACTION MATRIX (spec §4): exactly one group of actions per state ───

    [Theory]
    [InlineData("Shell_LogsDiagnostics", "NavigateToDiagnosticLogsCommand", "Presence.IsHostDown")]
    [InlineData("Btn_Connect", "Connection.ConnectCommand", "Presence.IsHostDown")]
    [InlineData("Home_PairPhoneButton", "NavigateToSettingsCommand", "Presence.HasNoPhone")]
    [InlineData("Nav_Settings", "NavigateToSettingsCommand", "Presence.HasPhone")]
    public void EachFlyoutActionIsGatedOnExactlyItsState(string key, string command, string gate)
    {
        // Rebinding one action's IsVisible to the wrong state would show two actions in one state
        // and none in another, with every other test still green (review, round 1).
        var actions = Regex.Matches(ConnectionStatusButtonElement(), @"<Button\b[^>]*Content=""\{conv:Localize (?<key>\w+)\}""[^>]*/>")
            .Cast<Match>()
            .ToDictionary(m => m.Groups["key"].Value, m => m.Value);

        actions.Should().ContainKey(key, "the flyout offers this action");
        actions[key].Should().Contain($"Command=\"{{Binding {command}}}\"");
        actions[key].Should().Contain($"IsVisible=\"{{Binding {gate}}}\"");
    }

    [Fact]
    public void TheFlyoutOffersNoDisconnect()
    {
        // Spec §4: Disconnect already lives on Settings and Home, and a click-away flyout is the
        // wrong home for something that costs a reconnect round-trip to undo.
        ConnectionStatusButtonElement().Should().NotContain("DisconnectCommand");
    }

    [Fact]
    public void TheTooltipAddressLineTemplateTakesBothArguments()
    {
        var english = LoadResx("Strings.resx");

        english["Shell_StatusTooltipAddressLine"].Should().Contain("{0}").And.Contain("{1}");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string ConnectionStatusButtonElement()
    {
        // Captures the WHOLE Button element, open tag (with its attributes — AutomationProperties.Name
        // included) through matching close tag, so the tooltip, the dot and the flyout can all be
        // asserted on together without the scan spilling into the rest of the drawer content around it.
        var match = Regex.Match(ShellMarkup(),
            @"(?<elem><Button\b(?=[^>]*\bName=""ConnectionStatusButton"")[^>]*>.*?</Button>)",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue(
            "the connection-status Button has to have a closing tag with its content in between");
        return match.Groups["elem"].Value;
    }

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static Dictionary<string, string> LoadResx(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", fileName);
        Assert.True(File.Exists(path), $"Not found: {path}");

        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
