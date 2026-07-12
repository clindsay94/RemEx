using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Remex.Branding;

namespace Remex.Desktop.Controls;

/// <summary>
/// Draws the RemEx terminal-window brand mark (gradient backdrop + mark) natively, from the same
/// path data as the launcher icon (Remex.Branding.RemexBrandData). Resolution-independent — used on
/// the About screen. Fixed brand palette by design (not theme-adaptive).
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
    private static readonly IBrush WindowFill = new SolidColorBrush(C(RemexBrandData.WindowFillArgb));
    private static readonly IBrush Amber      = new SolidColorBrush(C(RemexBrandData.AmberArgb));
    private static readonly IBrush SlateLo    = new SolidColorBrush(C(RemexBrandData.SlateLoArgb));
    private static readonly IBrush SlateHi    = new SolidColorBrush(C(RemexBrandData.SlateHiArgb));
    private static readonly IBrush OffWhite   = new SolidColorBrush(C(RemexBrandData.OffWhiteArgb));
    private static readonly IPen WindowStroke = new Pen(
        new SolidColorBrush(C(RemexBrandData.WindowStrokeArgb), RemexBrandData.WindowStrokeAlpha),
        RemexBrandData.WindowStrokeWidth);
    private static readonly IPen ChevronPen = new Pen(Amber, RemexBrandData.ChevronStrokeWidth)
    { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Full-bleed diagonal gradient backdrop.
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

        // Center the 108-unit mark, undistorted, and apply the 0.8 group scale about (54,54).
        double min = Math.Min(w, h);
        double s = min / RemexBrandData.Viewport;
        var center = Matrix.CreateTranslation((w - min) / 2, (h - min) / 2);
        var scale = Matrix.CreateScale(s, s);
        var group = Matrix.CreateTranslation(-RemexBrandData.GroupPivot, -RemexBrandData.GroupPivot)
                    * Matrix.CreateScale(RemexBrandData.GroupScale, RemexBrandData.GroupScale)
                    * Matrix.CreateTranslation(RemexBrandData.GroupPivot, RemexBrandData.GroupPivot);
        using (context.PushTransform(group * scale * center))
        {
            context.DrawGeometry(WindowFill, WindowStroke, Window);
            context.DrawGeometry(Amber, null, Dot1);
            context.DrawGeometry(SlateLo, null, Dot2);
            context.DrawGeometry(SlateHi, null, Dot3);
            context.DrawGeometry(null, ChevronPen, Chevron);
            context.DrawGeometry(OffWhite, null, RStem);
            context.DrawGeometry(OffWhite, null, RBowl);
            context.DrawGeometry(OffWhite, null, RLeg);
            context.DrawGeometry(WindowFill, null, RHole);
            context.DrawGeometry(Amber, null, CursorBar);
        }
    }
}
