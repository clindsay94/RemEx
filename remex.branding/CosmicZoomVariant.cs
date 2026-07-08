using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// "Cosmic Zoom" — starfield zoom into the terminal mark + white-flash / chromatic-aberration impact.
/// Pixel-exact port of the Android SplashCosmicZoom.kt: 25-point starfield streaming radially out of
/// centre, faint amber HUD (concentric rings + crosshair), a hero terminal mark that eases up from
/// scale ~0.1 toward rest, then a 1.8s IMPACT (white flash, deterministic camera shake, elastic punch,
/// amber shockwave + gold ring, additive RGB-split chromatic bloom), the two-colour "RemEx" wordmark +
/// "⚡ COMMAND CENTER" tagline revealing below, and a fade-to-backdrop exit. Fixed brand palette
/// (SplashBrand) plus the transient impact accents present in the Android source; never theme colours.
/// Fully deterministic — no Random/DateTime; particles seed by index, shake via sin(t).
/// </summary>
public sealed class CosmicZoomVariant : ISplashVariant
{
    // Transient impact accents — verbatim from SplashCosmicZoom.kt (allowed non-palette colours).
    private static readonly SKColor Gold = new(0xFF, 0xD7, 0x00);        // #FFD700 inner shock ring
    private static readonly SKColor AberrRed = new(0xFF, 0x2D, 0x55);    // #FF2D55 chromatic red
    private static readonly SKColor AberrCyan = new(0x00, 0xE5, 0xFF);   // #00E5FF chromatic cyan
    private static readonly SKColor AberrGreen = new(0x45, 0xFF, 0x8F);  // #45FF8F chromatic green

    // Resting size of the hero mark — matches Android's bumped-up restScale for real presence.
    private const float RestScale = 4.0f;
    private const float ImpactAt = 1.8f;   // strike time (s)

    private Particle[]? _particles;
    private readonly DeterministicRandom _respawnRng = new(99); // Android used Random(99L) for respawns

    /// <summary>~3.1s: exit fade completes at 3.0s, +0.1s tail — identical to the Android source.</summary>
    public float Duration => 3.1f;

    public void Render(SKCanvas canvas, float width, float height, float t, float dt)
    {
        EnsureParticles();

        float cx = width / 2f, cy = height / 2f;
        // dp→px scale for this canvas. The Android layout is expressed in dp/density; `u` replaces the
        // phone's `density` so the whole composition (hero offsets, wordmark, strokes) scales together.
        float u = MathF.Min(width, height) / 520f;

        // ── Backdrop: full-bleed diagonal brand gradient ──
        SplashScene.DrawBackdrop(canvas, width, height);

        // ── Cosmic starfield: advance one frame, then draw ──
        UpdateParticles(dt);
        SplashFx.DrawStarfield(canvas, width, height, _particles!);

        // ── Faint amber HUD: 4 concentric target rings + centre crosshair (Amber @ ~0.05) ──
        SKColor hud = SplashBrand.Amber.WithAlpha(A(0.05f));
        float maxRad = MathF.Min(width, height) * 0.45f;
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f * u, Color = hud })
        {
            foreach (float rf in new[] { 0.25f, 0.45f, 0.65f, 0.85f })
                canvas.DrawCircle(cx, cy, rf * maxRad, ring);
            canvas.DrawLine(cx - 20f * u, cy, cx + 20f * u, cy, ring);
            canvas.DrawLine(cx, cy - 20f * u, cx, cy + 20f * u, ring);
        }

        // ── Expanding energy shockwave (impact): amber ring + inner gold ring ──
        if (t > ImpactAt)
        {
            float waveT = Clamp01((t - ImpactAt) / 0.5f);
            float shockRadius = (40f + waveT * 280f) * u;
            float shockOpacity = 1f - waveT;
            using var amberWave = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (3f + (1f - waveT) * 8f) * u,
                Color = SplashBrand.Amber.WithAlpha(A(shockOpacity * 0.6f)),
            };
            canvas.DrawCircle(cx, cy, shockRadius, amberWave);
            using var goldWave = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (1.5f + (1f - waveT) * 4f) * u,
                Color = Gold.WithAlpha(A(shockOpacity * 0.3f)),
            };
            canvas.DrawCircle(cx, cy, shockRadius * 0.7f, goldWave);
        }

        // ── Cinematic zoom / impact camera state ──
        float zoomScaleVal;
        float shudderX, shudderY;
        float flashOverlay;
        if (t < ImpactAt)
        {
            float tt = t / ImpactAt;
            // Ease-in toward just under rest; the strike pops it the rest of the way (0.1 + tt²·3.7).
            zoomScaleVal = 0.1f + tt * tt * (RestScale - 0.3f);
            shudderX = 0f; shudderY = 0f; flashOverlay = 0f;
        }
        else
        {
            float strikeElapsed = t - ImpactAt;
            const float strikeDuration = 0.6f;
            flashOverlay = strikeElapsed < strikeDuration ? 1f - (strikeElapsed / strikeDuration) : 0f;

            // Punchy camera shake, ~30px→0 over 0.6s. Deterministic: high-frequency sin (not Random),
            // ±0.5 amplitude to mirror Android's (rand-0.5)·intensity jitter.
            float shakeIntensity = strikeElapsed < strikeDuration ? (1f - (strikeElapsed / strikeDuration)) * 30f : 0f;
            shudderX = MathF.Sin(t * 91.7f) * shakeIntensity * u * 0.5f;
            shudderY = MathF.Sin(t * 113.3f + 2.1f) * shakeIntensity * u * 0.5f;

            // Impact "pop": elastic overshoot (+0.85) over 0.32s then subtle breathing.
            float punch = strikeElapsed < 0.32f ? MathF.Sin((strikeElapsed / 0.32f) * MathF.PI) * 0.85f : 0f;
            zoomScaleVal = RestScale + punch + MathF.Sin((t - ImpactAt) * 3f) * 0.05f;
        }

        // ── Hero terminal mark: same transform (scale / shudder / punch / up-offset) as Android ──
        float heroScale = zoomScaleVal * u;
        float heroX = cx + shudderX;
        float heroY = cy - 30f * heroScale + shudderY;
        SplashBrand.DrawMarkAt(canvas, heroX, heroY, 108f * heroScale, 1f);

        // ── Wordmark + tagline reveal: fade + rise below the settled mark (staggered) ──
        if (t > ImpactAt)
        {
            float wordmarkIn = FastOutSlowIn(Clamp01((t - 1.85f) / 0.35f));
            float taglineIn = FastOutSlowIn(Clamp01((t - 2.15f) / 0.35f));
            float rise = 12f * u;

            SplashBrand.DrawWordmark(canvas, cx, cy + 81f * u + (1f - wordmarkIn) * rise, 40f * u, wordmarkIn);

            // Tagline "⚡ COMMAND CENTER": a small amber lightning bolt + slate caps as one centred
            // lockup. The bolt is a vector glyph (not the emoji) so it renders identically on
            // Windows/Linux — Victor Mono has no ⚡ and SkiaSharp does no font fallback (Compose does).
            const string tagline = "COMMAND CENTER";
            float tagSize = 11f * u;
            float tagBaseline = cy + 113f * u + (1f - taglineIn) * rise;
            float tagW = SplashBrand.MeasureText(tagline, tagSize);
            float boltH = tagSize * 1.05f, boltW = boltH * 0.55f, gap = tagSize * 0.35f;
            float lockLeft = cx - (boltW + gap + tagW) / 2f;
            DrawBolt(canvas, lockLeft + boltW / 2f, tagBaseline - tagSize * 0.34f, boltW, boltH,
                SplashBrand.Amber.WithAlpha(A(taglineIn)));
            SplashBrand.DrawText(canvas, tagline, cx + (boltW + gap) / 2f, tagBaseline, tagSize,
                SplashBrand.SlateLo, taglineIn);
        }

        // ── Softened full-screen white arrival flash on impact ──
        if (flashOverlay > 0f)
        {
            using var flash = new SKPaint { Color = SKColors.White.WithAlpha(A(flashOverlay * 0.4f)) };
            canvas.DrawRect(0, 0, width, height, flash);
        }

        // ── Chromatic-aberration bloom: 3 additive RGB-split rings bursting out over 0.25s ──
        float bloomT = (t - ImpactAt) / 0.25f;
        if (bloomT >= 0f && bloomT <= 1f)
        {
            float bloomAlpha = (1f - bloomT) * 0.55f;
            float bloomRadius = 40f * u + bloomT * 360f * u;
            float split = (1f - bloomT) * 16f * u;
            float bloomWidth = (2f + (1f - bloomT) * 7f) * u;
            // Fixed centre concentric with the hero at rest (Android's cy − 30·restScale, mapped to px).
            float bloomCy = cy - 30f * RestScale * u;

            using var red = AberrPaint(bloomWidth, AberrRed.WithAlpha(A(bloomAlpha)));
            canvas.DrawCircle(cx - split, bloomCy, bloomRadius, red);
            using var cyanPaint = AberrPaint(bloomWidth, AberrCyan.WithAlpha(A(bloomAlpha)));
            canvas.DrawCircle(cx + split, bloomCy, bloomRadius, cyanPaint);
            using var green = AberrPaint(bloomWidth, AberrGreen.WithAlpha(A(bloomAlpha * 0.8f)));
            canvas.DrawCircle(cx, bloomCy - split, bloomRadius, green);
        }

        // ── Exit: fade to BackdropStart over 2.6s → 3.0s ──
        if (t > 2.6f)
        {
            float fade = Clamp01((t - 2.6f) / 0.4f);
            using var fadePaint = new SKPaint { Color = SplashBrand.BackdropStart.WithAlpha(A(fade)) };
            canvas.DrawRect(0, 0, width, height, fadePaint);
        }
    }

    // Classic 7-point lightning-bolt polygon, normalized to a 0..1 box (x right, y down).
    private static readonly float[] BoltX = { 0.50f, 0.10f, 0.40f, 0.25f, 0.90f, 0.55f, 0.75f };
    private static readonly float[] BoltY = { 0.00f, 0.60f, 0.60f, 1.00f, 0.35f, 0.35f, 0.00f };

    /// <summary>Fill a small lightning-bolt glyph centered at (cx,cy), spanning w×h — the tagline "⚡".</summary>
    private static void DrawBolt(SKCanvas canvas, float cx, float cy, float w, float h, SKColor color)
    {
        using var path = new SKPath();
        for (int i = 0; i < BoltX.Length; i++)
        {
            float x = cx + (BoltX[i] - 0.5f) * w;
            float y = cy + (BoltY[i] - 0.5f) * h;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        canvas.DrawPath(path, fill);
    }

    /// <summary>Additive (Plus) stroke paint used for the chromatic-aberration bloom rings.</summary>
    private static SKPaint AberrPaint(float strokeWidth, SKColor color) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = strokeWidth,
        BlendMode = SKBlendMode.Plus,
        Color = color,
    };

    private void EnsureParticles()
    {
        if (_particles is not null) return;
        // 25 white points seeded deterministically by index (DeterministicRandom(42) ≈ Android's
        // Random(42L) intent). Only X/Y persist across frames; Alpha is recomputed every update.
        var rng = new DeterministicRandom(42);
        _particles = new Particle[25];
        for (int i = 0; i < 25; i++)
            _particles[i] = new Particle { X = rng.NextFloat(), Y = rng.NextFloat(), MaxLifetime = 1f };
    }

    /// <summary>Advance the starfield one frame: stream radially outward, respawn near centre off-screen.</summary>
    private void UpdateParticles(float dt)
    {
        foreach (var p in _particles!)
        {
            float dx = p.X - 0.5f, dy = p.Y - 0.5f;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < 0.01f) dist = 0.01f;

            float speed = (0.02f + dist * 0.8f) * dt * 60f;
            p.X += (dx / dist) * speed;
            p.Y += (dy / dist) * speed;

            p.Alpha = Clamp(dist * 3.0f, 0f, 0.9f) * Clamp((0.5f - dist) * 4.0f, 0f, 1.0f);

            if (p.X < 0f || p.X > 1f || p.Y < 0f || p.Y > 1f || dist > 0.5f)
            {
                float angle = _respawnRng.NextFloat() * MathF.PI * 2f;
                float startDist = 0.01f + _respawnRng.NextFloat() * 0.06f;
                p.X = 0.5f + MathF.Cos(angle) * startDist;
                p.Y = 0.5f + MathF.Sin(angle) * startDist;
                p.Alpha = 0f;
            }
        }
    }

    // ── Easing: Compose FastOutSlowInEasing = CubicBezier(0.4, 0.0, 0.2, 1.0) ──
    private static float FastOutSlowIn(float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        // Solve Bx(s)=x (Newton), then evaluate By(s). Control x1=0.4,x2=0.2 ; y1=0.0,y2=1.0.
        const float cx = 3f * 0.4f;
        const float bx = 3f * (0.2f - 0.4f) - cx;
        const float ax = 1f - cx - bx;
        const float cy = 3f * 0.0f;
        const float by = 3f * (1.0f - 0.0f) - cy;
        const float ay = 1f - cy - by;

        float s = x;
        for (int i = 0; i < 6; i++)
        {
            float fx = ((ax * s + bx) * s + cx) * s - x;
            float d = (3f * ax * s + 2f * bx) * s + cx;
            if (MathF.Abs(d) < 1e-6f) break;
            s = Clamp(s - fx / d, 0f, 1f);
        }
        return ((ay * s + by) * s + cy) * s;
    }

    private static byte A(float o) => (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);
}
