using SkiaSharp;

namespace Remex.Branding;

/// <summary>SkiaSharp realization of the brand mark. Paths parse once; drawing is in 108-unit space.</summary>
public static class SplashBrand
{
    public static readonly SKColor BackdropStart = new(RemexBrandData.BackdropStartArgb);
    public static readonly SKColor BackdropEnd   = new(RemexBrandData.BackdropEndArgb);
    public static readonly SKColor WindowFill    = new(RemexBrandData.WindowFillArgb);
    public static readonly SKColor WindowStroke  = new(RemexBrandData.WindowStrokeArgb);
    public static readonly SKColor Amber         = new(RemexBrandData.AmberArgb);
    public static readonly SKColor SlateLo       = new(RemexBrandData.SlateLoArgb);
    public static readonly SKColor SlateHi       = new(RemexBrandData.SlateHiArgb);
    public static readonly SKColor OffWhite      = new(RemexBrandData.OffWhiteArgb);

    public static SKPath Window  { get; } = Parse(RemexBrandData.WindowPath);
    public static SKPath Dot1    { get; } = Parse(RemexBrandData.Dot1Path);
    public static SKPath Dot2    { get; } = Parse(RemexBrandData.Dot2Path);
    public static SKPath Dot3    { get; } = Parse(RemexBrandData.Dot3Path);
    public static SKPath Chevron { get; } = Parse(RemexBrandData.ChevronPath);
    public static SKPath RStem   { get; } = Parse(RemexBrandData.RStemPath);
    public static SKPath RBowl   { get; } = Parse(RemexBrandData.RBowlPath);
    public static SKPath RLeg    { get; } = Parse(RemexBrandData.RLegPath);
    public static SKPath RHole   { get; } = Parse(RemexBrandData.RHolePath);
    public static SKPath Cursor  { get; } = Parse(RemexBrandData.CursorPath);

    private static SKPath Parse(string d)
    {
        var path = SKPath.ParseSvgPathData(d)
            ?? throw new InvalidOperationException($"Brand path failed to parse: {d}");
        if (path.IsEmpty) throw new InvalidOperationException($"Brand path parsed empty: {d}");
        return path;
    }

    private static byte Alpha(float o) => (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);

    /// <summary>Fill the whole w×h with the diagonal gradient, then draw the mark centered and undistorted.</summary>
    public static void DrawInto(SKCanvas canvas, int width, int height)
    {
        float f0 = RemexBrandData.GradStart / RemexBrandData.Viewport; // 6/108
        float f1 = RemexBrandData.GradEnd / RemexBrandData.Viewport;   // 102/108
        using var bg = new SKPaint { IsAntialias = true };
        bg.Shader = SKShader.CreateLinearGradient(
            new SKPoint(width * f0, height * f0),
            new SKPoint(width * f1, height * f1),
            new[] { BackdropStart, BackdropEnd }, null, SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, bg);

        float s = Math.Min(width, height);
        canvas.Save();
        canvas.Translate((width - s) / 2f, (height - s) / 2f);
        canvas.Scale(s / RemexBrandData.Viewport);
        DrawMark(canvas);
        canvas.Restore();
    }

    /// <summary>Draw the terminal-window mark in 108-unit space (caller sets scale/translation).</summary>
    public static void DrawMark(SKCanvas canvas, float opacity = 1f)
    {
        byte a = Alpha(opacity);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        canvas.Save();
        canvas.Translate(RemexBrandData.GroupPivot, RemexBrandData.GroupPivot);
        canvas.Scale(RemexBrandData.GroupScale);
        canvas.Translate(-RemexBrandData.GroupPivot, -RemexBrandData.GroupPivot);

        fill.Color = WindowFill.WithAlpha(a);
        canvas.DrawPath(Window, fill);
        stroke.Color = WindowStroke.WithAlpha(Alpha(RemexBrandData.WindowStrokeAlpha * opacity));
        stroke.StrokeWidth = RemexBrandData.WindowStrokeWidth;
        canvas.DrawPath(Window, stroke);

        fill.Color = Amber.WithAlpha(a);   canvas.DrawPath(Dot1, fill);
        fill.Color = SlateLo.WithAlpha(a); canvas.DrawPath(Dot2, fill);
        fill.Color = SlateHi.WithAlpha(a); canvas.DrawPath(Dot3, fill);

        stroke.Color = Amber.WithAlpha(a);
        stroke.StrokeWidth = RemexBrandData.ChevronStrokeWidth;
        stroke.StrokeCap = SKStrokeCap.Round;
        stroke.StrokeJoin = SKStrokeJoin.Round;
        canvas.DrawPath(Chevron, stroke);

        fill.Color = OffWhite.WithAlpha(a);
        canvas.DrawPath(RStem, fill);
        canvas.DrawPath(RBowl, fill);
        canvas.DrawPath(RLeg, fill);
        fill.Color = WindowFill.WithAlpha(a);
        canvas.DrawPath(RHole, fill);

        fill.Color = Amber.WithAlpha(a);
        canvas.DrawPath(Cursor, fill);

        canvas.Restore();
    }
}
