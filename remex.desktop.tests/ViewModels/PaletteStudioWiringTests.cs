using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The Palette Studio's wiring (RemEx-5u0vy), asserted over source text because
/// <c>CustomizationViewModel</c> cannot be constructed here — it takes a shell view model, a
/// dashboard layout service and a theme service, none of which stand up outside a running app.
/// </summary>
/// <remarks>
/// SOURCE-TEXT ASSERTIONS ARE WEAK AND THESE ARE STILL WORTH HAVING, because the failure they cover
/// is not a wrong calculation — it is a setting that is persisted, honoured, and reachable by
/// nobody. <c>ThemeContrast</c> and <c>UseLightPalette</c> were both in exactly that state until
/// this bead: correct in the record, correct in <c>ThemeService</c>, and settable only by editing
/// the profile JSON by hand. Nothing failed, so nothing said so.
/// </remarks>
public class PaletteStudioWiringTests
{
    [Theory]
    [InlineData("ThemeContrast")]
    [InlineData("UseLightPaletteSwitch")]
    [InlineData("SeedHue")]
    [InlineData("SeedChroma")]
    [InlineData("SeedTone")]
    public void EverySettingTheStudioOwnsIsBoundToAControlThatCanWriteIt(string property)
    {
        var markup = PanelMarkup();

        // A WRITER, NOT MERELY A MENTION. This asked only that the name appear in some binding, and
        // an injection proved that vacuous: deleting the contrast SLIDER's binding left the readout
        // TextBlock next to it still matching, so the test stayed green over a setting that had gone
        // back to being unreachable — the exact condition it is named for. Value= and IsChecked= are
        // the two properties on this panel through which a user changes anything.
        markup.Should().MatchRegex($@"(Value|IsChecked)=""\{{Binding {Regex.Escape(property)}[,}}]",
            "{0} is persisted and honoured, so a build with no INPUT control bound to it is a setting "
            + "the user cannot reach — which is the state this bead was filed to end", property);
    }

    [Fact]
    public void TheSeedWheelIsOnThePanelAndReportsWhenAnInteractionEnds()
    {
        var markup = PanelMarkup();

        markup.Should().Contain("<controls:HctColorWheel",
            "the wheel is the bead's headline control; sliders alone are the screen that already existed");

        markup.Should().Contain("SeedCommitted=",
            "without the commit signal the recently-used row fills with every colour a drag passed "
            + "through rather than the one the user stopped on");
    }

    [Fact]
    public void TheWheelAndItsSlidersAreReachableWithoutAMouse()
    {
        var markup = PanelMarkup();
        var studio = StudioSection(markup);

        // A wheel is the most obviously mouse-only control on the screen, so the accessible name is
        // the difference between "a colour picker" and "an unlabelled square" to a screen reader.
        studio.Should().MatchRegex(@"<controls:HctColorWheel[\s\S]*?AutomationProperties\.Name=");

        foreach (var axis in new[] { "SeedHue", "SeedChroma", "SeedTone", "ThemeContrast" })
        {
            studio.Should().MatchRegex($@"<Slider[^>]*\{{Binding {axis}\}}[^>]*AutomationProperties\.Name=",
                "the {0} slider needs a name, so the keyboard path through the studio is announced", axis);
        }
    }

    [Fact]
    public void TheAccentHandlerRefreshesTheSlidersBeforeItRepaints()
    {
        var body = MethodBody(ViewModelSource(), "partial void OnAccentColorChanged");

        var sync = body.IndexOf("SyncSeedFromAccent()", StringComparison.Ordinal);
        var apply = body.IndexOf("ApplyAndSave()", StringComparison.Ordinal);

        sync.Should().BeGreaterThanOrEqualTo(0,
            "a swatch click, a preset and the hex box all set AccentColor without going through the "
            + "wheel — nothing else moves the sliders back onto the new seed");
        apply.Should().BeGreaterThan(sync,
            "repainting first leaves one frame where the thumb sits on the colour that was just replaced");
    }

    [Theory]
    [InlineData("partial void OnSeedHueChanged")]
    [InlineData("partial void OnSeedChromaChanged")]
    [InlineData("partial void OnSeedToneChanged")]
    public void EachHctAxisRebuildsTheSeed(string handler)
    {
        MethodBody(ViewModelSource(), handler).Should().Contain("PushSeedToAccent",
            "an axis that does not write back to AccentColor is a slider that moves and changes nothing");
    }

    [Fact]
    public void ContrastIsSavedFromTheViewModelRatherThanCarriedPastItself()
    {
        var initializer = ApplyAndSaveInitializer();

        initializer.Should().NotMatchRegex(@"ThemeContrast\s*=\s*carried\.ThemeContrast",
            "carrying the loaded value forward was correct while nothing could set it; now that the "
            + "slider exists, carrying it would discard every move of that slider");
        initializer.Should().MatchRegex(@"ThemeContrast\s*=[^,]*ThemeContrast");
    }

    [Fact]
    public void TheSeedsChromaIsPersistedFromTheSeedItself()
    {
        var initializer = ApplyAndSaveInitializer();

        initializer.Should().MatchRegex(@"ThemeSeedChroma\s*=\s*SeedHct\.ChromaOf\(",
            "ThemeSeedChroma is what lets Android rebuild this exact seed (Theme.kt:511); reading it "
            + "off the slider instead would persist a chroma the seed does not have");
    }

    [Fact]
    public void ThereIsOneWriterForTheRecentlyUsedList()
    {
        var source = ViewModelSource();
        var body = MethodBody(source, "private void ConfirmCustomAccent");

        body.Should().Contain("CommitSeedToRecents",
            "this method used to build and save its own profile record, which is a second place that "
            + "has to remember every field ApplyAndSave carries forward");
        body.Should().NotContain("RequestSave",
            "a second saver is the deletion-by-omission shape CustomizationSettingsRoundTripTests exists for");
    }

    [Fact]
    public void TheSectionThisTestReadsIsActuallyThere()
    {
        // ANTI-VACUITY. Every assertion above is a substring search over a file this test locates by
        // relative path. If the panel is renamed or moved, they would all pass over an empty string
        // and report a screen that does not exist as fully wired.
        var markup = PanelMarkup();

        markup.Length.Should().BeGreaterThan(2000);
        markup.Should().Contain("Custom_PaletteStudio");
        StudioSection(markup).Length.Should().BeGreaterThan(500);
        ApplyAndSaveInitializer().Length.Should().BeGreaterThan(200);

        // MethodBody must stop at ITS OWN member. An injection that emptied OnSeedHueChanged used to
        // pass because the extracted "body" ran on into the two handlers below it, both of which say
        // PushSeedToAccent — a guard reading a neighbour's code and reporting on this one.
        var hueHandler = MethodBody(ViewModelSource(), "partial void OnSeedHueChanged");
        hueHandler.Should().NotContain("OnSeedChromaChanged");
        hueHandler.Should().NotContain("OnSeedToneChanged");
    }

    // ═══════════════ Helpers ═══════════════

    private static string PanelMarkup() => File.ReadAllText(Path.Combine(RepoRoot(),
        "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string ViewModelSource() => File.ReadAllText(Path.Combine(RepoRoot(),
        "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));

    /// <summary>The Palette Studio card, from its header to the start of the next section comment.</summary>
    private static string StudioSection(string markup)
    {
        var start = markup.IndexOf("Section 2: Palette Studio", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the studio's section comment is how this test finds it");

        var end = markup.IndexOf("Section 3:", start, StringComparison.Ordinal);
        return end < 0 ? markup[start..] : markup[start..end];
    }

    /// <summary>Exactly one member's body — expression-bodied or block-bodied — and nothing after it.</summary>
    /// <remarks>
    /// A FIXED-SIZE WINDOW WAS NOT GOOD ENOUGH, and an injection is what showed it. This used to take
    /// 1200 characters from the signature and search inside them, which for an EMPTY body swallowed
    /// the two handlers below it — so emptying <c>OnSeedHueChanged</c> left the test reading its
    /// neighbours' bodies and reporting the wiring intact. Brace-matching costs eight lines and
    /// cannot over-capture.
    /// </remarks>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "{0} moved or was renamed — re-point this test rather than deleting it", signature);

        var brace = source.IndexOf('{', start);
        var semicolon = source.IndexOf(';', start);
        var arrow = source.IndexOf("=>", start, StringComparison.Ordinal);

        // Expression-bodied: the "=>" arrives before any block would have opened.
        if (arrow >= 0 && semicolon >= 0 && arrow < semicolon && (brace < 0 || arrow < brace))
            return source[start..(semicolon + 1)];

        brace.Should().BeGreaterThanOrEqualTo(0, "{0} has neither a block nor an expression body", signature);

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"{signature}'s body is unterminated — the source did not parse");
    }

    private static string ApplyAndSaveInitializer()
    {
        var match = Regex.Match(ViewModelSource(),
            @"var settings = new CustomizationSettings\s*\{(.*?)\n        \};",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            "ApplyAndSave's initializer moved or was reshaped — re-point this test rather than deleting it");
        return match.Groups[1].Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
