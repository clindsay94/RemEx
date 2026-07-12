using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// One splash animation. Pure SkiaSharp so a frame can be rendered to a PNG for verification
/// without a running Avalonia app. The host control owns the frame loop, tap-to-skip, version
/// label, and completion; a variant just advances its own state and draws the current frame.
/// Fixed brand palette (SplashBrand) — never theme-adaptive.
/// </summary>
public interface ISplashVariant
{
    /// <summary>Total animation length in seconds; the control finishes once elapsed ≥ Duration.</summary>
    float Duration { get; }

    /// <summary>
    /// Advance internal state by <paramref name="dt"/> seconds and draw the frame at elapsed time
    /// <paramref name="t"/> into a <paramref name="width"/>×<paramref name="height"/> area
    /// (origin top-left). Called ~60×/s by the host; for offline PNG rendering, call repeatedly
    /// with a fixed dt up to the desired t.
    /// </summary>
    void Render(SKCanvas canvas, float width, float height, float t, float dt);
}

/// <summary>Shared helpers for the splash variants (backdrop, etc.).</summary>
public static class SplashScene
{
    /// <summary>Fill the whole area with the diagonal brand backdrop gradient (top-left → bottom-right).</summary>
    public static void DrawBackdrop(SKCanvas canvas, float width, float height)
    {
        using var bg = new SKPaint { IsAntialias = true };
        bg.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(width, height),
            new[] { SplashBrand.BackdropStart, SplashBrand.BackdropEnd }, null, SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, bg);
    }
}
