using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>Cosmic Zoom is the splash default in the model, the default preset, and the Skia
/// control's fallback (spec section 8). Three places, one answer.</summary>
public class SplashDefaultTests
{
    [Fact]
    public void TheModelDefaultsToCosmicZoom()
    {
        new CustomizationSettings().SplashStyle.Should().Be("CosmicZoom");
    }

    [Fact]
    public void TheDefaultPresetCarriesCosmicZoom()
    {
        SeedPresetCatalog.Default.SplashStyle.Should().Be("CosmicZoom");
    }

    [Fact]
    public void TheSkiaControlFallsBackToCosmicZoom()
    {
        // The control needs an Avalonia runtime to construct, so its two defaults are pinned as source.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "Splash", "SkiaSplashControl.cs"));

        source.Should().Contain("nameof(SplashStyle), \"CosmicZoom\")", "the registered StyledProperty default");
        source.Should().Contain("ISplashVariant _variant = new CosmicZoomVariant();", "the pre-attach variant");
        source.Should().NotContain("nameof(SplashStyle), \"RemexCommand\")");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
