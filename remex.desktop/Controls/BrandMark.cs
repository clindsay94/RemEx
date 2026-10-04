using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Remex.Branding;

namespace Remex.Desktop.Controls;

/// <summary>
/// Draws the RemEx terminal-window brand mark (gradient backdrop + mark) natively, from the same
/// path data as the launcher icon (Remex.Branding.RemexBrandData). Resolution-independent — used on
/// the About screen and the drawer header (ShellView.axaml). Brand default, runtime recolour
/// allowed (RemEx-alwfa.1): the window's own fill follows the live theme's primary -&gt; tertiary
/// gradient (the same resources <see cref="Remex.Desktop.Services.ThemeService"/> publishes for every other themed
/// surface — "AccentPrimary" and "PaletteTertiary"), so the drawer-header mark matches whatever
/// seed the splash just painted once the drawer opens. The backdrop and the accent details (amber,
/// slate, off-white) stay the fixed brand colours — only the mark gradient recolours (decision (c)).
/// </summary>
public sealed class BrandMark : Control
{
    private static readonly Geometry Window  = Geometry.Parse(RemexBrandData.WindowPath);
    private static readonly Geometry Dot1    = Geometry.Parse(RemexBrandData.Dot1Path);
    private static readonly Geometry Dot2    = Geometry.Parse(RemexBrandData.Dot2Path);
    private static readonly Geometry Dot3    = Geometry.Parse(RemexBrandData.Dot3Path);
    private static readonly Geometry Chevron = Geometry.Parse(RemexBrandData.ChevronPath);
    private static readonly Geometry RStem   = Geometry.Parse(RemexBrandData.RStemPath);
    private static readonly Geometry RBowl   = Geometry.Parse(RemexBrandData.RBowlPath);
    private static readonly Geometry RLeg    = Geometry.Parse(RemexBrandData.RLegPath);
    private static readonly Geometry RHole   = Geometry.Parse(RemexBrandData.RHolePath);
    private static readonly Geometry CursorBar = Geometry.Parse(RemexBrandData.CursorPath);

    private static Color C(uint argb) => Color.FromUInt32(argb);

    /// <summary>Fallback for the mark gradient's two stops when the theme resources are not (yet)
    /// published — e.g. rendered outside a themed <c>Application</c>. Identical to the brand
    /// default's own window fill, so the gradient collapses to the same solid colour it always drew.</summary>
    private static readonly Color FallbackMarkColor = C(RemexBrandData.WindowFillArgb);

    private static readonly IBrush Amber      = new SolidColorBrush(C(RemexBrandData.AmberArgb));
    private static readonly IBrush SlateLo    = new SolidColorBrush(C(RemexBrandData.SlateLoArgb));
    private static readonly IBrush SlateHi    = new SolidColorBrush(C(RemexBrandData.SlateHiArgb));
    private static readonly IPen WindowStroke = new Pen(
        new SolidColorBrush(C(RemexBrandData.WindowStrokeArgb), RemexBrandData.WindowStrokeAlpha),
        RemexBrandData.WindowStrokeWidth);
    private static readonly IPen ChevronPen = new Pen(Amber, RemexBrandData.ChevronStrokeWidth)
    { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    /// <summary>
    /// Repaints on a theme switch. <c>ResourcesChanged</c> rather than <c>ActualThemeVariantChanged</c>
    /// — same reasoning as <see cref="Controls.SparklineControl"/> and <c>CanvasMinimap</c>: this
    /// control paints in <see cref="Render"/>, which nothing but an explicit invalidate re-triggers,
    /// and <c>ThemeService</c> repaints by swapping resource overrides (a seed change can leave the
    /// Dark/Light variant itself unchanged).
    /// </summary>
    public BrandMark()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    private Color ResolveThemeColor(string key) =>
        this.TryFindResource(key, out var value) && value is Color color ? color : FallbackMarkColor;

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Full-bleed diagonal gradient backdrop — the fixed brand colours (decision (c): only the
        // mark recolours, not the backdrop, for this control).
        double f0 = RemexBrandData.GradStart / RemexBrandData.Viewport;
        double f1 = RemexBrandData.GradEnd / RemexBrandData.Viewport;
        var bg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(f0, f0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(f1, f1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(C(RemexBrandData.BackdropStartArgb), 0),
                new GradientStop(C(RemexBrandData.BackdropEndArgb), 1),
            },
        };
        context.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        // Fit the terminal-window mark to fill the control: map the window geometry's bounding box
        // to the centered square so the window's border becomes the icon's edge — no surrounding
        // padding — while preserving proportions. Half the window stroke is inset so it isn't clipped.
        double min = Math.Min(w, h);
        var wb = Window.Bounds;
        double boxSide = Math.Max(wb.Width, wb.Height);
        if (boxSide <= 0) return;
        // size (px) / (box + stroke) (path units) — reserves the stroke so it isn't clipped, units-consistent.
        double s = min / (boxSide + RemexBrandData.WindowStrokeWidth);
        var fit = Matrix.CreateTranslation(-(wb.X + wb.Width / 2), -(wb.Y + wb.Height / 2))
                  * Matrix.CreateScale(s, s)
                  * Matrix.CreateTranslation(w / 2, h / 2);

        // The window's own fill is the live theme's primary -> tertiary gradient (RemEx-alwfa.1
        // decision (c)). Built from fixed (absolute) points in the SAME local coordinate space the
        // fit transform establishes, and reused verbatim for RHole below, so the "hole" reads as a
        // punch through the window's fill rather than a mismatched patch — mirrors the identical
        // trick in Remex.Branding.SplashBrand.DrawMark.
        var primary = ResolveThemeColor("AccentPrimary");
        var tertiary = ResolveThemeColor("PaletteTertiary");
        var onPrimary = this.TryFindResource("AccentForegroundBrush", out var fg) && fg is ISolidColorBrush fgBrush
            ? fgBrush.Color
            : BrandMarkGlyph.OffWhiteColor;
        var glyph = new SolidColorBrush(BrandMarkGlyph.GlyphColor(primary, tertiary, onPrimary));
        double mf0 = RemexBrandData.GradStart / RemexBrandData.Viewport;
        double mf1 = RemexBrandData.GradEnd / RemexBrandData.Viewport;
        var markFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(wb.Left + wb.Width * mf0, wb.Top + wb.Height * mf0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(wb.Left + wb.Width * mf1, wb.Top + wb.Height * mf1, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(primary, 0), new GradientStop(tertiary, 1) },
        };

        using (context.PushTransform(fit))
        {
            context.DrawGeometry(markFill, WindowStroke, Window);
            context.DrawGeometry(Amber, null, Dot1);
            context.DrawGeometry(SlateLo, null, Dot2);
            context.DrawGeometry(SlateHi, null, Dot3);
            context.DrawGeometry(null, ChevronPen, Chevron);
            context.DrawGeometry(glyph, null, RStem);
            context.DrawGeometry(glyph, null, RBowl);
            context.DrawGeometry(glyph, null, RLeg);
            context.DrawGeometry(markFill, null, RHole);
            context.DrawGeometry(Amber, null, CursorBar);
        }
    }
}

/// <summary>The brand mark's R colour rule, kept apart from <see cref="BrandMark"/> so it can be tested
/// without a render platform (BrandMark's static geometry needs one to parse).</summary>
internal static class BrandMarkGlyph
{
    internal static readonly Color OffWhiteColor = Color.FromUInt32(RemexBrandData.OffWhiteArgb);

    /// <summary>The R needs 3:1 against both ends of the window's gradient (graphics, WCAG 1.4.11).</summary>
    internal const double GlyphMinimumContrast = 3.0;

    /// <summary>
    /// The R's colour (RemEx-pp4cm.23). It was always the brand off-white, but the window behind it
    /// is the live primary -> tertiary gradient, and a near-white seed in Dark at contrast 1 (Ink-Dark-C1)
    /// makes that gradient near-white too: the R vanished. The brand off-white stays whenever it clears
    /// <see cref="GlyphMinimumContrast"/> against both stops; otherwise the palette's OnPrimary, the role
    /// made to sit on Primary, takes over, or whichever of the two reads better if neither clears it.
    /// </summary>
    internal static Color GlyphColor(Color primary, Color tertiary, Color onPrimary)
    {
        double Worst(Color c) => Math.Min(ContrastRatio(c, primary), ContrastRatio(c, tertiary));
        if (Worst(OffWhiteColor) >= GlyphMinimumContrast) return OffWhiteColor;
        return Worst(onPrimary) >= Worst(OffWhiteColor) ? onPrimary : OffWhiteColor;
    }

    internal static double ContrastRatio(Color a, Color b)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double L(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
