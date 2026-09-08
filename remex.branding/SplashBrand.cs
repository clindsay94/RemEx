using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// SkiaSharp realization of the brand mark. Paths parse once; drawing is in 108-unit space.
/// <see cref="BackdropStart"/>, <see cref="BackdropEnd"/>, <see cref="MarkStart"/>,
/// <see cref="MarkEnd"/> and <see cref="Amber"/> are mutable (RemEx-alwfa.1) — <see cref="ApplyPalette"/>
/// overwrites them from a <see cref="SplashPalette"/> before a frame paints, which is how the splash
/// variants (CosmicZoomVariant, PongVariant, RemexCommandVariant) recolour without each one taking a
/// palette parameter of its own: they already read these statics on every frame. Process-wide state
/// by design — one desktop app process, one active splash — so nothing here needs to be threadsafe.
/// </summary>
public static partial class SplashBrand
{
    public static SKColor BackdropStart { get; private set; } = new(RemexBrandData.BackdropStartArgb);
    public static SKColor BackdropEnd   { get; private set; } = new(RemexBrandData.BackdropEndArgb);
    public static readonly SKColor WindowFill    = new(RemexBrandData.WindowFillArgb);
    public static readonly SKColor WindowStroke  = new(RemexBrandData.WindowStrokeArgb);
    public static SKColor Amber         { get; private set; } = new(RemexBrandData.AmberArgb);
    public static readonly SKColor SlateLo       = new(RemexBrandData.SlateLoArgb);
    public static readonly SKColor SlateHi       = new(RemexBrandData.SlateHiArgb);
    public static readonly SKColor OffWhite      = new(RemexBrandData.OffWhiteArgb);

    /// <summary>The terminal-window mark's own gradient fill (primary -&gt; tertiary once a seed palette
    /// is applied). Defaults to <see cref="RemexBrandData.WindowFillArgb"/> at both stops, which is a
    /// solid fill identical to the brand default's look.</summary>
    public static SKColor MarkStart { get; private set; } = new(RemexBrandData.WindowFillArgb);
    public static SKColor MarkEnd   { get; private set; } = new(RemexBrandData.WindowFillArgb);

    /// <summary>Overwrites the mutable palette fields above from <paramref name="palette"/>. Call before
    /// painting a frame; see the class remarks for why this is global, mutable state.</summary>
    public static void ApplyPalette(SplashPalette palette)
    {
        BackdropStart = new SKColor(palette.BackdropStart);
        BackdropEnd   = new SKColor(palette.BackdropEnd);
        MarkStart     = new SKColor(palette.MarkStart);
        MarkEnd       = new SKColor(palette.MarkEnd);
        Amber         = new SKColor(palette.Accent);
    }

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

    /// <summary>Brand-default overload — <see cref="BrandRasterizer"/> and BrandAssetGen's checked-in
    /// rasters always render this, never a seed palette (RemEx-alwfa.1 decision (e)).</summary>
    public static void DrawInto(SKCanvas canvas, int width, int height) =>
        DrawInto(canvas, width, height, SplashPalette.Default);

    /// <summary>Fill the whole w×h with the diagonal gradient, then draw the mark centered and undistorted.</summary>
    public static void DrawInto(SKCanvas canvas, int width, int height, SplashPalette palette)
    {
        ApplyPalette(palette);
        float f0 = RemexBrandData.GradStart / RemexBrandData.Viewport; // 6/108
        float f1 = RemexBrandData.GradEnd / RemexBrandData.Viewport;   // 102/108
        using var bg = new SKPaint { IsAntialias = true };
        bg.Shader = SKShader.CreateLinearGradient(
            new SKPoint(width * f0, height * f0),
            new SKPoint(width * f1, height * f1),
            new[] { BackdropStart, BackdropEnd }, null, SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, bg);

        // Fit the window mark's bounds to fill the canvas so the window border becomes the icon's
        // edge (no surrounding padding), proportions preserved; the stroke is reserved so it isn't
        // clipped. Matches the in-app BrandMark control. Group padding is disabled here (the fit fills).
        float s = Math.Min(width, height);
        var wb = Window.Bounds;
        float boxSide = Math.Max(wb.Width, wb.Height);
        if (boxSide <= 0f) return;
        float scale = s / (boxSide + RemexBrandData.WindowStrokeWidth);
        canvas.Save();
        canvas.Translate(width / 2f, height / 2f);
        canvas.Scale(scale);
        canvas.Translate(-(wb.Left + wb.Width / 2f), -(wb.Top + wb.Height / 2f));
        DrawMark(canvas, 1f, 1f);
        canvas.Restore();
    }

    /// <summary>Draw the terminal-window mark in 108-unit space (caller sets scale/translation).</summary>
    public static void DrawMark(SKCanvas canvas, float opacity = 1f, float? groupScale = null)
    {
        byte a = Alpha(opacity);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        float gs = groupScale ?? RemexBrandData.GroupScale;
        canvas.Save();
        canvas.Translate(RemexBrandData.GroupPivot, RemexBrandData.GroupPivot);
        canvas.Scale(gs);
        canvas.Translate(-RemexBrandData.GroupPivot, -RemexBrandData.GroupPivot);

        // The window's own fill is the "mark gradient" (RemEx-alwfa.1 decision (c)): MarkStart ->
        // MarkEnd, diagonal, same fractions as the backdrop gradient in DrawInto. Built from a fixed
        // (absolute) pair of points rather than a relative shader so RHole below — drawn later, over
        // RBowl — can reuse the SAME shader instance and land on exactly the same colours at exactly
        // the same canvas position, which is what makes the "hole" read as a punch through the
        // window's fill rather than a mismatched patch. At the default palette MarkStart == MarkEnd,
        // so this collapses to the same solid fill the brand default always painted.
        var markBounds = Window.Bounds;
        float mf0 = RemexBrandData.GradStart / RemexBrandData.Viewport;
        float mf1 = RemexBrandData.GradEnd / RemexBrandData.Viewport;
        using var markShader = SKShader.CreateLinearGradient(
            new SKPoint(markBounds.Left + markBounds.Width * mf0, markBounds.Top + markBounds.Height * mf0),
            new SKPoint(markBounds.Left + markBounds.Width * mf1, markBounds.Top + markBounds.Height * mf1),
            new[] { MarkStart, MarkEnd }, null, SKShaderTileMode.Clamp);

        fill.Shader = markShader;
        fill.Color = SKColors.White.WithAlpha(a);
        canvas.DrawPath(Window, fill);
        fill.Shader = null;
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
        // Same shader instance and alpha as the Window fill above, so the "hole" reads as a punch
        // through the window's own gradient rather than a mismatched solid patch.
        fill.Shader = markShader;
        fill.Color = SKColors.White.WithAlpha(a);
        canvas.DrawPath(RHole, fill);
        fill.Shader = null;

        fill.Color = Amber.WithAlpha(a);
        canvas.DrawPath(Cursor, fill);

        canvas.Restore();
    }
}
