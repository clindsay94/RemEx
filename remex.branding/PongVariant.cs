using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// "Signal Pong" — a mini-Pong built from the brand mark's own elements. Two amber paddles rally a
/// signal comet (with a trailing tail) across a landscape court; each of three contacts pops the
/// struck paddle and ejects a feature glyph that flies to a parked spot in the top band and stays.
/// In the finale the paddles shape-morph into the icon's exact amber "&gt;" chevron (left) and cursor
/// bar (right) — a matched-sample path interpolation (rounded-bar outline → target outline, sampled by
/// arc length with <see cref="SKPathMeasure"/>) — while the comet streaks to the R-spot and cross-fades
/// into the white "R". The full terminal mark then materializes over the assembled chevron/R/cursor,
/// the "RemEx" wordmark + "COMMAND YOUR PC" tagline rise, and the whole lockup zooms out and fades to
/// the window fill.
///
/// Pixel-exact port of the Android <c>SplashPong.kt</c>. Deterministic — a pure function of elapsed
/// time with no <c>Random</c>/<c>DateTime</c> (rally trajectory varies by contact index + sin), so the
/// offline PNG renderer yields byte-stable frames. Fixed brand palette; never theme-adaptive.
/// </summary>
public sealed class PongVariant : ISplashVariant
{
    // ── Timeline (seconds) — identical to Android SplashPong.kt through the wordmark ──
    private const float IntroDur    = 0.30f;
    private const float Contact1    = 0.75f;   // right paddle
    private const float Contact2    = 1.20f;   // left paddle
    private const float Contact3    = 1.65f;   // right paddle
    private const float FinaleStart = 1.71f;   // Contact3 + 60ms
    private const float FinaleDur   = 0.56f;   // → 2.27
    private const float CompleteAt  = 2.27f;
    private const float CompleteDur = 0.20f;   // → 2.47
    private const float WordmarkAt  = 2.47f;
    private const float WordmarkDur = 0.35f;   // → 2.82
    private const float GlyphDur    = 0.42f;   // Android tween(420) per ejection

    // Exit — the post-wordmark zoom-out + fade. Android holds 200ms then runs a 750ms zoom with a
    // 400ms fade tail (≈3.8s total); the host hard-cuts at Duration and no verification frame reaches
    // past the wordmark, so the tail is compressed into the spec's ~2.8–3.2s envelope while keeping the
    // identical zoom-out(→3.4×)-then-fade character. Every earlier beat is byte-identical to Android.
    private const float ExitAt   = 2.82f;
    private const float ZoomDur  = 0.75f;
    private const float FadeAt   = 2.82f;
    private const float FadeDur  = 0.38f;      // fade fully covers the handoff at 3.20

    public float Duration => 3.20f;

    private const int MorphSamples = 72;
    private static readonly float Pi = MathF.PI;

    // Lazily-built 108-unit-space morph target samples (independent of w/h; transformed per frame).
    private SKPoint[]? _chevronTarget108;   // outline of the stroked "&gt;" chevron (fills to the exact mark chevron)
    private SKPoint[]? _cursorTarget108;    // outline of the filled cursor bar

    public void Render(SKCanvas canvas, float width, float height, float t, float dt)
    {
        float w = width, h = height;
        float baseSize = MathF.Min(w, h);

        // Court geometry — X on the width, Y on the height (task: normalized × w/h).
        float leftX = w * 0.30f, rightX = w * 0.70f, cxMid = w * 0.50f;
        float midY = h * 0.42f;

        // Assembled-mark layout + the 108→pixel transform (matches DrawMarkAt / DrawMark exactly).
        float markSize = baseSize * 0.50f;
        var markCenter = new SKPoint(w * 0.50f, h * 0.40f);
        float s108 = markSize / 108f;
        SKPoint IconPt(float px, float py) => new(
            markCenter.X - markSize / 2f + s108 * (54f + 0.8f * (px - 54f)),
            markCenter.Y - markSize / 2f + s108 * (54f + 0.8f * (py - 54f)));
        var rSpot = IconPt(58f, 60f);

        // Phase progresses — FastOutSlowIn tweens (Android), the comet uses smoothstep sub-eases.
        float intro = FastOutSlowIn(Ramp(t, 0f, IntroDur));
        float fin   = FastOutSlowIn(Ramp(t, FinaleStart, FinaleDur));
        float comp  = FastOutSlowIn(Ramp(t, CompleteAt, CompleteDur));
        float word  = FastOutSlowIn(Ramp(t, WordmarkAt, WordmarkDur));

        // Exit zoom + fade.
        float zoomP = FastOutSlowIn(Ramp(t, ExitAt, ZoomDur));
        float zoomScale = 1f + 2.4f * zoomP;      // 1 → 3.4
        float fade = Ramp(t, FadeAt, FadeDur);    // linear (Android LinearEasing)

        // Deterministic rally trajectory — vary by contact index + sin (never Random).
        float yf0 = 0.42f;
        float yf1 = 0.44f + 0.12f * MathF.Sin(1f * 1.9f + 0.5f);   // ≈ 0.521
        float yf2 = 0.44f + 0.12f * MathF.Sin(2f * 1.9f + 0.5f);   // ≈ 0.330
        float yf3 = 0.44f + 0.12f * MathF.Sin(3f * 1.9f + 0.5f);   // ≈ 0.430
        float arc0 = ArcPeak(0), arc1 = ArcPeak(1), arc2 = ArcPeak(2);
        float y0 = yf0 * h, y1 = yf1 * h, y2 = yf2 * h, y3 = yf3 * h;

        SKPoint Signal(float tt)
        {
            if (tt < 0.30f) return new SKPoint(cxMid, y0);
            if (tt < 0.75f) { float u = Smooth((tt - 0.30f) / 0.45f); return new SKPoint(Lerp(cxMid, rightX, u), Lerp(y0, y1, u) + MathF.Sin(u * Pi) * arc0 * h); }
            if (tt < 1.20f) { float u = Smooth((tt - 0.75f) / 0.45f); return new SKPoint(Lerp(rightX, leftX, u), Lerp(y1, y2, u) + MathF.Sin(u * Pi) * arc1 * h); }
            if (tt < 1.65f) { float u = Smooth((tt - 1.20f) / 0.45f); return new SKPoint(Lerp(leftX, rightX, u), Lerp(y2, y3, u) + MathF.Sin(u * Pi) * arc2 * h); }
            return new SKPoint(rightX, y3);
        }

        // Paddles chase the signal on their own side of the court (recenter when it's away).
        var sig = Signal(t);
        float wR = Clamp01((sig.X - cxMid) / (rightX - cxMid));
        float wL = Clamp01((cxMid - sig.X) / (cxMid - leftX));
        float leftPaddleY = Lerp(midY, sig.Y, wL * 0.9f);
        float rightPaddleY = Lerp(midY, sig.Y, wR * 0.9f);

        // Spring recoil pops — right paddle struck at contacts 1 & 3, left paddle at contact 2.
        float rightRecoil = Recoil(t, t >= Contact3 ? Contact3 : (t >= Contact1 ? Contact1 : -1f));
        float leftRecoil  = Recoil(t, t >= Contact2 ? Contact2 : -1f);

        float paddleCourtScale = baseSize * 0.06f;
        float paddleAlpha = intro * (1f - comp);

        // ── Zoom-wrapped scene: screenPos = center + (p-center)*s + (0, (h/2 - markCy)*(s-1)*zoomP) ──
        canvas.Save();
        canvas.Translate(0f, (h / 2f - markCenter.Y) * (zoomScale - 1f) * zoomP);
        canvas.Translate(w / 2f, h / 2f);
        canvas.Scale(zoomScale);
        canvas.Translate(-w / 2f, -h / 2f);

        SplashScene.DrawBackdrop(canvas, w, h);

        // Paddles — morph rounded bar → chevron/cursor while sliding to the icon layout.
        EnsureTargets();
        var chevronPix = new SKPoint[MorphSamples];
        var cursorPix = new SKPoint[MorphSamples];
        for (int i = 0; i < MorphSamples; i++)
        {
            var c = _chevronTarget108![i]; chevronPix[i] = IconPt(c.X, c.Y);
            var u = _cursorTarget108![i]; cursorPix[i] = IconPt(u.X, u.Y);
        }
        DrawPaddleMorph(canvas, leftX, leftPaddleY, paddleCourtScale, leftRecoil, fin, paddleAlpha, chevronPix);
        DrawPaddleMorph(canvas, rightX, rightPaddleY, paddleCourtScale, rightRecoil, fin, paddleAlpha, cursorPix);

        // Comet (rally, 7-step trailing tail) / cross-fade to the R (finale).
        float cometUnit = MathF.Max(baseSize / 520f, 0.8f);
        if (fin <= 0f)
        {
            const int steps = 7;
            for (int i = steps; i >= 0; i--)
            {
                var p = Signal(t - i * 0.016f);
                float f = 1f - i / (float)steps;
                float a = f * 0.9f * intro;
                if (a <= 0.01f) continue;
                float rad = Lerp(2.2f, 6.5f, f) * cometUnit;
                DrawDisc(canvas, p, rad * 2.4f, SplashBrand.Amber, a * 0.35f);   // glow
                DrawDisc(canvas, p, rad, i == 0 ? SplashBrand.OffWhite : SplashBrand.Amber, a);
            }
        }
        else
        {
            // Signal streaks to the R-spot and fades as the R resolves in.
            var sp = LerpP(new SKPoint(rightX, y3), rSpot, fin);
            float sigA = 1f - fin;
            if (sigA > 0.01f)
            {
                DrawDisc(canvas, sp, cometUnit * 6.5f * 2.4f * 0.55f, SplashBrand.Amber, sigA * 0.35f);
                DrawDisc(canvas, sp, cometUnit * 6.5f * 0.55f, SplashBrand.OffWhite, sigA);
            }
            if (comp < 1f)
            {
                canvas.Save();
                canvas.Translate(markCenter.X - markSize / 2f, markCenter.Y - markSize / 2f);
                canvas.Scale(s108, s108);
                canvas.Translate(54f, 54f); canvas.Scale(0.8f, 0.8f); canvas.Translate(-54f, -54f);
                SplashBrand.DrawRGlyph(canvas, fin * (1f - comp), SplashBrand.BackdropStart);
                canvas.Restore();
            }
        }

        // Completion beat — the full brand mark materializes over the assembled elements.
        if (comp > 0f)
            SplashBrand.DrawMarkAt(canvas, markCenter.X, markCenter.Y, markSize, comp);

        // Feature glyphs — eject from the contact point, park in the top band, and hold.
        float g1 = FastOutSlowIn(Ramp(t, Contact1, GlyphDur));
        float g2 = FastOutSlowIn(Ramp(t, Contact2, GlyphDur));
        float g3 = FastOutSlowIn(Ramp(t, Contact3, GlyphDur));
        DrawParkedGlyph(canvas, FeatureGlyph.RemoteDesktop, g1, new SKPoint(rightX, midY), new SKPoint(w * 0.28f, h * 0.24f), w * 0.13f);
        DrawParkedGlyph(canvas, FeatureGlyph.Telemetry,     g2, new SKPoint(leftX, midY),  new SKPoint(w * 0.50f, h * 0.19f), w * 0.13f);
        DrawParkedGlyph(canvas, FeatureGlyph.FileTransfer,  g3, new SKPoint(rightX, midY), new SKPoint(w * 0.72f, h * 0.24f), w * 0.13f);

        // Wordmark lockup below the completed mark.
        if (word > 0.01f)
        {
            float rise = (1f - word) * baseSize * 0.02f;
            float wmSize = markSize * 0.22f;
            float wy = markCenter.Y + markSize * 0.62f + rise;
            SplashBrand.DrawWordmark(canvas, w / 2f, wy, wmSize, word);
            float tagSize = markSize * 0.075f;
            DrawTagline(canvas, w / 2f, wy + wmSize * 0.85f, tagSize, word);
        }

        canvas.Restore();

        // Exit fade to the window fill (screen space — covers the handoff to the app).
        if (fade > 0f)
        {
            using var f = new SKPaint { Color = SplashBrand.WindowFill.WithAlpha(Alpha(fade)) };
            canvas.DrawRect(0, 0, w, h, f);
        }
    }

    // ── Morph ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the two 108-space morph targets once: the chevron as the fill-outline of its stroked
    /// "&gt;" (so the filled morph lands on the exact amber chevron the mark renders), and the cursor
    /// as its already-closed bar outline. Both are normalized so point i corresponds across the morph.
    /// </summary>
    private void EnsureTargets()
    {
        if (_chevronTarget108 is not null) return;
        using (var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = RemexBrandData.ChevronStrokeWidth,   // 4.8, matching DrawMark
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        })
        using (var outline = new SKPath())
        {
            strokePaint.GetFillPath(SplashBrand.Chevron, outline);
            _chevronTarget108 = SamplePath(outline, MorphSamples);
        }
        NormalizeLoop(_chevronTarget108!);
        _cursorTarget108 = SamplePath(SplashBrand.Cursor, MorphSamples);
        NormalizeLoop(_cursorTarget108);
    }

    /// <summary>Draw the amber paddle at <paramref name="fin"/>: a rounded bar (0) lerped point-by-point
    /// toward the pre-mapped target outline (1). The horizontal recoil widens the bar on impact.</summary>
    private static void DrawPaddleMorph(SKCanvas canvas, float px, float py, float scale, float recoil, float fin, float alpha, SKPoint[] tgt)
    {
        if (alpha <= 0.01f) return;

        float halfW = scale * 0.20f * (1f + recoil * 0.5f);   // paddlePoly x half-extent 0.2, +recoil pop
        float halfH = scale * 1.0f;                            // y half-extent 1.0
        float corner = scale * 0.20f;                          // CornerRounding 0.2
        using var bar = new SKPath();
        bar.AddRoundRect(new SKRect(px - halfW, py - halfH, px + halfW, py + halfH), corner, corner);
        var src = SamplePath(bar, MorphSamples);
        NormalizeLoop(src);

        float m = Smooth(fin);
        using var path = new SKPath();
        for (int i = 0; i < MorphSamples; i++)
        {
            var pt = LerpP(src[i], tgt[i], m);
            if (i == 0) path.MoveTo(pt); else path.LineTo(pt);
        }
        path.Close();
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SplashBrand.Amber.WithAlpha(Alpha(alpha)) };
        canvas.DrawPath(path, fill);
    }

    /// <summary>Sample <paramref name="n"/> points evenly by arc length around the path's perimeter.</summary>
    private static SKPoint[] SamplePath(SKPath path, int n)
    {
        var pts = new SKPoint[n];
        using var pm = new SKPathMeasure(path, false);
        float len = pm.Length;
        if (len <= 0f) return pts;
        for (int i = 0; i < n; i++)
            pm.GetPosition(len * i / n, out pts[i]);
        return pts;
    }

    /// <summary>Give a sampled closed loop a canonical orientation (consistent winding) and start point
    /// (topmost, then leftmost) so two loops morph without swirling.</summary>
    private static void NormalizeLoop(SKPoint[] pts)
    {
        int n = pts.Length;
        if (n < 3) return;

        float area = 0f;
        for (int i = 0; i < n; i++) { var a = pts[i]; var b = pts[(i + 1) % n]; area += a.X * b.Y - b.X * a.Y; }
        if (area < 0f) Array.Reverse(pts);

        int top = 0;
        for (int i = 1; i < n; i++)
            if (pts[i].Y < pts[top].Y || (pts[i].Y == pts[top].Y && pts[i].X < pts[top].X)) top = i;
        if (top == 0) return;

        var rot = new SKPoint[n];
        for (int i = 0; i < n; i++) rot[i] = pts[(top + i) % n];
        Array.Copy(rot, pts, n);
    }

    // ── Glyphs / comet / text ─────────────────────────────────────────────────────

    /// <summary>Eject a feature glyph from <paramref name="from"/> to its parked spot as it grows in, then hold.</summary>
    private static void DrawParkedGlyph(SKCanvas canvas, FeatureGlyph kind, float p, SKPoint from, SKPoint park, float sizePx)
    {
        if (p <= 0.01f) return;
        float e = Smooth(p);
        var pos = LerpP(from, park, e);
        SplashBrand.DrawFeatureGlyph(canvas, kind, pos.X, pos.Y, sizePx * (0.4f + 0.6f * e), e);
    }

    private static void DrawDisc(SKCanvas canvas, SKPoint c, float r, SKColor color, float alpha)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(Alpha(alpha)) };
        canvas.DrawCircle(c.X, c.Y, r, p);
    }

    /// <summary>Two-color tagline "COMMAND " (slate) + "YOUR PC" (amber), centered — matches Android's split.</summary>
    private static void DrawTagline(SKCanvas canvas, float cx, float baselineY, float size, float opacity)
    {
        const string cmd = "COMMAND ", pc = "YOUR PC";
        float wc = SplashBrand.MeasureText(cmd, size);
        float wp = SplashBrand.MeasureText(pc, size);
        float startX = cx - (wc + wp) / 2f;
        SplashBrand.DrawText(canvas, cmd, startX + wc / 2f, baselineY, size, SplashBrand.SlateLo, opacity);
        SplashBrand.DrawText(canvas, pc, startX + wc + wp / 2f, baselineY, size, SplashBrand.Amber, opacity);
    }

    // ── Easing / math ─────────────────────────────────────────────────────────────

    private static float Ramp(float t, float start, float dur) => Clamp01((t - start) / dur);
    private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static SKPoint LerpP(SKPoint a, SKPoint b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    private static float Smooth(float x) { float c = Clamp01(x); return c * c * (3f - 2f * c); }
    private static byte Alpha(float o) => (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);

    /// <summary>Signed, index-varied arc height for rally contact <paramref name="i"/> (min magnitude 0.07).</summary>
    private static float ArcPeak(int i)
    {
        float s = MathF.Sin(i * 2.2f + 1.1f);
        return (0.07f + 0.08f * MathF.Abs(s)) * (s < 0f ? -1f : 1f);
    }

    /// <summary>Underdamped spring step response from 1→0 (ζ=0.35, stiffness 1500) — the paddle "pop".</summary>
    private static float Recoil(float t, float contact)
    {
        if (contact < 0f) return 0f;
        float tau = t - contact;
        if (tau < 0f) return 0f;
        const float zeta = 0.35f;
        float wn = MathF.Sqrt(1500f);
        float root = MathF.Sqrt(1f - zeta * zeta);
        float wd = wn * root;
        return MathF.Exp(-zeta * wn * tau) * (MathF.Cos(wd * tau) + (zeta / root) * MathF.Sin(wd * tau));
    }

    private static float FastOutSlowIn(float x) => CubicBezier(Clamp01(x), 0.4f, 0f, 0.2f, 1f);

    private static float CubicBezier(float x, float x1, float y1, float x2, float y2)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        float t = x;
        for (int i = 0; i < 8; i++)
        {
            float bx = Bez(t, x1, x2) - x;
            float d = BezD(t, x1, x2);
            if (MathF.Abs(d) < 1e-6f) break;
            t = Clamp01(t - bx / d);
        }
        return Bez(t, y1, y2);
    }

    private static float Bez(float t, float p1, float p2) { float u = 1f - t; return 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t; }
    private static float BezD(float t, float p1, float p2) { float u = 1f - t; return 3f * u * u * p1 + 6f * u * t * (p2 - p1) + 3f * t * t * (1f - p2); }
}
