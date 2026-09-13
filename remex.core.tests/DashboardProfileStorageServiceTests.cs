using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// The host store's read-merge-write (RemEx-4kv0g.3, the 2026-09-13 preset wipe), driven through
/// the real <see cref="DashboardProfileStorageService"/> on a temp file — never the machine-wide
/// ProgramData store (RemEx-4u29).
/// </summary>
public sealed class DashboardProfileStorageServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("remex-hoststore-").FullName;
    private DashboardProfileStorageService Store() => new(Path.Combine(_dir, "host_dashboard_layout.json"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    private static DashboardProfile Themed() => new()
    {
        Cards = new List<CardState>
        {
            new() { CardId = "c1", CardType = "Sensor", SensorId = "CPU", CardTheme = SensorCardTheme.Presets[4] },   // Sunset
            new() { CardId = "c2", CardType = "Sensor", SensorId = "GPU", CardTheme = SensorCardTheme.Presets[6] },   // Neon Purple
        },
    };

    [Fact]
    public async Task ASaveWithNoCardThemesKeepsTheStoredOnes()
    {
        var store = Store();
        await store.SaveProfileAsync(Themed());

        var themeless = Themed() with { Cards = Themed().Cards.Select(c => c with { CardTheme = null, PositionX = 300 }).ToList() };
        await store.SaveProfileAsync(themeless);

        var loaded = await store.LoadProfileAsync();
        Assert.Equal("Sunset", loaded.Cards.Single(c => c.CardId == "c1").CardTheme?.Name);
        Assert.Equal("Neon Purple", loaded.Cards.Single(c => c.CardId == "c2").CardTheme?.Name);
        Assert.All(loaded.Cards, c => Assert.Equal(300, c.PositionX));   // the rest of the update still lands
    }

    [Fact]
    public async Task ASaveWithAThemeReplacesTheStoredOne()
    {
        var store = Store();
        await store.SaveProfileAsync(Themed());

        var recoloured = Themed() with { Cards = Themed().Cards.Select(c => c with { CardTheme = SensorCardTheme.Presets[0] }).ToList() };
        await store.SaveProfileAsync(recoloured);

        var loaded = await store.LoadProfileAsync();
        Assert.All(loaded.Cards, c => Assert.Equal("Default", c.CardTheme?.Name));
    }

    [Fact]
    public async Task TheFirstSaveIntoAnEmptyStoreWritesTheProfileAsIs()
    {
        var store = Store();
        var themeless = Themed() with { Cards = Themed().Cards.Select(c => c with { CardTheme = null }).ToList() };

        await store.SaveProfileAsync(themeless);

        var loaded = await store.LoadProfileAsync();
        Assert.Equal(2, loaded.Cards.Count);
        Assert.All(loaded.Cards, c => Assert.Null(c.CardTheme));
    }
}
