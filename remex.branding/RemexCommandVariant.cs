using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// "Original Scan" — phone→monitor connection revealed by 3 top-to-bottom sweeps + terminal typing.
/// STUB: draws a static brand frame; to be replaced by the pixel-exact port of SplashRemexCommand.kt.
/// </summary>
public sealed class RemexCommandVariant : ISplashVariant
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
