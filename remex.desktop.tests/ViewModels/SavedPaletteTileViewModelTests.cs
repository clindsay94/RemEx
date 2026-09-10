using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class SavedPaletteTileViewModelTests
{
    private static SavedPalette Dusk() => new()
    {
        Name = "Dusk", ColorSource = ColorSources.Custom, Seed = "#FF2D95", Vibrancy = 60, Contrast = 0.2, Strategy = "Expressive",
    };

    [Fact]
    public void TheTileIsPaintedFromItsOwnRecipeInTheLiveMode()
    {
        var tile = new SavedPaletteTileViewModel(Dusk());

        tile.Refresh(liveIsLight: false);
        var dark = ((SolidColorBrush)tile.SurfaceBrush).Color;
        tile.Refresh(liveIsLight: true);
        var light = ((SolidColorBrush)tile.SurfaceBrush).Color;

        dark.Should().NotBe(light, "the tile follows the window's light/dark like the preset tiles do");
        ((SolidColorBrush)tile.PrimaryBrush).Color.Should().NotBe(Colors.Transparent);
    }

    [Fact]
    public void RenamingUpdatesTheRecordAndRaisesRenamed()
    {
        var tile = new SavedPaletteTileViewModel(Dusk());
        SavedPaletteTileViewModel? raised = null;
        tile.Renamed += t => raised = t;

        tile.Name = "Evening";

        tile.Record.Name.Should().Be("Evening");
        tile.Record.Seed.Should().Be("#FF2D95", "a rename changes nothing but the name");
        raised.Should().BeSameAs(tile);
    }

    [Fact]
    public void ABlankRenameIsIgnored()
    {
        var tile = new SavedPaletteTileViewModel(Dusk());

        tile.Name = "   ";

        tile.Record.Name.Should().Be("Dusk");
        tile.Name.Should().Be("Dusk");
    }
}
