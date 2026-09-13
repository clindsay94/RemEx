using System;
using System.Linq;
using Remex.Core.Models;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// RemEx-4kv0g.3, the 2026-09-13 preset wipe: a layout that carries no card themes (the phone's
/// copy) must not strip the themes a stored/live layout already has when it is written over it.
/// </summary>
public class CardThemeMergeTests
{
    private static SensorCardTheme Sunset => SensorCardTheme.Presets[4];
    private static SensorCardTheme Default => SensorCardTheme.Presets[0];

    [Fact]
    public void AThemelessIncomingCardTakesTheStoredThemeByCardId()
    {
        var existing = new[] { new CardState { CardId = "c1", SensorId = "CPU", CardTheme = Sunset } };
        var incoming = new[] { new CardState { CardId = "c1", SensorId = "CPU", CardTheme = null, PositionX = 300 } };

        var merged = CardThemeMerge.PreserveThemes(incoming, existing);

        Assert.Single(merged);
        Assert.Equal("Sunset", merged[0].CardTheme?.Name);
        Assert.Equal(300, merged[0].PositionX);   // everything else from the incoming card
    }

    [Fact]
    public void AThemelessIncomingCardFallsBackToTheStoredThemeBySensorIdWhenTheIdWasReKeyed()
    {
        var existing = new[] { new CardState { CardId = "old", SensorId = "GPU", CardTheme = Sunset } };
        var incoming = new[] { new CardState { CardId = "new", SensorId = "GPU", CardTheme = null } };

        var merged = CardThemeMerge.PreserveThemes(incoming, existing);

        Assert.Equal("Sunset", merged[0].CardTheme?.Name);
    }

    [Fact]
    public void AnIncomingThemeWinsOverTheStoredOne()
    {
        var existing = new[] { new CardState { CardId = "c1", SensorId = "CPU", CardTheme = Sunset } };
        var incoming = new[] { new CardState { CardId = "c1", SensorId = "CPU", CardTheme = Default } };

        var merged = CardThemeMerge.PreserveThemes(incoming, existing);

        Assert.Equal("Default", merged[0].CardTheme?.Name);
    }

    [Fact]
    public void ACardWithNoCounterpartStaysThemeless()
    {
        var existing = new[] { new CardState { CardId = "c1", SensorId = "CPU", CardTheme = Sunset } };
        var incoming = new[] { new CardState { CardId = "c9", SensorId = "RAM", CardTheme = null } };

        var merged = CardThemeMerge.PreserveThemes(incoming, existing);

        Assert.Null(merged[0].CardTheme);
    }

    [Fact]
    public void NullOrEmptyInputsAreHandled()
    {
        Assert.Empty(CardThemeMerge.PreserveThemes(null, null));
        var incoming = new[] { new CardState { CardId = "c1", CardTheme = null } };
        Assert.Single(CardThemeMerge.PreserveThemes(incoming, null));
        Assert.Single(CardThemeMerge.PreserveThemes(incoming, Array.Empty<CardState>()));
    }

    [Fact]
    public void TheWholeWipeScenario_APhoneLayoutOverAThemedHostCopy_KeepsEveryPreset()
    {
        // 44 cards with presets on the host; the phone pushes the same 44 with no themes at all.
        var themed = Enumerable.Range(0, 44).Select(i => new CardState { CardId = $"c{i}", SensorId = $"s{i}", CardTheme = SensorCardTheme.Presets[1 + i % 7] }).ToList();
        var phone = themed.Select(c => c with { CardTheme = null, PositionX = c.PositionX + 50 }).ToList();

        var merged = CardThemeMerge.PreserveThemes(phone, themed);

        Assert.Equal(44, merged.Count);
        Assert.All(merged, c => Assert.NotNull(c.CardTheme));
        Assert.Equal(themed.Select(c => c.CardTheme!.Name), merged.Select(c => c.CardTheme!.Name));
        Assert.All(merged, c => Assert.Equal(50, c.PositionX));   // the phone's move still lands
    }
}
