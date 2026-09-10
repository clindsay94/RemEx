using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// "Original Scan" — a phone→monitor connection revealed by three top-to-bottom sweep passes,
/// then terminal typing inside the monitor, a "RemEx" wordmark lockup, and a camera pull-in
/// into the monitor screen. Pixel-faithful port of the Android SplashRemexCommand.kt (phases,
/// timings and beats preserved). Pure SkiaSharp — a frame renders to a PNG without Avalonia.
///
/// Landscape adaptation: Android renders portrait; here the composition is laid out for a
/// landscape window. Positions are normalized (×w for x, ×h for y); hero elements and the
/// dp→px scale are sized from S = min(w,h) so the scene reads the same at any aspect.
/// </summary>
public sealed class RemexCommandVariant : ISplashVariant
{
    // ── Timeline (seconds) — mirrors the Android LaunchedEffect sequence exactly ──────────────
    //   pass1 sweep 0→1 (540ms, FastOutSlowIn) · gap 95ms
    //   pass2 sweep 1→2 (540ms)                 · gap 95ms
    //   pass3 sweep 2→3 (540ms)                 · settle 350ms
    //   pull-in: zoomScale 1→6 & zoomProgress 0→1 (700ms) ; fade 0→1 starts +400ms, runs 400ms.
    private const float SweepDur = 0.540f;
    private const float P1End = SweepDur;                 // 0.540
    private const float P2Start = P1End + 0.095f;         // 0.635
    private const float P2End = P2Start + SweepDur;       // 1.175
    private const float P3Start = P2End + 0.095f;         // 1.270
    private const float P3End = P3Start + SweepDur;       // 1.810
    private const float SettleEnd = P3End + 0.350f;       // 2.160
    private const float ZoomDur = 0.700f;
    private const float ZoomEnd = SettleEnd + ZoomDur;    // 2.860
    private const float FadeStart = SettleEnd + 0.400f;   // 2.560
    private const float FadeDur = 0.400f;
    private const float FadeEnd = FadeStart + FadeDur;    // 2.960

    public float Duration => FadeEnd; // 2.96s

    // In-monitor terminal reveal — literal command syntax stays English (not localized).
    private static readonly string[] TermLines =
    {
        "> connect --host pc",
        "> pairing... ok",
        "> session ready",
    };

    // ── Mutable state (advanced by dt each frame; lazily initialized, deterministic) ──────────
    private bool _inited;
    private readonly List<Particle> _embers = new();      // rising ambient embers
    private readonly List<FloatingShape> _shapes = new();  // ambient morph shapes
    private readonly List<Stream> _stream = new();         // particles travelling the Bezier
    private readonly DeterministicRandom _rng = new(99);   // respawn / morph-target picks

    private sealed class Stream { public float T, Speed, Radius, Alpha; }

    public void Render(SKCanvas canvas, float width, float height, float t, float dt)
    {
        EnsureInit();
        Advance(dt);

        float w = width, h = height;
        float S = MathF.Min(w, h);
        float dp = S / 400f; // phone-equivalent density: min dimension ≈ 400dp

        // ── Timeline values as a function of elapsed t ────────────────────────────────────────
        float sweep = SweepValue(t);
        float p1 = Math.Clamp(sweep, 0f, 1f);       // pass 1: device outlines
        float p2 = Math.Clamp(sweep - 1f, 0f, 1f);  // pass 2: solid fills + stream
        float p3 = Math.Clamp(sweep - 2f, 0f, 1f);  // pass 3: terminal + wordmark
        float glow = p2 * (1f - 0.8f * p3);         // stream ramps in on pass 2, dims behind wordmark
        float streamOffset = (t - MathF.Floor(t / 1.5f) * 1.5f) / 1.5f; // 0→1 over 1500ms, repeating

        float zoomProgress = ZoomProgress(t);
        float zoomScale = 1f + 5f * zoomProgress;
        float fade = FadeValue(t);

        // ── Device geometry (sizes ∝ S, positions ∝ w/h) ─────────────────────────────────────
        float monW = 0.46f * S, monH = monW * 0.62f;
        float monCx = w * 0.28f, monCy = h * 0.30f;
        float monX = monCx - monW / 2f, monY = monCy - monH / 2f;
        float monCorner = monW * 0.06f;

        float phoneW = 0.195f * S, phoneH = phoneW * 1.8f;
        float phoneCx = w * 0.74f, phoneCy = h * 0.70f;
        float phoneX = phoneCx - phoneW / 2f, phoneY = phoneCy - phoneH / 2f;
        float phoneCorner = phoneW * 0.15f;

        // Monitor screen inset.
        float mInset = monW * 0.04f;
        float monScreenX = monX + mInset, monScreenY = monY + mInset;
        float monScreenW = monW - mInset * 2f, monScreenH = monH - mInset * 2f;

        // Phone screen inset.
        float pInset = phoneW * 0.08f;
        float pScreenX = phoneX + pInset, pScreenY = phoneY + pInset * 1.5f;
        float pScreenW = phoneW - pInset * 2f, pScreenH = phoneH - pInset * 2.5f;

        // Connection Bezier (phone → monitor).
        var connStart = new SKPoint(phoneCx, phoneCy - phoneH * 0.35f);
        var connEnd = new SKPoint(monCx + monW * 0.3f, monCy + monH * 0.3f);
        var connCtrl1 = new SKPoint(phoneCx - w * 0.15f, phoneCy - h * 0.25f);
        var connCtrl2 = new SKPoint(monCx + w * 0.2f, monCy + h * 0.15f);

        // Feature glyphs — icon 1 (pass 1), icons 2 & 3 (pass 2); they park and stay.
        float featSpread = w * 0.26f, featSize = S * 0.12f;
        float[] featCx = { w * 0.5f - featSpread, w * 0.5f, w * 0.5f + featSpread };
        float[] featCy = { h * 0.44f, h * 0.37f, h * 0.44f };
        float[] featA = { Sstep(0.55f, 1f, p1), Sstep(0.15f, 0.6f, p2), Sstep(0.55f, 1f, p2) };
        FeatureGlyph[] kinds = { FeatureGlyph.RemoteDesktop, FeatureGlyph.Telemetry, FeatureGlyph.FileTransfer };

        // Wordmark lockup (pass 3): icon above "RemEx" + tagline, fades and rises in.
        float wmAlpha = Sstep(0.25f, 1f, p3);
        float wmRise = (1f - wmAlpha) * 14f * dp;
        float lockupCy = h * 0.55f;
        float wmIconSize = S * 0.16f;
        float wmTextSize = S * 0.09f;

        // ══ Camera: everything below draws under the exit pull-in (identity until t≈2.16s) ══════
        canvas.Save();
        if (zoomProgress > 0f)
        {
            // Move monitor-centre toward viewport-centre, then scale about the monitor centre.
            canvas.Translate((w * 0.5f - monCx) * zoomProgress, (h * 0.5f - monCy) * zoomProgress);
            canvas.Translate(monCx, monCy);
            canvas.Scale(zoomScale);
            canvas.Translate(-monCx, -monCy);
        }

        // ── Background (always) ───────────────────────────────────────────────────────────────
        SplashScene.DrawBackdrop(canvas, w, h);
        SplashFx.DrawAmbientFx(canvas, w, h, _shapes, SplashBrand.WindowStroke, SplashBrand.SlateLo);

        // Rising embers (fade out as the solid pass takes over).
        if (p2 < 0.5f)
        {
            using var ep = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            float er = 1.5f * dp;
            foreach (var p in _embers)
                if (p.Alpha > 0.02f)
                {
                    ep.Color = SplashBrand.SlateLo.WithAlpha(A(p.Alpha));
                    canvas.DrawCircle(p.X * w, p.Y * h, er, ep);
                }
        }

        // ── Pass 1: device wireframe outlines (under a descending clip band) ──────────────────
        SweepClipped(canvas, w, h, p1, () =>
        {
            using var st = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SplashBrand.SlateLo };
            st.StrokeWidth = 3f * dp;
            canvas.DrawRoundRect(new SKRect(monX, monY, monX + monW, monY + monH), monCorner, monCorner, st);
            st.StrokeWidth = 5f * dp;
            canvas.DrawLine(monCx, monY + monH, monCx, monY + monH + monH * 0.18f, st);
            float baseW = monW * 0.35f, baseY = monY + monH + monH * 0.18f;
            st.StrokeWidth = 4f * dp;
            canvas.DrawLine(monCx - baseW / 2f, baseY, monCx + baseW / 2f, baseY, st);
            st.StrokeWidth = 2f * dp;
            canvas.DrawRoundRect(new SKRect(phoneX, phoneY, phoneX + phoneW, phoneY + phoneH), phoneCorner, phoneCorner, st);
        });

        // ── Pass 2: solid devices + connection stream ─────────────────────────────────────────
        SweepClipped(canvas, w, h, p2, () =>
        {
            // Monitor shadow / body gradient / screen.
            using (var sh = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(A(0.35f)) })
                canvas.DrawRoundRect(new SKRect(monX + 4f * dp, monY + 6f * dp, monX + 4f * dp + monW, monY + 6f * dp + monH), monCorner, monCorner, sh);
            using (var body = new SKPaint { IsAntialias = true })
            {
                body.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(monX, monY), new SKPoint(monX + monW, monY + monH),
                    new[] { SplashBrand.SlateLo, SplashBrand.WindowStroke }, null, SKShaderTileMode.Clamp);
                canvas.DrawRoundRect(new SKRect(monX, monY, monX + monW, monY + monH), monCorner, monCorner, body);
            }
            using (var scr = new SKPaint { IsAntialias = true, Color = SplashBrand.WindowFill })
                canvas.DrawRoundRect(new SKRect(monScreenX, monScreenY, monScreenX + monScreenW, monScreenY + monScreenH), monW * 0.03f, monW * 0.03f, scr);
            using (var ln = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SplashBrand.SlateLo, StrokeWidth = 5f * dp })
            {
                canvas.DrawLine(monCx, monY + monH, monCx, monY + monH + monH * 0.18f, ln);
                ln.StrokeWidth = 4f * dp;
                float bW = monW * 0.35f, bY = monY + monH + monH * 0.18f;
                canvas.DrawLine(monCx - bW / 2f, bY, monCx + bW / 2f, bY, ln);
            }
            // Phone shadow / body / screen.
            using (var psh = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(A(0.35f)) })
                canvas.DrawRoundRect(new SKRect(phoneX + 3f * dp, phoneY + 5f * dp, phoneX + 3f * dp + phoneW, phoneY + 5f * dp + phoneH), phoneCorner, phoneCorner, psh);
            using (var pb = new SKPaint { IsAntialias = true, Color = SplashBrand.WindowStroke })
                canvas.DrawRoundRect(new SKRect(phoneX, phoneY, phoneX + phoneW, phoneY + phoneH), phoneCorner, phoneCorner, pb);
            using (var ps = new SKPaint { IsAntialias = true, Color = SplashBrand.WindowFill })
                canvas.DrawRoundRect(new SKRect(pScreenX, pScreenY, pScreenX + pScreenW, pScreenY + pScreenH), phoneW * 0.08f, phoneW * 0.08f, ps);

            if (glow > 0f)
                DrawConnectionStream(canvas, dp, glow, streamOffset, connStart, connCtrl1, connCtrl2, connEnd, phoneCx, phoneY);
        });

        // ── Feature glyphs (unclipped; alpha-driven) ──────────────────────────────────────────
        for (int i = 0; i < 3; i++)
            if (featA[i] > 0.01f)
                SplashBrand.DrawFeatureGlyph(canvas, kinds[i], featCx[i], featCy[i], featSize * (0.7f + featA[i] * 0.3f), featA[i]);

        // ── Pass 3: terminal typing inside the monitor ────────────────────────────────────────
        if (p3 > 0f)
            DrawTerminal(canvas, t, p3, monScreenX, monScreenY, monScreenW, monScreenH);

        // ── Wordmark lockup: icon above "RemEx" + "COMMAND YOUR PC" ───────────────────────────
        if (wmAlpha > 0.01f)
        {
            SplashBrand.DrawMarkAt(canvas, w * 0.5f, lockupCy - wmIconSize * 0.62f + wmRise, wmIconSize, wmAlpha);
            float wmBaseline = lockupCy + wmTextSize * 0.35f + wmRise;
            SplashBrand.DrawWordmark(canvas, w * 0.5f, wmBaseline, wmTextSize, wmAlpha);

            float tagSize = S * 0.032f;
            float wCmd = SplashBrand.MeasureText("COMMAND ", tagSize);
            float wPc = SplashBrand.MeasureText("YOUR PC", tagSize);
            float tagX = w * 0.5f - (wCmd + wPc) / 2f;
            float tagTop = wmBaseline + wmTextSize * 0.55f + 10f * dp;
            DrawLeftText(canvas, "COMMAND ", tagX, tagTop, tagSize, SplashBrand.SlateLo.WithAlpha(A(wmAlpha)));
            DrawLeftText(canvas, "YOUR PC", tagX + wCmd, tagTop, tagSize, SplashBrand.Amber.WithAlpha(A(wmAlpha)));
        }

        // ── Leading amber edge + gradient trail at the active sweep front ─────────────────────
        float activeFrac = sweep <= 0f ? -1f : sweep < 1f ? p1 : sweep < 2f ? p2 : sweep < 3f ? p3 : -1f;
        if (activeFrac > 0.001f && activeFrac < 0.999f)
        {
            float edgeY = h * activeFrac;
            float trail = 44f * dp;
            using (var tp = new SKPaint { IsAntialias = true })
            {
                tp.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, edgeY - trail), new SKPoint(0, edgeY),
                    new[] { SKColors.Transparent, SplashBrand.Amber.WithAlpha(A(0.22f)) }, null, SKShaderTileMode.Clamp);
                canvas.DrawRect(0, edgeY - trail, w, trail, tp);
            }
            using (var el = new SKPaint { Color = SplashBrand.Amber.WithAlpha(A(0.9f)) })
                canvas.DrawRect(0, edgeY - 1.5f * dp, w, 3f * dp, el);
        }

        canvas.Restore(); // end camera

        // ── Exit fade to the window-fill colour (screen space, guaranteed full coverage) ──────
        if (fade > 0f)
            using (var fp = new SKPaint { Color = SplashBrand.WindowFill.WithAlpha(A(fade)) })
                canvas.DrawRect(0, 0, w, h, fp);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Connection stream (energy flow phone → monitor).
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private void DrawConnectionStream(SKCanvas canvas, float dp, float glow, float streamOffset,
        SKPoint cs, SKPoint c1, SKPoint c2, SKPoint ce, float phoneCx, float phoneY)
    {
        using var connPath = new SKPath();
        connPath.MoveTo(cs);
        connPath.CubicTo(c1, c2, ce);

        // Wide translucent glow layer.
        using (var g = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = 6f * dp + glow * 18f * dp,
            Color = SplashBrand.Amber.WithAlpha(A(0.08f + glow * 0.25f)),
        })
            canvas.DrawPath(connPath, g);

        // Animated dashed core line.
        float dashPhase = streamOffset * 80f;
        using (var dash = SKPathEffect.CreateDash(new[] { 10f, 10f }, dashPhase))
        using (var core = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = 1.5f * dp + glow * 2f * dp,
            Color = SplashBrand.Amber.WithAlpha(A(0.25f + glow * 0.6f)),
            PathEffect = dash,
        })
            canvas.DrawPath(connPath, core);

        // Particles travelling along the curve (core + soft halo).
        using (var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
            foreach (var s in _stream)
            {
                var pos = CubicPoint(cs, c1, c2, ce, s.T);
                float a = s.Alpha * (0.5f + glow * 0.5f);
                sp.Color = SplashBrand.Amber.WithAlpha(A(a));
                canvas.DrawCircle(pos.X, pos.Y, s.Radius * dp, sp);
                sp.Color = SplashBrand.Amber.WithAlpha(A(a * 0.3f));
                canvas.DrawCircle(pos.X, pos.Y, s.Radius * dp * 2.5f, sp);
            }

        // Wi-Fi / signal arcs emanating from the phone.
        float wcx = phoneCx, wcy = phoneY - 4f * dp;
        using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 1.5f * dp })
            for (int k = 0; k < 3; k++)
            {
                float ar = 20f * dp + k * 18f * dp;
                float aa = Math.Clamp((0.15f - k * 0.04f) + glow * 0.2f, 0f, 1f);
                arc.Color = SplashBrand.Amber.WithAlpha(A(aa));
                using var arcPath = new SKPath();
                arcPath.AddArc(new SKRect(wcx - ar, wcy - ar, wcx + ar, wcy + ar), 210f, 120f);
                canvas.DrawPath(arcPath, arc);
            }

        // Decorative offset trace.
        using (var dash2 = SKPathEffect.CreateDash(new[] { 6f, 12f }, dashPhase * 0.7f))
        using (var t2 = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f * dp,
            Color = SplashBrand.Amber.WithAlpha(A(0.12f + glow * 0.15f)),
            PathEffect = dash2,
        })
        using (var conn2 = new SKPath())
        {
            conn2.MoveTo(cs.X + 12f * dp, cs.Y - 8f * dp);
            conn2.CubicTo(c1.X + 20f, c1.Y - 20f, c2.X - 20f, c2.Y + 20f, ce.X - 10f * dp, ce.Y + 8f * dp);
            canvas.DrawPath(conn2, t2);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Terminal typing: 3 monospaced lines typed char-by-char + a blinking amber block cursor.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    private static void DrawTerminal(SKCanvas canvas, float t, float p3,
        float monScreenX, float monScreenY, float monScreenW, float monScreenH)
    {
        float termSize = monScreenW / 15f;
        // SkiaSharp 3 moved typeface, size and metrics onto SKFont; alignment became a DrawText
        // argument (RemEx-jcma3). The metrics still have to come from the SAME font that draws and
        // measures, because Ascent positions every baseline here and MeasureText drives the
        // typing-reveal clip - a mismatch would shear the terminal text against its own clip rect.
        using var termFont = new SKFont(SplashBrand.Typeface ?? SKTypeface.Default, termSize);
        using var term = new SKPaint
        {
            IsAntialias = true,
            Color = SplashBrand.OffWhite,
        };
        var fm = termFont.Metrics;
        float lineH = (fm.Descent - fm.Ascent) * 1.25f;

        int total = TermLines[0].Length + TermLines[1].Length + TermLines[2].Length;
        float shown = p3 * total;
        float pad = monScreenW * 0.06f;
        float cursorX = monScreenX + pad, cursorY = monScreenY + pad;

        canvas.Save();
        canvas.ClipRect(new SKRect(monScreenX, monScreenY, monScreenX + monScreenW, monScreenY + monScreenH));
        int consumed = 0;
        for (int idx = 0; idx < 3; idx++)
        {
            string line = TermLines[idx];
            float ly = monScreenY + pad + idx * lineH;
            float revealed = Math.Clamp((shown - consumed) / line.Length, 0f, 1f);
            if (revealed > 0f)
            {
                float lw = termFont.MeasureText(line);
                canvas.Save();
                canvas.ClipRect(new SKRect(monScreenX, ly, monScreenX + pad + lw * revealed, ly + lineH));
                canvas.DrawText(line, monScreenX + pad, ly - fm.Ascent, SKTextAlign.Left, termFont, term);
                canvas.Restore();
                cursorX = monScreenX + pad + lw * revealed;
                cursorY = ly;
            }
            consumed += line.Length;
        }
        // Blinking block cursor at the typing head.
        if ((int)(t * 2f) % 2 == 0)
            using (var cur = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SplashBrand.Amber })
                canvas.DrawRect(new SKRect(cursorX + 2f, cursorY, cursorX + 2f + monScreenW * 0.03f, cursorY + lineH * 0.7f), cur);
        canvas.Restore();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Reveal <paramref name="block"/> under a top-to-bottom band covering <paramref name="frac"/> of the height.</summary>
    private static void SweepClipped(SKCanvas canvas, float w, float h, float frac, Action block)
    {
        if (frac <= 0f) return;
        if (frac >= 1f) { block(); return; }
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, w, h * frac));
        block();
        canvas.Restore();
    }

    private static void DrawLeftText(SKCanvas canvas, string text, float x, float topY, float size, SKColor color)
    {
        using var font = new SKFont(SplashBrand.Typeface ?? SKTypeface.Default, size);
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = color,
        };
        // topY is a TOP edge, so the baseline is topY minus the (negative) ascent. Metrics moved
        // from SKPaint to SKFont in SkiaSharp 3; the arithmetic is unchanged (RemEx-jcma3).
        canvas.DrawText(text, x, topY - font.Metrics.Ascent, SKTextAlign.Left, font, p);
    }

    private static SKPoint CubicPoint(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float t)
    {
        float u = 1f - t, tt = t * t, uu = u * u, uuu = uu * u, ttt = tt * t;
        return new SKPoint(
            uuu * p0.X + 3f * uu * t * p1.X + 3f * u * tt * p2.X + ttt * p3.X,
            uuu * p0.Y + 3f * uu * t * p1.Y + 3f * u * tt * p2.Y + ttt * p3.Y);
    }

    /// <summary>0 at/below edge0, 1 at/above edge1, linear between (Android sstep).</summary>
    private static float Sstep(float edge0, float edge1, float x) => Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);

    private static byte A(float o) => (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);

    private float SweepValue(float t)
    {
        if (t < P1End) return FastOutSlowIn(t / SweepDur);
        if (t < P2Start) return 1f;
        if (t < P2End) return 1f + FastOutSlowIn((t - P2Start) / SweepDur);
        if (t < P3Start) return 2f;
        if (t < P3End) return 2f + FastOutSlowIn((t - P3Start) / SweepDur);
        return 3f;
    }

    private static float ZoomProgress(float t)
    {
        if (t <= SettleEnd) return 0f;
        if (t < ZoomEnd) return FastOutSlowIn((t - SettleEnd) / ZoomDur);
        return 1f;
    }

    private static float FadeValue(float t)
    {
        if (t <= FadeStart) return 0f;
        if (t < FadeEnd) return (t - FadeStart) / FadeDur;
        return 1f;
    }

    /// <summary>Compose's FastOutSlowInEasing = cubic-bezier(0.4, 0.0, 0.2, 1.0).</summary>
    private static float FastOutSlowIn(float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        return CubicBezierEase(0.4f, 0.0f, 0.2f, 1.0f, x);
    }

    private static float CubicBezierEase(float p1x, float p1y, float p2x, float p2y, float x)
    {
        float cx = 3f * p1x, bx = 3f * (p2x - p1x) - cx, ax = 1f - cx - bx;
        float cy = 3f * p1y, by = 3f * (p2y - p1y) - cy, ay = 1f - cy - by;
        // Solve x(u)=x for u (Newton-Raphson), then evaluate y(u).
        float u = x;
        for (int i = 0; i < 8; i++)
        {
            float xu = ((ax * u + bx) * u + cx) * u - x;
            if (MathF.Abs(xu) < 1e-6f) break;
            float dxu = (3f * ax * u + 2f * bx) * u + cx;
            if (MathF.Abs(dxu) < 1e-6f) break;
            u -= xu / dxu;
        }
        u = Math.Clamp(u, 0f, 1f);
        return ((ay * u + by) * u + cy) * u;
    }

    // ── Deterministic lazy state setup + per-frame advance (mirrors the Android frame loop) ────
    private void EnsureInit()
    {
        if (_inited) return;
        _inited = true;

        var er = new DeterministicRandom(42);
        for (int i = 0; i < 25; i++)
            _embers.Add(new Particle
            {
                X = er.NextFloat(),
                Y = er.NextFloat(),
                Vx = (er.NextFloat() - 0.5f) * 0.005f,
                Vy = -(0.01f + er.NextFloat() * 0.02f),
                Lifetime = er.NextFloat() * 2f,
                MaxLifetime = 2f + er.NextFloat() * 2f,
                Alpha = 0f,
            });

        var sr = new DeterministicRandom(77);
        SKColor[] palette = { SplashBrand.SlateLo, SplashBrand.SlateHi, SplashBrand.WindowStroke };
        for (int i = 0; i < 18; i++)
            _shapes.Add(new FloatingShape
            {
                X = sr.NextFloat(),
                Y = sr.NextFloat(),
                Size = 0.06f + sr.NextFloat() * 0.12f,
                Vx = (sr.NextFloat() - 0.5f) * 0.002f,
                Vy = (sr.NextFloat() - 0.5f) * 0.002f,
                MorphProgress = sr.NextFloat(),
                MorphSpeed = 0.003f + sr.NextFloat() * 0.007f,
                Rotation = sr.NextFloat() * 360f,
                RotationSpeed = (sr.NextFloat() - 0.5f) * 1.8f,
                Alpha = 0.08f + sr.NextFloat() * 0.12f,
                SidesA = 3 + (int)(sr.NextFloat() * 6),
                SidesB = 3 + (int)(sr.NextFloat() * 6),
                RoundA = sr.NextFloat(),
                RoundB = sr.NextFloat(),
                Color = palette[(int)(sr.NextFloat() * 3)],
            });

        var pr = new DeterministicRandom(111);
        for (int i = 0; i < 12; i++)
            _stream.Add(new Stream
            {
                T = pr.NextFloat(),
                Speed = 0.008f + pr.NextFloat() * 0.012f,
                Radius = 1f + pr.NextFloat() * 2.5f,
                Alpha = 0.3f + pr.NextFloat() * 0.6f,
            });
    }

    private void Advance(float dt)
    {
        float f = dt * 60f;

        foreach (var p in _embers)
        {
            p.Lifetime += dt;
            p.X += p.Vx * f;
            p.Y += p.Vy * dt;
            float tt = p.Lifetime / p.MaxLifetime;
            p.Alpha = tt < 0.2f ? (tt / 0.2f) * 0.7f : tt < 0.8f ? 0.7f : (1f - (tt - 0.8f) / 0.2f) * 0.7f;
            if (p.Lifetime >= p.MaxLifetime)
            {
                p.X = _rng.NextFloat();
                p.Y = 0.4f + _rng.NextFloat() * 0.6f;
                p.Vx = (_rng.NextFloat() - 0.5f) * 0.016f;
                p.Vy = -(0.02f + _rng.NextFloat() * 0.02f);
                p.Lifetime = 0f;
                p.MaxLifetime = 1.5f + _rng.NextFloat() * 1.5f;
            }
        }

        foreach (var s in _shapes)
        {
            s.X += s.Vx * f;
            s.Y += s.Vy * f;
            s.Rotation += s.RotationSpeed * f;
            s.MorphProgress += s.MorphSpeed * f;
            if (s.MorphProgress > 1f)
            {
                s.MorphProgress = 0f;
                s.SidesA = s.SidesB;
                s.RoundA = s.RoundB;
                s.SidesB = 3 + (int)(_rng.NextFloat() * 6);
                s.RoundB = _rng.NextFloat();
            }
            if (s.X < -0.1f) s.X = 1.1f;
            if (s.X > 1.1f) s.X = -0.1f;
            if (s.Y < -0.1f) s.Y = 1.1f;
            if (s.Y > 1.1f) s.Y = -0.1f;
        }

        foreach (var s in _stream)
        {
            s.T += s.Speed * f;
            if (s.T > 1f) s.T -= 1f;
        }
    }
}
