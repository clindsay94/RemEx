using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The Palette Studio's colour maths (RemEx-5u0vy). Everything the wheel and the three sliders do
/// to a seed goes through <see cref="SeedHct"/>, and it is the only part of that screen a test can
/// reach — <c>CustomizationViewModel</c> wants a shell, a layout service and a theme service, so it
/// cannot be constructed here at all.
/// </summary>
public class SeedHctTests
{
    /// <summary>
    /// The four preset seeds. Whatever the studio does to them has to end up somewhere sane, because
    /// these are the colours a user starts from.
    /// </summary>
    public static TheoryData<string> PresetSeeds() => new()
    {
        "#6C4CFF", // BaseDarkGlass
        "#00F3FF", // CyberNOC
        "#FFB800", // SolarFlare
        "#0A84FF", // Monolith
    };

    [Theory]
    [MemberData(nameof(PresetSeeds))]
    public void SplittingASeedAndPuttingItBackReturnsTheSameColour(string hex)
    {
        var seed = Color.Parse(hex);

        var (hue, chroma, tone) = SeedHct.FromColor(seed);
        var rebuilt = SeedHct.ToColor(hue, chroma, tone);

        // THE ROUND TRIP IS THE WHOLE UI CONTRACT. The sliders are derived from the seed on the way
        // in and recombined into it on the way out, so a lossy conversion means simply OPENING the
        // panel repaints the app in a slightly different colour than the one that was saved.
        //
        // Not exact equality: HCT is solved in a perceptual space and quantised back to 8-bit sRGB,
        // so a channel landing one step out is the conversion working, not failing. Anything larger
        // is visible.
        Math.Abs(rebuilt.R - seed.R).Should().BeLessThanOrEqualTo(1, "red drifted on {0}", hex);
        Math.Abs(rebuilt.G - seed.G).Should().BeLessThanOrEqualTo(1, "green drifted on {0}", hex);
        Math.Abs(rebuilt.B - seed.B).Should().BeLessThanOrEqualTo(1, "blue drifted on {0}", hex);
    }

    [Fact]
    public void AnUnreachableChromaSettlesRatherThanDriftingOnEveryPass()
    {
        // Most hue/tone pairs cannot reach chroma 120 in sRGB. Hct answers with the closest colour it
        // can render, so the FIRST pass legitimately lands below what was asked for. What must not
        // happen is that each subsequent open-and-save walks it further — that is a colour that
        // slowly changes on its own, which is the bug this pins.
        var first = SeedHct.ToColor(120.0, SeedHct.MaxChroma, 50.0);
        var (hue, chroma, tone) = SeedHct.FromColor(first);
        var second = SeedHct.ToColor(hue, chroma, tone);

        second.R.Should().BeCloseTo(first.R, 1);
        second.G.Should().BeCloseTo(first.G, 1);
        second.B.Should().BeCloseTo(first.B, 1);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(360.0, 0.0)]
    [InlineData(370.0, 10.0)]
    [InlineData(-10.0, 350.0)]
    [InlineData(-370.0, 350.0)]
    [InlineData(720.0, 0.0)]
    public void HueWrapsInsteadOfClampingAtTheSeam(double input, double expected)
    {
        // A wheel drag crosses 0/360 constantly. Clamping there would stick the thumb at red every
        // time the pointer passed the seam.
        SeedHct.NormalizeHue(input).Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonFiniteHueIsAnsweredRatherThanPropagated(double input)
    {
        // NaN % 360 is NaN, and a NaN hue reaches Hct.From, then a pixel loop, then the window.
        SeedHct.NormalizeHue(input).Should().Be(0.0);
    }

    [Theory]
    [InlineData(-50.0, 0.0, 50.0)]      // chroma below the floor
    [InlineData(50.0, 900.0, 50.0)]     // chroma above the ceiling
    [InlineData(50.0, 40.0, -20.0)]     // tone below the floor
    [InlineData(50.0, 40.0, 400.0)]     // tone above the ceiling
    public void OutOfRangeInputsAreClampedRatherThanThrowing(double hue, double chroma, double tone)
    {
        // This sits directly under a pointer drag and a bound slider. A throw here is an unhandled
        // exception on the UI thread, which on this app means an unkillable freeze (RemEx-e3pn).
        var act = () => SeedHct.ToColor(hue, chroma, tone);

        act.Should().NotThrow();
        SeedHct.ToColor(hue, chroma, tone).A.Should().Be(255, "the seed is always opaque");
    }

    [Fact]
    public void ToneZeroAndToneOneHundredAreBlackAndWhite()
    {
        // The ends of the tone slider have to be the ends of the range, or the slider has dead travel.
        var darkest = SeedHct.ToColor(200.0, 60.0, 0.0);
        var lightest = SeedHct.ToColor(200.0, 60.0, 100.0);

        darkest.Should().Be(Color.FromRgb(0, 0, 0));
        lightest.Should().Be(Color.FromRgb(255, 255, 255));
    }

    [Fact]
    public void TheHexFormIsTheSevenCharacterFormThatThemeServiceParses()
    {
        // AccentColor is persisted as this string and ThemeService runs Color.TryParse over it. The
        // LENGTH is the part that matters here: an eight-character #AARRGGBB would parse and paint
        // correctly while being a different string from the six-character form the swatches hold, so
        // the recents row would carry the same colour twice. (Case is handled separately — the row
        // compares case-insensitively, because the hex box saves whatever the user typed.)
        var hex = SeedHct.ToHex(265.0, 60.0, 55.0);

        hex.Should().MatchRegex("^#[0-9A-F]{6}$");
        Color.TryParse(hex, out _).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(PresetSeeds))]
    public void ChromaOfReturnsTheSeedsOwnChromaSoTheAndroidFormulaReproducesIt(string hex)
    {
        // WHY ThemeSeedChroma IS WRITTEN AT ALL. Android builds its seed as
        // Hct.from(seedHue, themeSeedChroma, seedTone) (Theme.kt:511). That reproduces the desktop's
        // seed exactly only while the persisted chroma is the seed's OWN chroma — persist the
        // slider's requested value instead and the two platforms paint different colours from one
        // saved profile.
        var seed = Color.Parse(hex);
        var persisted = SeedHct.ChromaOf(hex, fallback: -1.0);

        persisted.Should().NotBe(-1.0);

        var (hue, _, tone) = SeedHct.FromColor(seed);
        var androidRebuild = SeedHct.ToColor(hue, persisted, tone);

        androidRebuild.R.Should().BeCloseTo(seed.R, 1);
        androidRebuild.G.Should().BeCloseTo(seed.G, 1);
        androidRebuild.B.Should().BeCloseTo(seed.B, 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a colour")]
    [InlineData("#FF0O00")] // a capital O for a zero — seven characters, and not a colour
    public void ChromaOfKeepsTheCarriedValueWhenTheSeedWillNotParse(string? hex)
    {
        // The accent is a bad string for exactly as long as it takes someone to type a good one.
        // Answering 0 there would persist "grey" over a perfectly good saved chroma.
        SeedHct.ChromaOf(hex, fallback: 48.0).Should().Be(48.0);
    }

    [Fact]
    public void TheDefaultSeedIsNotBlackSoAnUnparseableAccentDoesNotOpenOnOne()
    {
        // WHAT THE VIEW MODEL FALLS BACK TO AT CONSTRUCTION, pinned here because the failure it
        // prevents is silent. Mid-edit the right answer is to leave the axes alone; at construction
        // there is nothing to leave alone, and the C# default of 0/0/0 is not neutral, it is BLACK.
        // A profile carrying "#FF0O00" survives a restart (RemEx-07jij), so a studio that opened on
        // 0/0/0 would show a solid black disc and push #000000 over the app on the first arrow key.
        var (hue, chroma, tone) = SeedHct.FromColor(ThemeService.FallbackAccentColor);

        tone.Should().BeGreaterThan(0, "a fallback of tone 0 is black, which is the bug");
        chroma.Should().BeGreaterThan(0, "a fallback with no chroma is grey — the seed has a colour");

        // And it has to be the same seed the painting side falls back to, or the sliders describe a
        // window that is not there.
        SeedHct.ToColor(hue, chroma, tone).Should().Be(Color.Parse(ThemeService.FallbackAccentSeed));
    }

    [Fact]
    public void HexOutputIsUpperCaseSoTheRecentsListCanDeduplicateAgainstTypedInput()
    {
        // The recents list compares case-insensitively for this reason; this pins the other half, so
        // that the two writers at least AGREE on a canonical form rather than both being arbitrary.
        //
        // A SWEEP, NOT ONE SAMPLE, AND IT COUNTS THE LETTERS. Asserting case over a single colour is
        // vacuous whenever that colour's hex happens to be all digits — an injection that lower-cased
        // the format string passed exactly that way. Only a value containing A–F can tell the two
        // cases apart, so the test checks that it actually saw some.
        var samples = Enumerable.Range(0, 36)
            .Select(i => SeedHct.ToHex(i * 10, 60, 50))
            .ToArray();

        samples.Should().OnlyContain(hex => Regex.IsMatch(hex, "^#[0-9A-F]{6}$"));

        samples.Count(hex => hex.Any(char.IsLetter))
            .Should().BeGreaterThan(0, "with no letters anywhere, the case assertion above proves nothing");
    }
}
