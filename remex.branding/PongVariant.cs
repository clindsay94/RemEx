using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// "Signal Pong" — mini-Pong whose paddles morph into the chevron + cursor; comet becomes the "R".
/// STUB: draws a static brand frame; to be replaced by the pixel-exact port of SplashPong.kt.
/// </summary>
public sealed class PongVariant : ISplashVariant
{
    public float Duration => 3.0f;

    public void Render(SKCanvas canvas, float width, float height, float t, float dt)
    {
        SplashScene.DrawBackdrop(canvas, width, height);
        float m = MathF.Min(width, height) * 0.32f;
        SplashBrand.DrawMarkAt(canvas, width / 2f, height * 0.42f, m);
        SplashBrand.DrawWordmark(canvas, width / 2f, height * 0.64f, m * 0.30f);
    }
}
