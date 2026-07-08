using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// "Cosmic Zoom" — starfield zoom into the terminal mark + white-flash / chromatic-aberration impact.
/// STUB: draws a static brand frame; to be replaced by the pixel-exact port of SplashCosmicZoom.kt.
/// </summary>
public sealed class CosmicZoomVariant : ISplashVariant
{
    public float Duration => 3.1f;

    public void Render(SKCanvas canvas, float width, float height, float t, float dt)
    {
        SplashScene.DrawBackdrop(canvas, width, height);
        float m = MathF.Min(width, height) * 0.32f;
        SplashBrand.DrawMarkAt(canvas, width / 2f, height * 0.42f, m);
        SplashBrand.DrawWordmark(canvas, width / 2f, height * 0.64f, m * 0.30f);
    }
}
