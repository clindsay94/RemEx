using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Remex.Client.Controls;

public class BootSequenceControl : Control
{
    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<BootSequenceControl, Color>(
            nameof(AccentColor), Color.Parse("#00F0FF"));

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public event Action? SequenceCompleted;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private double _elapsed;
    private bool _completed;

    private readonly Random _rng = new(42);

    private struct Particle { public double X, Y, Vx, Vy, Lifetime, MaxLifetime, Alpha; }
    private Particle[] _particles = new Particle[25];

    private struct StreamParticle { public double T, Speed, Radius, Alpha; }
    private StreamParticle[] _streamParticles = new StreamParticle[12];

    private double _scanProgress;
    private double _waveProgress;
    private double _connectionGlow;
    private double _zoomScale = 1.0;
    private double _zoomProgress = 0.0;
    private double _fadeOverlay = 0.0;

    static BootSequenceControl()
    {
        AffectsRender<BootSequenceControl>(AccentColorProperty);
    }

    public BootSequenceControl()
    {
        for (int i = 0; i < 25; i++)
        {
            _particles[i] = new Particle
            {
                X = _rng.NextDouble(),
                Y = _rng.NextDouble(),
                Vx = (_rng.NextDouble() - 0.5) * 0.005,
                Vy = -(0.01 + _rng.NextDouble() * 0.02),
                Lifetime = _rng.NextDouble() * 2.0,
                MaxLifetime = 2.0 + _rng.NextDouble() * 2.0,
                Alpha = 0
            };
        }

        for (int i = 0; i < 12; i++)
        {
            _streamParticles[i] = new StreamParticle
            {
                T = _rng.NextDouble(),
                Speed = 0.008 + _rng.NextDouble() * 0.012,
                Radius = 1.0 + _rng.NextDouble() * 2.5,
                Alpha = 0.3 + _rng.NextDouble() * 0.6
            };
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _stopwatch.Restart();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _stopwatch.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        _elapsed += dt;

        // Particle updates
        for (int i = 0; i < 25; i++)
        {
            _particles[i].Lifetime += dt;
            _particles[i].X += _particles[i].Vx;
            _particles[i].Y += _particles[i].Vy * dt;
            double t = _particles[i].Lifetime / _particles[i].MaxLifetime;
            if (t < 0.2) _particles[i].Alpha = (t / 0.2) * 0.7;
            else if (t < 0.8) _particles[i].Alpha = 0.7;
            else _particles[i].Alpha = (1.0 - (t - 0.8) / 0.2) * 0.7;

            if (_particles[i].Lifetime >= _particles[i].MaxLifetime)
            {
                _particles[i].X = _rng.NextDouble();
                _particles[i].Y = 0.4 + _rng.NextDouble() * 0.6;
                _particles[i].Vx = (_rng.NextDouble() - 0.5) * 0.016;
                _particles[i].Vy = -(0.02 + _rng.NextDouble() * 0.02);
                _particles[i].Lifetime = 0;
                _particles[i].MaxLifetime = 1.5 + _rng.NextDouble() * 1.5;
            }
        }

        for (int i = 0; i < 12; i++)
        {
            _streamParticles[i].T += _streamParticles[i].Speed * dt * 60;
            if (_streamParticles[i].T > 1.0) _streamParticles[i].T -= 1.0;
        }

        // Animation logic (Complimentary: Monitor scans first, Phone waves)
        _scanProgress = Math.Clamp(_elapsed / 2.0, 0, 1.2);

        if (_elapsed > 0.8)
            _waveProgress = Math.Clamp((_elapsed - 0.8) / 2.0, 0, 1.2);

        if (_elapsed > 1.6)
            _connectionGlow = Math.Clamp((_elapsed - 1.6) / 0.4, 0, 1);

        if (_elapsed > 2.0)
        {
            double zt = Math.Clamp((_elapsed - 2.0) / 0.7, 0, 1);
            double ease = zt < 0.5 ? 4 * zt * zt * zt : 1 - Math.Pow(-2 * zt + 2, 3) / 2;
            _zoomScale = 1.0 + ease * 5.0;
            _zoomProgress = ease;
        }

        if (_elapsed > 2.3)
            _fadeOverlay = Math.Clamp((_elapsed - 2.3) / 0.4, 0, 1);

        if (_elapsed >= 3.0 && !_completed)
        {
            _completed = true;
            _timer.Stop();
            SequenceCompleted?.Invoke();
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        double w = bounds.Width;
        double h = bounds.Height;

        Color primary = AccentColor;
        Color secondary = new Color(255, primary.B, primary.R, primary.G);
        Color tertiary = new Color(255, primary.G, primary.B, primary.R);
        Color substrate = Color.Parse("#050508");
        Color surface = new Color(255, 30, 30, 30);
        Color surfaceVariant = new Color(255, 45, 45, 45);

        ctx.DrawRectangle(new ImmutableSolidColorBrush(substrate), null, bounds);

        double s = _zoomScale;
        double monCxT = w * 0.22;
        double monCyT = h * 0.20;
        double targetDx = (w / 2.0 - monCxT) * (s - 1.0);
        double targetDy = (h / 2.0 - monCyT) * (s - 1.0);

        var matrix = Matrix.CreateScale(s, s) * Matrix.CreateTranslation(targetDx * _zoomProgress, targetDy * _zoomProgress);
        using var transform = ctx.PushTransform(matrix);

        // Geometry
        double monitorW = w * 0.52;
        double monitorH = monitorW * 0.62;
        double monitorX = w * 0.22 - monitorW / 2.0;
        double monitorY = h * 0.20 - monitorH / 2.0;
        double monitorCx = monitorX + monitorW / 2.0;
        double monitorCy = monitorY + monitorH / 2.0;

        double phoneW = w * 0.22;
        double phoneH = phoneW * 1.8;
        double phoneX = w * 0.75 - phoneW / 2.0;
        double phoneY = h * 0.72 - phoneH / 2.0;
        double phoneCx = phoneX + phoneW / 2.0;
        double phoneCy = phoneY + phoneH / 2.0;

        // Centers (Complimentary logic: Monitor is scan center, Phone is wave center)
        Point scanCenter = new Point(monitorCx, monitorCy);
        Point waveCenter = new Point(phoneCx, phoneCy);

        double maxRadiusPx = Math.Sqrt(w * w + h * h);
        double scanRadius = maxRadiusPx * _scanProgress;
        double waveRadius = maxRadiusPx * _waveProgress;

        // Background traces
        var traceRng = new Random(55);
        for (int i = 0; i < 20; i++)
        {
            double sx = traceRng.NextDouble() * w;
            double sy = traceRng.NextDouble() * h;
            double seg1 = 30 + traceRng.NextDouble() * 80;
            double seg2 = 20 + traceRng.NextDouble() * 60;
            bool horiz = traceRng.NextDouble() > 0.5;
            double d1 = traceRng.NextDouble() > 0.5 ? 1 : -1;
            double d2 = traceRng.NextDouble() > 0.5 ? 1 : -1;
            
            double tx, ty, ex, ey;
            if (horiz) { tx = sx + seg1 * d1; ty = sy; ex = tx; ey = ty + seg2 * d2; }
            else { tx = sx; ty = sy + seg1 * d1; ex = tx + seg2 * d2; ey = ty; }

            var geo = new StreamGeometry();
            using (var gctx = geo.Open())
            {
                gctx.BeginFigure(new Point(sx, sy), false);
                gctx.LineTo(new Point(tx, ty));
                gctx.LineTo(new Point(ex, ey));
                gctx.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(new Color(15, surface.R, surface.G, surface.B)), 1.5), geo);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(new Color(15, surface.R, surface.G, surface.B)), null, new Point(tx, ty), 4, 4);
        }

        // Radial grid
        var radBrush = new ImmutableSolidColorBrush(new Color(8, surfaceVariant.R, surfaceVariant.G, surfaceVariant.B));
        var radPen = new Pen(radBrush, 1.0);
        double maxRad = Math.Min(w, h) * 0.45;
        foreach (var rf in new[] { 0.25, 0.45, 0.65, 0.85 })
            ctx.DrawEllipse(null, radPen, new Point(w/2, h/2), rf * maxRad, rf * maxRad);

        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4.0;
            ctx.DrawLine(radPen, new Point(w/2, h/2), new Point(w/2 + maxRad * Math.Cos(a), h/2 + maxRad * Math.Sin(a)));
        }

        // Wireframe (clipped by scan)
        if (scanRadius > 0)
        {
            var scanGeo = new StreamGeometry();
            using (var gctx = scanGeo.Open())
            {
                gctx.BeginFigure(new Point(scanCenter.X + scanRadius, scanCenter.Y), true);
                gctx.ArcTo(new Point(scanCenter.X - scanRadius, scanCenter.Y), new Size(scanRadius, scanRadius), 0, false, SweepDirection.Clockwise);
                gctx.ArcTo(new Point(scanCenter.X + scanRadius, scanCenter.Y), new Size(scanRadius, scanRadius), 0, false, SweepDirection.Clockwise);
                gctx.EndFigure(true);
            }
            using var clip = ctx.PushGeometryClip(scanGeo);

            var primPen = new Pen(new ImmutableSolidColorBrush(primary), 3.0);
            var secPen = new Pen(new ImmutableSolidColorBrush(secondary), 2.0);
            
            ctx.DrawRectangle(null, primPen, new Rect(monitorX, monitorY, monitorW, monitorH), monitorW * 0.06, monitorW * 0.06);
            ctx.DrawLine(primPen, new Point(monitorCx, monitorY + monitorH), new Point(monitorCx, monitorY + monitorH + monitorH * 0.18));
            
            ctx.DrawRectangle(null, secPen, new Rect(phoneX, phoneY, phoneW, phoneH), phoneW * 0.15, phoneW * 0.15);

            DrawText(ctx, "REM", new Point(w * 0.50 - 80, h * 0.48 - 60), 54, Colors.White, true);
            DrawText(ctx, "EX", new Point(w * 0.50 - 40, h * 0.48 - 10), 54, Colors.White, true);
            DrawText(ctx, "COMMAND YOUR PC", new Point(w * 0.50 - 65, h * 0.48 + 60), 14, new Color(180, 255, 255, 255), false);
        }

        // Solid (clipped by wave)
        if (waveRadius > 0)
        {
            var waveGeo = new StreamGeometry();
            using (var gctx = waveGeo.Open())
            {
                gctx.BeginFigure(new Point(waveCenter.X + waveRadius, waveCenter.Y), true);
                gctx.ArcTo(new Point(waveCenter.X - waveRadius, waveCenter.Y), new Size(waveRadius, waveRadius), 0, false, SweepDirection.Clockwise);
                gctx.ArcTo(new Point(waveCenter.X + waveRadius, waveCenter.Y), new Size(waveRadius, waveRadius), 0, false, SweepDirection.Clockwise);
                gctx.EndFigure(true);
            }
            using var clip = ctx.PushGeometryClip(waveGeo);

            ctx.DrawRectangle(new ImmutableSolidColorBrush(new Color(255, (byte)(primary.R*0.3), (byte)(primary.G*0.3), (byte)(primary.B*0.3))), null, new Rect(monitorX, monitorY, monitorW, monitorH), monitorW * 0.06, monitorW * 0.06);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(substrate), null, new Rect(monitorX + monitorW*0.04, monitorY + monitorW*0.04, monitorW - monitorW*0.08, monitorH - monitorW*0.08), monitorW * 0.03, monitorW * 0.03);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Colors.White), null, new Rect(phoneX, phoneY, phoneW, phoneH), phoneW * 0.15, phoneW * 0.15);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(substrate), null, new Rect(phoneX + phoneW*0.08, phoneY + phoneW*0.12, phoneW - phoneW*0.16, phoneH - phoneW*0.2), phoneW * 0.08, phoneW * 0.08);

            DrawText(ctx, "REM", new Point(w * 0.50 - 80, h * 0.48 - 60), 54, Colors.White, true);
            DrawText(ctx, "EX", new Point(w * 0.50 - 40, h * 0.48 - 10), 54, Colors.White, true);
            DrawText(ctx, "ote", new Point(w * 0.50 + 25, h * 0.48 - 35), 24, primary, false);
            DrawText(ctx, "ecution", new Point(w * 0.50 + 35, h * 0.48 + 15), 24, primary, false);
            DrawText(ctx, "COMMAND YOUR PC", new Point(w * 0.50 - 65, h * 0.48 + 60), 14, new Color(180, 255, 255, 255), false);

            // Connection Stream
            Point connStart = new Point(monitorCx + monitorW * 0.3, monitorCy + monitorH * 0.3);
            Point connEnd = new Point(phoneCx, phoneCy - phoneH * 0.35);
            Point connCtrl1 = new Point(monitorCx + w * 0.2, monitorCy + h * 0.15);
            Point connCtrl2 = new Point(phoneCx - w * 0.15, phoneCy - h * 0.25);

            var streamGeo = new StreamGeometry();
            using (var gctx = streamGeo.Open())
            {
                gctx.BeginFigure(connStart, false);
                gctx.CubicBezierTo(connCtrl1, connCtrl2, connEnd);
                gctx.EndFigure(false);
            }

            double glowAlpha = 0.08 + _connectionGlow * 0.25;
            ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(new Color((byte)(glowAlpha*255), secondary.R, secondary.G, secondary.B)), 6 + _connectionGlow * 18), streamGeo);
            
            var corePen = new Pen(new ImmutableSolidColorBrush(new Color((byte)((0.25 + _connectionGlow * 0.6)*255), secondary.R, secondary.G, secondary.B)), 1.5 + _connectionGlow * 2);
            corePen.DashStyle = new DashStyle(new double[] { 10, 10 }, _elapsed * -60);
            ctx.DrawGeometry(null, corePen, streamGeo);

            foreach (var sp in _streamParticles)
            {
                Point pos = CubicBezier(connStart, connCtrl1, connCtrl2, connEnd, sp.T);
                ctx.DrawEllipse(new ImmutableSolidColorBrush(new Color((byte)(sp.Alpha*255), secondary.R, secondary.G, secondary.B)), null, pos, sp.Radius, sp.Radius);
            }
        }

        // Render Rings (radar edges)
        if (scanRadius > 0 && scanRadius < maxRadiusPx * 1.2)
            ctx.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(new Color(50, primary.R, primary.G, primary.B)), 18), scanCenter, scanRadius, scanRadius);
        
        if (waveRadius > 0 && waveRadius < maxRadiusPx * 1.2)
            ctx.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(new Color(45, secondary.R, secondary.G, secondary.B)), 14), waveCenter, waveRadius, waveRadius);

        // Fade overlay
        if (_fadeOverlay > 0)
        {
            ctx.DrawRectangle(new ImmutableSolidColorBrush(new Color((byte)(_fadeOverlay * 255), substrate.R, substrate.G, substrate.B)), null, bounds);
        }
    }

    private void DrawText(DrawingContext ctx, string text, Point pos, double size, Color color, bool bold)
    {
        var tf = new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Black : FontWeight.Medium);
        var fmt = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, new ImmutableSolidColorBrush(color));
        ctx.DrawText(fmt, pos);
    }

    private Point CubicBezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        double tt = t * t;
        double uu = u * u;
        double uuu = uu * u;
        double ttt = tt * t;
        return new Point(
            uuu * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + ttt * p3.X,
            uuu * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + ttt * p3.Y
        );
    }
}
