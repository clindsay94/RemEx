using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAssertions;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// Proves the headless-plus-Skia harness itself before anything trusts it to render the real shell
/// (RemEx-0e9eq): a plain window with a solid-colour child actually rasterises to a bitmap of the
/// requested size, filled with the colour it was given.
/// </summary>
public sealed class HarnessSmokeTests
{
    [AvaloniaFact]
    public void AWindowRendersAFrame()
    {
        var window = new Window
        {
            Width = 1280,
            Height = 800,
            Content = new Border { Background = Brushes.Red },
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();

        bitmap.Should().NotBeNull("a shown headless window with Skia should produce a frame");
        bitmap!.PixelSize.Should().Be(new PixelSize(1280, 800));

        var pixels = FramePixels.From(bitmap);
        var centre = pixels.ColorAt(640, 400);
        centre.Should().Be(Colors.Red, "the whole window is a red Border with nothing else painted over it");

        var whole = new PixelRect(0, 0, pixels.Size.Width, pixels.Size.Height);
        pixels.DistinctQuantisedColours(whole).Should().Be(1,
            "a single flat-red border filling the window should read as exactly one quantised colour");
        pixels.DominantCoverage(whole).Should().Be(1.0,
            "with only one colour on screen, that colour's coverage is total");
    }
}
