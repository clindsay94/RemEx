using FluentAssertions;
using Remex.Branding;
using Remex.Desktop.Controls.Splash;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Renders Live Handshake the way the app does - the localized text, the console, every frame from
/// the first to past the hand-off - and requires every frame to render without throwing and to leave
/// the canvas state balanced. A draw op that throws mid-frame leaves the leased canvas saved/clipped
/// and blanks the whole window (RemEx-8g6n0.3 regression: a pure black window over a live shell).
/// </summary>
public class LiveHandshakeRenderPathTests
{
    private static HandshakeSnapshot PcScenario() => new(
        new[]
        {
            new HandshakePeer("Pixel 9 Pro", HandshakeDeviceKind.Phone, null),
            new HandshakePeer("Galaxy S26 Ultra", HandshakeDeviceKind.Phone, 0.86f),
            new HandshakePeer("Galaxy Tab S10", HandshakeDeviceKind.Tablet, null),
        },
        TargetIndex: 1, TargetAt: 0.86f, ReadyAt: 0f, ListeningAt: 0f, ListeningPort: 5005, FailedAt: null);

    public static TheoryData<bool, bool> Modes() => new()
    {
        { true, false }, { false, false }, { true, true }, { false, true },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryFrameRendersWithTheLocalizedConsoleAndLeavesTheCanvasBalanced(bool field, bool reduced)
    {
        using var variant = new LiveHandshakeVariant(new LocalizedHandshakeText()) { FieldEnabled = field, ReducedMotion = reduced };
        variant.Update(PcScenario());
        using var surface = SKSurface.Create(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        for (float t = 0f; t < 3.6f; t += 0.05f)
        {
            int saves = canvas.SaveCount;
            var act = () => variant.Render(canvas, 300, 200, t, 0.05f);
            act.Should().NotThrow($"frame at t={t:0.00}");
            canvas.SaveCount.Should().Be(saves, $"frame at t={t:0.00} must restore every save it makes");
        }
    }
}
