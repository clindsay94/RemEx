using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The PC's Personalize entry lives in the drawer footer, next to the connection chip, instead of a
/// floating palette button in the window corner (RemEx-kq10x.6, cross-app cohesion decision 8 in
/// <c>docs/specs/2026-10-01-cross-app-cohesion.md</c>: one floating element per app).
/// </summary>
/// <remarks>
/// <para>
/// A source scan, matching <see cref="ShellOverlayFocusTests"/> and <see cref="ShellSnackbarHostTests"/>:
/// <c>remex.desktop.tests</c> has no headless render, and a missing or mis-wired entry point throws
/// nothing. The user just can no longer find Personalize. XML comments are stripped before every
/// markup assertion, because this move is explained in prose right next to the markup and a guard a
/// comment can satisfy is not a guard.
/// </para>
/// <para>
/// This file replaces <c>ShellGearFabTests</c>, which pinned the floating button's Material
/// FloatingButton size, shadow and ripple traps. Those traps went with the control.
/// </para>
/// </remarks>
public class ShellPersonalizeFooterTests
{
    private static readonly string[] Locales = { "", ".es", ".fr", ".hi", ".id", ".pl", ".pt-BR", ".tr", ".uk" };

    [Fact]
    public void ThereIsNoFloatingPaletteButtonAnyMore()
    {
        var xaml = WithoutXmlComments(ShellMarkup());

        xaml.Should().NotContain("material:FloatingButton",
            "the shell keeps one floating element (the snackbar); Personalize moved into the drawer footer");
        xaml.Should().NotMatchRegex(@"\bName=""GearFab(Wrap|Shadow)?""",
            "the floating gear FAB, its wrapper and its shadow are gone, not hidden");
        xaml.Should().NotContain("#GearFab",
            "a style selector for an element that no longer exists styles nothing");
    }

    [Fact]
    public void ThePersonalizeEntrySitsInTheDrawerFooterAboveTheConnectionChip()
    {
        var xaml = WithoutXmlComments(ShellMarkup());

        var footer = Regex.Match(xaml,
            @"<StackPanel\b(?=[^>]*\bName=""DrawerFooter"")[^>]*>(?<body>.*?)</StackPanel>\s*</Grid>\s*</Border>\s*</Border>\s*</material:NavigationDrawer\.LeftDrawerContent>",
            RegexOptions.Singleline);
        footer.Success.Should().BeTrue("the drawer's last row is a named footer stack, closing the drawer content");

        var body = footer.Groups["body"].Value;
        var entry = body.IndexOf(@"Name=""PersonalizeFooterButton""", StringComparison.Ordinal);
        var chip = body.IndexOf(@"Name=""ConnectionStatusButton""", StringComparison.Ordinal);
        entry.Should().BeGreaterThanOrEqualTo(0, "Personalize has to be in the drawer footer");
        chip.Should().BeGreaterThan(entry, "the connection chip stays in the footer, below Personalize");

        FooterOpenTag().Should().MatchRegex(@"\bGrid\.Row=""2""", "the footer is the drawer grid's bottom row");
    }

    [Fact]
    public void ThePersonalizeEntryIsNotANavDestination()
    {
        // A ListBoxItem in NavList would take the "you are here" selection away from the real page
        // while the sheet is open. Personalize is a sheet over the current page, not a destination.
        var xaml = WithoutXmlComments(ShellMarkup());
        var navList = Regex.Match(xaml, @"<ListBox\b(?=[^>]*\bName=""NavList"").*?</ListBox>", RegexOptions.Singleline);
        navList.Success.Should().BeTrue();
        navList.Value.Should().NotContain("ToggleSettingsPanelCommand");
        navList.Value.Should().NotContain("PersonalizeFooterButton");
    }

    [Fact]
    public void ThePersonalizeEntryOpensTheSheet()
    {
        PersonalizeButtonOpenTag().Should().MatchRegex(@"\bCommand=""\{Binding ToggleSettingsPanelCommand\}""",
            "the footer entry uses the same command Ctrl+, and the command palette use");
    }

    [Fact]
    public void ThePersonalizeEntryHasATooltipAndAnAutomationName()
    {
        var tag = PersonalizeButtonOpenTag();
        tag.Should().MatchRegex(@"\bToolTip\.Tip=""\{conv:Localize Personalize_EntryTooltip\}""");
        tag.Should().MatchRegex(@"\bAutomationProperties\.Name=""\{conv:Localize Personalize_EntryTooltip\}""",
            "UI Automation and screen readers find the entry by this name");
    }

    [Fact]
    public void ThePersonalizeEntryShowsThePaletteIconAndALabel()
    {
        var element = PersonalizeButtonElement();
        element.Should().MatchRegex(@"<mi:MaterialIcon\b[^>]*\bKind=""Palette""");
        element.Should().MatchRegex(@"<TextBlock\b[^>]*\bText=""\{conv:Localize Personalize_Title\}""",
            "the label reuses the sheet's own title key, so the entry and the sheet it opens say the same word");
    }

    [Fact]
    public void ThePersonalizeEntryUsesTheButtonVocabulary()
    {
        // docs/BUTTON-VOCABULARY.md: tertiary is the present-but-not-competing emphasis. Background,
        // Foreground, CornerRadius and Padding belong to the class (ButtonVocabularyTests), so none
        // of them is set inline here.
        var tag = PersonalizeButtonOpenTag();
        tag.Should().MatchRegex(@"\bClasses=""tertiary""");
        foreach (var owned in new[] { "Background", "Foreground", "CornerRadius", "Padding" })
            tag.Should().NotMatchRegex($@"\s{owned}=", $"{owned} is owned by the tertiary class");
    }

    [Fact]
    public void ThePersonalizeKeysExistInEveryLocale()
    {
        foreach (var locale in Locales)
        {
            var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", $"Strings{locale}.resx");
            var values = XDocument.Load(path).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

            values.Should().ContainKey("Personalize_EntryTooltip", $"Strings{locale}.resx needs the footer entry's tooltip");
            values["Personalize_EntryTooltip"].Should().NotBeNullOrWhiteSpace();
            values.Should().ContainKey("Personalize_Title", $"Strings{locale}.resx needs the footer entry's label");
            values["Personalize_Title"].Should().NotBeNullOrWhiteSpace();
            values.Should().NotContainKey("Personalize_FabTooltip",
                $"Strings{locale}.resx still carries the retired FAB key; it was renamed, not duplicated");
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string FooterOpenTag()
    {
        var match = Regex.Match(WithoutXmlComments(ShellMarkup()),
            @"<StackPanel\b(?=[^>]*\bName=""DrawerFooter"")(?<attrs>[^>]*)>");
        match.Success.Should().BeTrue("the drawer footer stack has to exist");
        return match.Groups["attrs"].Value;
    }

    private static string PersonalizeButtonOpenTag()
    {
        // Anchored on the Name via a lookahead, so reordering attributes cannot fail the scan.
        var match = Regex.Match(WithoutXmlComments(ShellMarkup()),
            @"<Button\b(?=[^>]*\bName=""PersonalizeFooterButton"")(?<attrs>[^>]*)>");
        match.Success.Should().BeTrue("the Personalize footer button has to exist");
        return match.Groups["attrs"].Value;
    }

    private static string PersonalizeButtonElement()
    {
        var match = Regex.Match(WithoutXmlComments(ShellMarkup()),
            @"<Button\b(?=[^>]*\bName=""PersonalizeFooterButton"")[^>]*>.*?</Button>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the Personalize footer button has to exist");
        return match.Value;
    }

    private static string WithoutXmlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ShellMarkup()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
