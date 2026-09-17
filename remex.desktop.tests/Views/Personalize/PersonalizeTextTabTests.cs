using System.Linq;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The Text tab (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): the RemEx-jt6w5 TEXT block
/// as-is (through Reset), then the new FONTS sub-heading, page-title font and content font lifted
/// out of the retired Advanced tuning disclosure. Source-text, like
/// <see cref="PersonalizationTextSectionTests"/> and its siblings — <c>remex.desktop.tests</c> has
/// no headless render.
/// </summary>
public class PersonalizeTextTabTests
{
    private const string FileName = "PersonalizeTextTab.axaml";

    [Fact]
    public void TheControlsAppearInSpecOrder()
    {
        var markup = Markup();
        var keys = new[]
        {
            "Custom_TextHeaders", "Custom_TextSubtitles", "Custom_TextBody", "Custom_TextSmall", "Custom_TextSensor",
            "Custom_TextSensorBackdrop", "Custom_TextShadow", "Custom_TextShadowStrength",
            "Custom_TextReset", "Custom_SectionFonts", "Custom_PageTitleFont", "Custom_SubtitleFont", "Custom_ContentFont",
        };
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(
            "spec §2 (RemEx-n6csl): Headers, then Subtitles, then the rest of the TEXT block through " +
            "Reset, then the FONTS sub-heading, page-title font, subtitle font, content font");
    }

    [Fact]
    public void TheFontsSubHeadingIsTheOnlyHeaderThisTabCarries()
    {
        var markup = Markup();
        markup.Should().NotContain("Custom_SectionText",
            "the tab strip is the header now - the TEXT block's own section header is retired");
        markup.Should().Contain("Localize Custom_SectionFonts}",
            "the FONTS sub-heading is new (spec §2/§5) and reuses today's header markup");
    }

    [Fact]
    public void NoInlineFontSizeBeyondWhatThisTabInherited() => AssertInlineFontSizeCountIs(Markup(), 2);

    [Fact]
    public void NoLiteralColour() => AssertNoLiteralColour(Markup());

    [Fact]
    public void EveryVisibleStringRoutesThroughLocalizeOrABinding() => AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(Markup());

    [Fact]
    public void RootsInAScrollViewer() => AssertRootsInScrollViewer(Markup());

    private static string Markup() => TabMarkup(FileName);
}
