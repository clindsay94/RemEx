using SkiaSharp;

namespace Remex.Branding;

/// <summary>Which world a fixed-length splash plays in: the film field's <c>uStyle</c>.</summary>
public enum SplashFilmStyle
{
    /// <summary>RemEx Command: a command deck with a rolling grid floor and falling data columns.</summary>
    Command = 0,

    /// <summary>Cosmic Zoom: nebula and three star layers that stretch into a warp.</summary>
    Cosmic = 1,

    /// <summary>Pong: a phosphor CRT court that ripples on every contact.</summary>
    Pong = 2,
}

/// <summary>
/// A beat: a real moment in a film (an impact, a paddle contact, the session coming up) that sends
/// a shockwave through the field. Times are seconds on the splash clock; a strength of 0 is no beat.
/// </summary>
public readonly record struct FilmBeat(float X, float Y, float At, float Strength)
{
    /// <summary>No beat this frame.</summary>
    public static FilmBeat None => default;
}

/// <summary>
/// The film splashes' backdrop field (RemEx-pp4cm.11): <c>Shaders/splash_film_field.sksl</c>, embedded
/// and compiled once per process. Byte-identical with the Android <c>splash_film_field.agsl</c>; a
/// parity test compares the two files, so edit both together or not at all.
/// </summary>
/// <remarks>
/// Colours come from the palette <see cref="SplashBrand.ApplyPalette"/> installed (the user's seed),
/// with the lab slates standing in for the brand default. The field is additive light, so a light
/// palette gets a dark ground mixed from its own primary rather than a washed-out light one; the
/// films still fade out to the palette's backdrop, which is the app's own surface.
/// </remarks>
public static class SplashFilmField
{
    private static readonly EmbeddedRuntimeShader Shader = new("Remex.Branding.Shaders.splash_film_field.sksl");

    /// <summary>Every uniform the shader declares, in declaration order.</summary>
    public static IReadOnlyList<string> UniformNames { get; } = new[]
    {
        "uRes", "uTime", "uPx", "uCenter", "uBg0", "uBg1", "uPri", "uSec", "uAcc", "uStyle",
        "uBeat", "uDrive", "uStill", "uAlpha",
    };

    /// <summary>Reduced motion: how long the designed still frame holds before it fades out.</summary>
    public const float StillHold = 1.35f;

    /// <summary>Reduced motion: the still frame's fade into the app.</summary>
    public const float StillFade = 0.25f;

    /// <summary>A film's whole length under reduced motion.</summary>
    public const float StillDuration = StillHold + StillFade;

    // The lab's slates, used while the brand default palette is active (its mark colours are the
    // near-black window fill, which would light nothing).
    private static readonly SKColor DefaultPrimary = new(0xFF93A4C6);
    private static readonly SKColor DefaultSecondary = new(0xFF6F8BBF);

    /// <summary>The shader source as embedded.</summary>
    public static string Source => Shader.Source;

    /// <summary>The compiled effect, or null when compilation failed.</summary>
    public static SKRuntimeEffect? Effect => Shader.Effect;

    /// <summary>The compiler's message when <see cref="Effect"/> is null.</summary>
    public static string? CompileErrors => Shader.CompileErrors;

    /// <summary>Compiles the effect off the render path, once. Idempotent.</summary>
    public static void WarmUp() => Shader.WarmUp();

    /// <summary>
    /// Draws the field over 0..<paramref name="width"/> x 0..<paramref name="height"/> in the canvas's
    /// current coordinates. Falls back to the plain brand gradient when the host says the field may not
    /// run (<paramref name="enabled"/> false: a raster canvas, where a runtime shader costs ~200 ms a
    /// frame) or the shader did not compile.
    /// </summary>
    /// <param name="u">Pixels per dp at this canvas's scale.</param>
    /// <param name="center">The style's focal point: the floor's vanishing point, the warp's origin, the net.</param>
    /// <param name="drive">The build-up toward the film's beat, 0..1.</param>
    /// <param name="still">Reduced motion: every moving layer frozen at <paramref name="t"/>, no beat.</param>
    public static void Draw(
        SKCanvas canvas, float width, float height, SplashFilmStyle style, float t, float u,
        SKPoint center, float drive, in FilmBeat beat, bool still, bool enabled)
    {
        if (width <= 0 || height <= 0) return;
        var effect = enabled ? Shader.Effect : null;
        if (effect is null)
        {
            SplashScene.DrawBackdrop(canvas, width, height);
            return;
        }

        var (bg0, bg1, pri, sec) = Ground();
        using var uni = new SKRuntimeEffectUniforms(effect);
        uni["uRes"] = new[] { width, height };
        uni["uTime"] = t;
        uni["uPx"] = Math.Max(0.25f, u);
        uni["uCenter"] = new[] { center.X, center.Y };
        uni["uBg0"] = Rgb(bg0);
        uni["uBg1"] = Rgb(bg1);
        uni["uPri"] = Rgb(pri);
        uni["uSec"] = Rgb(sec);
        uni["uAcc"] = Rgb(SplashBrand.Amber);
        uni["uStyle"] = (float)(int)style;
        uni["uBeat"] = still || beat.Strength <= 0f
            ? new float[4]
            : new[] { beat.X, beat.Y, beat.At, beat.Strength };
        uni["uDrive"] = Math.Clamp(drive, 0f, 1f);
        uni["uStill"] = still ? 1f : 0f;
        uni["uAlpha"] = 1f;

        using var shader = effect.ToShader(uni);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, width, height, paint);
    }

    /// <summary>
    /// The designed still frame a film shows under reduced motion: its world frozen at a hero moment
    /// with the settled mark and wordmark over it, held for <see cref="StillHold"/> and then faded to
    /// the palette's backdrop (the app's surface) over <see cref="StillFade"/>. No camera moves, no
    /// beat, nothing that travels.
    /// </summary>
    public static void DrawStill(SKCanvas canvas, float width, float height, SplashFilmStyle style, float t, bool enabled)
    {
        float cx = width / 2f, cy = height / 2f;
        float u = MathF.Min(width, height) / 520f;
        var (heroT, heroDrive) = style switch
        {
            SplashFilmStyle.Command => (1.6f, 0.55f),
            SplashFilmStyle.Cosmic => (1.2f, 0.35f),
            _ => (1.0f, 0.7f),
        };
        // The same settled lockup Cosmic Zoom lands on: the mark above, the wordmark below.
        float markCy = cy - 120f * u;
        var focus = style == SplashFilmStyle.Command ? new SKPoint(cx, height * 0.56f) : new SKPoint(cx, markCy);
        Draw(canvas, width, height, style, heroT, u, focus, heroDrive, FilmBeat.None, still: true, enabled);
        SplashBrand.DrawMarkAt(canvas, cx, markCy, 432f * u, 1f);
        SplashBrand.DrawWordmark(canvas, cx, cy + 81f * u, 40f * u, 1f);

        float fade = Math.Clamp((t - StillHold) / StillFade, 0f, 1f);
        if (fade > 0f)
        {
            using var fp = new SKPaint { Color = SplashBrand.BackdropStart.WithAlpha((byte)(fade * 255f)) };
            canvas.DrawRect(0, 0, width, height, fp);
        }
    }

    /// <summary>The ground and lights for the current palette (see the class remarks).</summary>
    private static (SKColor Bg0, SKColor Bg1, SKColor Pri, SKColor Sec) Ground()
    {
        bool brandDefault = SplashBrand.MarkStart == SplashBrand.WindowFill;
        var pri = brandDefault ? DefaultPrimary : SplashBrand.MarkStart;
        var sec = brandDefault || SplashBrand.MarkEnd == SplashBrand.WindowFill ? DefaultSecondary : SplashBrand.MarkEnd;
        var bg0 = SplashBrand.BackdropStart;
        var bg1 = SplashBrand.BackdropEnd;
        if (Luma(bg0) > 0.55f)
        {
            bg0 = Mix(pri, SKColors.Black, 0.88f);
            bg1 = Mix(pri, SKColors.Black, 0.76f);
        }
        return (bg0, bg1, pri, sec);
    }

    private static float Luma(SKColor c) => (0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue) / 255f;

    private static SKColor Mix(SKColor a, SKColor b, float f) => new(
        (byte)(a.Red + (b.Red - a.Red) * f),
        (byte)(a.Green + (b.Green - a.Green) * f),
        (byte)(a.Blue + (b.Blue - a.Blue) * f));

    private static float[] Rgb(SKColor c) => new[] { c.Red / 255f, c.Green / 255f, c.Blue / 255f };
}
