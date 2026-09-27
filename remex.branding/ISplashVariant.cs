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

    /// <summary>
    /// Whether the splash is finished at elapsed time <paramref name="t"/>. The host finishes on this,
    /// not on <see cref="Duration"/> directly, so a variant that ends on a real event (Live Handshake:
    /// the app being ready) can say so. Fixed-length variants keep the default.
    /// </summary>
    bool IsComplete(float t) => t >= Duration;

    /// <summary>
    /// Multiplier for the host-drawn chrome (version label, skip hint) on the frame just rendered.
    /// 1 for the fixed-length variants; a variant with its own exit (a portal) fades the chrome with it.
    /// </summary>
    float ChromeOpacity => 1f;
}

/// <summary>
/// A splash driven by the real startup rather than a clock alone (RemEx-8g6n0, Live Handshake). The
/// host feeds it what it observes, forwards the pointer and a click, and asks <see cref="ISplashVariant.IsComplete"/>
/// every tick. <see cref="Update"/> and <see cref="RequestSkip"/> arrive on the UI thread while
/// <see cref="ISplashVariant.Render"/> may run on the render thread, so implementations treat the
/// snapshot as an immutable value swapped whole.
/// </summary>
public interface ILiveSplashVariant : ISplashVariant
{
    /// <summary>What the host has observed so far; times are seconds on the splash's own clock.</summary>
    void Update(HandshakeSnapshot snapshot);

    /// <summary>Click/tap: hand off now, from wherever the splash is.</summary>
    void RequestSkip();

    /// <summary>Reduced motion: no pulses, no displacement, final-state nodes, a fade instead of a portal.</summary>
    bool ReducedMotion { set; }

    /// <summary>
    /// Whether the GPU field shader may run this frame. The host sets it from the leased canvas: on a
    /// raster lease the shader costs ~200 ms a frame, so the variant falls back to a gradient.
    /// </summary>
    bool FieldEnabled { set; }

    /// <summary>Pointer position for parallax, each axis in [-1, 1] (0 = centre).</summary>
    void SetPointer(float nx, float ny);
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
