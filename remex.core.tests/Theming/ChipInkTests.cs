using Remex.Core.Theming;
using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>
/// <see cref="ChipInk"/> (spec 2026-09-13 personalize-tabs §3, RemEx-9ql7v): black at tone 50 and
/// above, white below. The tone-80/tone-40 pairs below are the same accent-role tones
/// <c>DynamicColorGenerator</c> assigns Dark-mode and Light-mode primaries/secondaries/tertiaries
/// respectively, so this pins ChipInk to agree with the palette engine, not an independent guess.
/// </summary>
public class ChipInkTests
{
    [Theory]
    [InlineData(0xFF84DD00u)] // Dark tone-80 primary-family role
    [InlineData(0xFFFFB2B8u)] // Dark tone-80 role
    [InlineData(0xFFAFCFABu)] // Dark tone-80 role
    public void ToneEightyRoles_GetBlackInk(uint argb)
    {
        Assert.True(Hct.FromInt(argb).Tone >= 50, "fixture must actually be a tone-80-ish role");
        Assert.Equal(0xFF000000u, ChipInk.For(argb));
    }

    [Theory]
    [InlineData(0xFF3D6A00u)] // Light tone-40 role
    [InlineData(0xFF934750u)] // Light tone-40 role
    [InlineData(0xFF23695Au)] // Light tone-40 role
    public void ToneFortyRoles_GetWhiteInk(uint argb)
    {
        Assert.True(Hct.FromInt(argb).Tone < 50, "fixture must actually be a tone-40-ish role");
        Assert.Equal(0xFFFFFFFFu, ChipInk.For(argb));
    }

    [Fact]
    public void White_GetsBlackInk() => Assert.Equal(0xFF000000u, ChipInk.For(0xFFFFFFFFu));

    [Fact]
    public void Black_GetsWhiteInk() => Assert.Equal(0xFFFFFFFFu, ChipInk.For(0xFF000000u));

    /// <summary>
    /// The documented boundary: tone == 50 resolves to black (the "&gt;=", not "&gt;", in
    /// <see cref="ChipInk.For"/>). Constructed through the HCT solver itself, the way
    /// <c>McuSchemeTests</c> builds its own tone-50 fixture, rather than hand-picked so the solver's
    /// actual round-trip is what's under test.
    /// </summary>
    [Fact]
    public void ToneFifty_IsTheBlackSideOfTheBoundary()
    {
        var argb = Hct.From(50.0, 40.0, 50.0).ToInt();
        Assert.True(Hct.FromInt(argb).Tone >= 50, "the fixture must actually land on or above the boundary");
        Assert.Equal(0xFF000000u, ChipInk.For(argb));
    }
}
