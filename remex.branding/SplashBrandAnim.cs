using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// Animation-facing brand primitives shared by the splash variants: the Victor Mono typeface,
/// positioned mark/R draws, the two-color wordmark, taglines, and the three feature glyphs.
/// Ported 1:1 from the Android SplashBrand.kt so PC splashes match pixel-for-pixel. Pure
/// SkiaSharp (no Avalonia) so a variant frame can be rendered to a PNG for verification.
/// </summary>
public static partial class SplashBrand
{
    /// <summary>
    /// Brand typeface (Victor Mono Bold). Injected once at startup by the host
    /// (remex.desktop loads it from avares://). Tools/tests may set it from the .ttf on disk.
    /// Falls back to the default monospace face if never set, so text still renders.
    /// </summary>
    public static SKTypeface? Typeface { get; set; }

    private static SKTypeface FaceOrDefault => Typeface ?? SKTypeface.Default;

    /// <summary>Load and set <see cref="Typeface"/> from a font stream (e.g. the bundled Victor Mono ttf).</summary>
    public static void LoadTypeface(Stream fontStream) => Typeface = SKTypeface.FromStream(fontStream);

    private static byte A(float o) => (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);

    // ── Positioned mark / R ───────────────────────────────────────────────────

    /// <summary>Draw the terminal-window mark centered at (cx,cy), the 108-unit artwork spanning sizePx.</summary>
    public static void DrawMarkAt(SKCanvas canvas, float cx, float cy, float sizePx, float opacity = 1f)
    {
        canvas.Save();
        canvas.Translate(cx - sizePx / 2f, cy - sizePx / 2f);
        canvas.Scale(sizePx / RemexBrandData.Viewport);
        DrawMark(canvas, opacity);
        canvas.Restore();
    }

    /// <summary>
    /// Draw the white "R" glyph (with its punched hole) in 108-unit space; the caller applies the
    /// transform. <paramref name="holeColor"/> is the surface showing through the counter (default WindowFill).
    /// </summary>
    public static void DrawRGlyph(SKCanvas canvas, float opacity = 1f, SKColor? holeColor = null)
    {
        byte a = A(opacity);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        fill.Color = OffWhite.WithAlpha(a);
        canvas.DrawPath(RStem, fill);
        canvas.DrawPath(RBowl, fill);
        canvas.DrawPath(RLeg, fill);
        fill.Color = (holeColor ?? WindowFill).WithAlpha(a);
        canvas.DrawPath(RHole, fill);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    /// <summary>Two-color wordmark: "Rem" (off-white) + "Ex" (amber), centered horizontally at (cx, baselineY).</summary>
    public static void DrawWordmark(SKCanvas canvas, float cx, float baselineY, float textSizePx, float opacity = 1f)
    {
        using var rem = TextPaint(textSizePx, OffWhite.WithAlpha(A(opacity)), SKTextAlign.Left);
        using var ex = TextPaint(textSizePx, Amber.WithAlpha(A(opacity)), SKTextAlign.Left);
        float wRem = rem.MeasureText("Rem");
        float wEx = ex.MeasureText("Ex");
        float startX = cx - (wRem + wEx) / 2f;
        canvas.DrawText("Rem", startX, baselineY, rem);
        canvas.DrawText("Ex", startX + wRem, baselineY, ex);
    }

    /// <summary>Centered single-color line of brand text (e.g. a tagline).</summary>
    public static void DrawText(SKCanvas canvas, string text, float cx, float baselineY, float textSizePx, SKColor color, float opacity = 1f)
    {
        using var paint = TextPaint(textSizePx, color.WithAlpha(A(opacity)), SKTextAlign.Center);
        canvas.DrawText(text, cx, baselineY, paint);
    }

    /// <summary>Measured width of a run of brand text at the given size (for layout).</summary>
    public static float MeasureText(string text, float textSizePx)
    {
        using var paint = TextPaint(textSizePx, SKColors.White, SKTextAlign.Left);
        return paint.MeasureText(text);
    }

    private static SKPaint TextPaint(float size, SKColor color, SKTextAlign align) => new()
    {
        IsAntialias = true,
        Color = color,
        Typeface = FaceOrDefault,
        TextSize = size,
        TextAlign = align,
    };

    // ── Feature glyphs (ported from SplashBrand.kt drawFeatureGlyph) ─────────────

    private static SKPaint GlyphStroke(float sizePx, float alpha) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = sizePx * 0.09f,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        Color = Amber.WithAlpha(A(alpha)),
    };

    private static SKPaint GlyphFill(float alpha) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = Amber.WithAlpha(A(alpha)),
    };

    /// <summary>Draw an amber line-glyph for <paramref name="kind"/>, centered at (cx,cy), spanning ~sizePx.</summary>
    public static void DrawFeatureGlyph(SKCanvas canvas, FeatureGlyph kind, float cx, float cy, float sizePx, float alpha = 1f)
    {
        float sw = sizePx * 0.09f;
        using var st = GlyphStroke(sizePx, alpha);
        using var fill = GlyphFill(alpha);

        switch (kind)
        {
            case FeatureGlyph.RemoteDesktop:
            {
                float mw = sizePx * 0.9f, mh = sizePx * 0.6f;
                float top = cy - mh / 2f - sizePx * 0.08f;
                canvas.DrawRoundRect(new SKRect(cx - mw / 2f, top, cx + mw / 2f, top + mh), sizePx * 0.08f, sizePx * 0.08f, st);
                float tw = sizePx * 0.16f, tcy = top + mh / 2f;
                using (var tri = new SKPath())
                {
                    tri.MoveTo(cx - tw * 0.5f, tcy - tw);
                    tri.LineTo(cx - tw * 0.5f, tcy + tw);
                    tri.LineTo(cx + tw, tcy);
                    tri.Close();
                    canvas.DrawPath(tri, fill);
                }
                canvas.DrawLine(cx, top + mh, cx, top + mh + sizePx * 0.12f, st);
                canvas.DrawLine(cx - sizePx * 0.2f, top + mh + sizePx * 0.14f, cx + sizePx * 0.2f, top + mh + sizePx * 0.14f, st);
                break;
            }
            case FeatureGlyph.Telemetry:
            {
                float r = sizePx * 0.4f;
                using (var arc = new SKPath())
                {
                    arc.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -60f, 300f);
                    canvas.DrawPath(arc, st);
                }
                canvas.DrawLine(cx, cy - r - sizePx * 0.1f, cx, cy - sizePx * 0.02f, st);
                break;
            }
            case FeatureGlyph.FileTransfer:
            {
                float ax = sizePx * 0.24f, ah = sizePx * 0.34f, head = sizePx * 0.14f;
                float ux = cx - ax;
                canvas.DrawLine(ux, cy + ah, ux, cy - ah, st);
                canvas.DrawLine(ux - head, cy - ah + head, ux, cy - ah, st);
                canvas.DrawLine(ux + head, cy - ah + head, ux, cy - ah, st);
                float dx = cx + ax;
                canvas.DrawLine(dx, cy - ah, dx, cy + ah, st);
                canvas.DrawLine(dx - head, cy + ah - head, dx, cy + ah, st);
                canvas.DrawLine(dx + head, cy + ah - head, dx, cy + ah, st);
                break;
            }
        }
    }
}

/// <summary>The three RemEx capability glyphs surfaced during the splash reveal (and Pong bounces).</summary>
public enum FeatureGlyph { RemoteDesktop, Telemetry, FileTransfer }
