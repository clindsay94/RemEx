using FluentAssertions;
using Remex.Branding;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

/// <summary>
/// Live Handshake renders (RemEx-8g6n0): real frames through <see cref="BrandRasterizer"/>, with the
/// field shader running on the CPU, plus the completion rules the host finishes on.
/// </summary>
public class LiveHandshakeVariantTests
{
    private const int W = 640, H = 400;

    /// <summary>The lab's "pc" scenario: three phones known, the S26 links at 0.86 s, host up at 0.40 s.</summary>
    private static HandshakeSnapshot PcScenario() => new(
        new[]
        {
            new HandshakePeer("Pixel 9 Pro", HandshakeDeviceKind.Phone, null),
            new HandshakePeer("Galaxy S26 Ultra", HandshakeDeviceKind.Phone, 0.86f),
            new HandshakePeer("Galaxy Tab S10", HandshakeDeviceKind.Tablet, null),
        },
        TargetIndex: 1, TargetAt: 0.86f, ReadyAt: 0.55f, ListeningAt: 0.40f, ListeningPort: 5005, FailedAt: null);

    // Hand-off for PcScenario: max(0.86 + LOCK_HOLD, FLOOR, 0.55) = 1.36 s; mid-exit half an EXIT later.
    private const float Handoff = 1.36f;
    private static readonly float MidExit = Handoff + LiveHandshakeDirector.Exit / 2f;

    private static LiveHandshakeVariant Variant(bool field = true)
    {
        var v = new LiveHandshakeVariant { FieldEnabled = field };
        v.Update(PcScenario());
        return v;
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(1.0f)]
    public void FramesBeforeTheHandoffAreOpaqueAndNotBlank(float t)
    {
        using var v = Variant();
        using var bmp = SKBitmap.Decode(BrandRasterizer.RenderSplashFramePng(v, W, H, t));

        bmp.Width.Should().Be(W);
        bmp.GetPixel(5, 5).Alpha.Should().Be(255, "the field covers the whole splash before the hand-off");
        bmp.GetPixel(W / 2, (int)(H * 0.46f)).Alpha.Should().Be(255);
        DistinctColours(bmp).Should().BeGreaterThan(50, "a real frame has a lattice, a mark and text, not a flat fill");
    }

    [Fact]
    public void MidExitThePortalIsAClearHoleAtTheMark()
    {
        using var v = Variant();
        using var bmp = SKBitmap.Decode(BrandRasterizer.RenderSplashFramePng(v, W, H, MidExit));

        v.HandoffAt(MidExit).Should().BeApproximately(Handoff, 0.001f);
        bmp.GetPixel(W / 2, (int)(H * 0.46f)).Alpha.Should().BeLessThan(3,
            "the live shell shows through the portal: its centre must be transparent");
        bmp.GetPixel(1, 1).Alpha.Should().Be(255, "mid-exit the corners are still outside the portal");
        DistinctColours(bmp).Should().BeGreaterThan(20, "the rim and the remaining field still draw");
    }

    [Fact]
    public void WithoutTheFieldTheFallbackBackdropStillHasThePortal()
    {
        using var v = Variant(field: false);
        using var before = SKBitmap.Decode(BrandRasterizer.RenderSplashFramePng(v, W, H, 1.0f));
        before.GetPixel(5, 5).Alpha.Should().Be(255, "the gradient fallback covers the splash on a raster lease");
        DistinctColours(before).Should().BeGreaterThan(20);

        using var mid = SKBitmap.Decode(BrandRasterizer.RenderSplashFramePng(v, W, H, MidExit));
        mid.GetPixel(W / 2, (int)(H * 0.46f)).Alpha.Should().BeLessThan(3);
    }

    [Fact]
    public void AfterTheExitNothingIsLeftCoveringTheShell()
    {
        using var v = Variant();
        float end = Handoff + LiveHandshakeDirector.Exit;
        using var bmp = SKBitmap.Decode(BrandRasterizer.RenderSplashFramePng(v, W, H, end));

        bmp.GetPixel(1, 1).Alpha.Should().BeLessThan(3);
        bmp.GetPixel(W - 2, H - 2).Alpha.Should().BeLessThan(3);
        v.ChromeOpacity.Should().BeApproximately(0f, 0.001f, "the version label and skip hint leave with the splash");
    }

    [Fact]
    public void ItCompletesOnTheDirectorsHandoffNotOnATimer()
    {
        using var v = Variant();
        v.IsComplete(Handoff + LiveHandshakeDirector.Exit - 0.01f).Should().BeFalse();
        v.IsComplete(Handoff + LiveHandshakeDirector.Exit).Should().BeTrue();
    }

    [Fact]
    public void WithTheHostNeverComingUpItStillHandsOffByTheCap()
    {
        using var v = new LiveHandshakeVariant();
        v.Update(HandshakeSnapshot.Empty);
        v.IsComplete(LiveHandshakeDirector.Cap - 0.01f).Should().BeFalse();
        v.HandoffAt(LiveHandshakeDirector.Cap).Should().BeApproximately(LiveHandshakeDirector.Cap, 0.001f);
        v.IsComplete(LiveHandshakeDirector.Cap + LiveHandshakeDirector.Exit).Should().BeTrue();
    }

    [Fact]
    public void AClickHandsOffAtOnce()
    {
        using var v = Variant();
        v.IsComplete(0.5f).Should().BeFalse();
        v.RequestSkip();
        v.HandoffAt(0.5f).Should().BeApproximately(0.5f, 0.001f);
        v.IsComplete(0.5f + LiveHandshakeDirector.Exit).Should().BeTrue();
    }

    [Fact]
    public void ReducedMotionFadesOutInsteadOfOpeningAPortal()
    {
        using var v = Variant();
        v.ReducedMotion = true;
        v.IsComplete(Handoff + LiveHandshakeDirector.FadeExit).Should().BeTrue("the reduced-motion hand-off is the short fade");

        using var bmp = SKBitmap.Decode(BrandRasterizer.RenderSplashFramePng(v, W, H, Handoff + LiveHandshakeDirector.FadeExit / 2f));
        var centre = bmp.GetPixel(W / 2, (int)(H * 0.46f)).Alpha;
        var corner = bmp.GetPixel(1, 1).Alpha;
        centre.Should().BeInRange(40, 254, "no hole at the mark: the whole splash fades together");
        corner.Should().BeInRange(40, 254, "half way through the fade, the corners are neither gone nor opaque");
    }

    [Fact]
    public void TheFixedFilmsStillFinishOnTheirDuration()
    {
        ISplashVariant cosmic = new CosmicZoomVariant();
        cosmic.IsComplete(cosmic.Duration - 0.01f).Should().BeFalse();
        cosmic.IsComplete(cosmic.Duration).Should().BeTrue();
        cosmic.ChromeOpacity.Should().Be(1f);
    }

    private static int DistinctColours(SKBitmap bmp)
    {
        var seen = new HashSet<uint>();
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
                seen.Add((uint)bmp.GetPixel(x, y));
        return seen.Count;
    }
}
