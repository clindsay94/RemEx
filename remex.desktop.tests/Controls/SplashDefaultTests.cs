using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>Live Handshake is the splash default in the model, the default preset, and the Skia
/// control's fallback (RemEx-8g6n0.2; before it, Cosmic Zoom, RemEx-8twk0.9). Three places, one answer.</summary>
public class SplashDefaultTests
{
    [Fact]
    public void TheModelDefaultsToLiveHandshake()
    {
        new CustomizationSettings().SplashStyle.Should().Be("LiveHandshake");
    }

    [Fact]
    public void TheDefaultPresetCarriesLiveHandshake()
    {
        SeedPresetCatalog.Default.SplashStyle.Should().Be("LiveHandshake");
    }

    [Fact]
    public void TheSkiaControlFallsBackToLiveHandshake()
    {
        // The control needs an Avalonia runtime to construct, so its two defaults are pinned as source.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "Splash", "SkiaSplashControl.cs"));

        source.Should().Contain("nameof(SplashStyle), \"LiveHandshake\")", "the registered StyledProperty default");
        source.Should().Contain("ISplashVariant _variant = new LiveHandshakeVariant();", "the pre-attach variant");
        source.Should().NotContain("nameof(SplashStyle), \"CosmicZoom\")");
        source.Should().NotContain("nameof(SplashStyle), \"RemexCommand\")");
    }

    [Fact]
    public void ThePickerOffersLiveHandshakeAndKeepsEveryOlderStyle()
    {
        // Source-pinned for the same reason: the view model needs the full DI graph to construct.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));

        source.Should().Contain("\"LiveHandshake\", \"RemexCommand\", \"CosmicZoom\", \"Pong\"",
            "Live Handshake joins the picker; Cosmic Zoom, Pong and RemEx Command stay selectable");
        _ = nameof(CustomizationViewModel.AvailableSplashStyles);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
