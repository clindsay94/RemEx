using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Remex.Desktop.Services;

namespace Remex.Desktop.Controls;

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

    public static readonly StyledProperty<string> SplashStyleProperty =
        AvaloniaProperty.Register<BootSequenceControl, string>(
            nameof(SplashStyle), "RemexCommand");

    public string SplashStyle
    {
        get => GetValue(SplashStyleProperty);
        set => SetValue(SplashStyleProperty, value);
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
    
    // Cosmic splash parameters (Flash and screen shake/shudder displacement offsets)
    private double _shudderX;
    private double _shudderY;
    private double _flashOverlay;

    static BootSequenceControl()
    {
        AffectsRender<BootSequenceControl>(AccentColorProperty);
        AffectsRender<BootSequenceControl>(SplashStyleProperty);
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

        if (SplashStyle == "CosmicZoom")
        {
            // Update particles for Cosmic Starfield!
            // Particles radiate outwards from the center (0.5, 0.5)
            for (int i = 0; i < 25; i++)
            {
                double dx = _particles[i].X - 0.5;
                double dy = _particles[i].Y - 0.5;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 0.01) dist = 0.01;

                // Speed increases as it gets further (warp/hyperdrive effect)
                double speed = (0.02 + dist * 0.8) * dt * 60;
                _particles[i].X += (dx / dist) * speed;
                _particles[i].Y += (dy / dist) * speed;

                // Alpha fades in as it goes outwards, then fades out at the outer edges
                _particles[i].Alpha = Math.Clamp(dist * 3.0, 0, 0.9) * Math.Clamp((0.5 - dist) * 4.0, 0, 1.0);

                // Reset if offscreen or too far
                if (_particles[i].X < 0 || _particles[i].X > 1 || _particles[i].Y < 0 || _particles[i].Y > 1 || dist > 0.5)
                {
                    // Reset to near center
                    double angle = _rng.NextDouble() * Math.PI * 2;
                    double startDist = 0.01 + _rng.NextDouble() * 0.06;
                    _particles[i].X = 0.5 + Math.Cos(angle) * startDist;
                    _particles[i].Y = 0.5 + Math.Sin(angle) * startDist;
                    _particles[i].Vx = Math.Cos(angle) * 0.1; // Direction vectors
                    _particles[i].Vy = Math.Sin(angle) * 0.1;
                    _particles[i].Lifetime = 0;
                    _particles[i].Alpha = 0;
                }
            }

            // Cinematic Zoom-In stages:
            if (_elapsed < 1.8)
            {
                // Acts I: Slow quadratic zoom of Letter R from distance
                double t = _elapsed / 1.8;
                _zoomScale = 0.08 + Math.Pow(t, 2) * 2.12;
                
                _shudderX = 0;
                _shudderY = 0;
                _flashOverlay = 0;
            }
            else
            {
                // Act II: Lightning strike at 1.8s. Screen flashes white/cyan and shudders violently.
                double strikeElapsed = _elapsed - 1.8;
                double strikeDuration = 0.5; // Decays over 500ms
                
                if (strikeElapsed < strikeDuration)
                {
                    double t = strikeElapsed / strikeDuration;
                    _flashOverlay = 1.0 - t; // Linear flash decay
                    
                    // Shudder/vibration offsets: decaying shake intensity
                    double shakeIntensity = (1.0 - t) * 12.0;
                    _shudderX = (_rng.NextDouble() - 0.5) * shakeIntensity;
                    _shudderY = (_rng.NextDouble() - 0.5) * shakeIntensity;
                }
                else
                {
                    _flashOverlay = 0;
                    _shudderX = 0;
                    _shudderY = 0;
                }
                
                // Keep logo at peak scale with subtle breath
                _zoomScale = 2.2 + Math.Sin((_elapsed - 1.8) * 3) * 0.04;
            }

            // Act III: Smooth exit fade to black before transition
            if (_elapsed > 2.5)
            {
                _fadeOverlay = Math.Clamp((_elapsed - 2.5) / 0.5, 0, 1);
            }
        }
        else
        {
            // Particle updates (Original RemexCommand)
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
        }

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

        if (SplashStyle == "CosmicZoom")
        {
            // ── COSMIC ZOOM ANIMATION ──
            
            // Draw Cosmic Starfield
            for (int i = 0; i < 25; i++)
            {
                double sx = _particles[i].X * w;
                double sy = _particles[i].Y * h;
                double dx = _particles[i].X - 0.5;
                double dy = _particles[i].Y - 0.5;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double radius = 0.5 + dist * 3.5; // Stars get larger as they approach the camera

                var opacityBrush = new ImmutableSolidColorBrush(Color.FromArgb((byte)(_particles[i].Alpha * 255), 255, 255, 255));
                ctx.DrawEllipse(opacityBrush, null, new Point(sx, sy), radius, radius);
            }

            // Draw target rings / circular HUD lines in background
            var radBrush = new ImmutableSolidColorBrush(Color.FromArgb(12, primary.R, primary.G, primary.B));
            var radPen = new Pen(radBrush, 1.0);
            double maxRad = Math.Min(w, h) * 0.45;
            foreach (var rf in new[] { 0.25, 0.45, 0.65, 0.85 })
                ctx.DrawEllipse(null, radPen, new Point(w/2, h/2), rf * maxRad, rf * maxRad);

            // Draw center crosshair target
            ctx.DrawLine(radPen, new Point(w/2 - 20, h/2), new Point(w/2 + 20, h/2));
            ctx.DrawLine(radPen, new Point(w/2, h/2 - 20), new Point(w/2, h/2 + 20));

            // Draw expanding energy shockwave circles during lightning strike
            if (_elapsed > 1.8)
            {
                double waveT = Math.Clamp((_elapsed - 1.8) / 0.5, 0, 1);
                double shockRadius = 40 + waveT * 280;
                double shockOpacity = 1.0 - waveT;
                
                var shockPen = new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(shockOpacity * 150), primary.R, primary.G, primary.B)), 3.0 + (1.0 - waveT) * 8.0);
                ctx.DrawEllipse(null, shockPen, new Point(w/2, h/2), shockRadius, shockRadius);

                var secondaryShockPen = new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(shockOpacity * 80), 255, 215, 0)), 1.5 + (1.0 - waveT) * 4.0);
                ctx.DrawEllipse(null, secondaryShockPen, new Point(w/2, h/2), shockRadius * 0.7, shockRadius * 0.7);
            }

            // Draw Zoomed Brand Logo in center (Applying global scale & shudder displacement)
            double s = _zoomScale;
            DrawStylizedRLogo(ctx, w, h, s, primary, 1.0, _elapsed > 1.8 ? Math.Clamp((_elapsed - 1.8) / 0.5, 0, 1) : 0.0, _shudderX, _shudderY, -30);

            // 3. Fading title text "REMOTE EXECUTION"
            if (_elapsed > 1.8)
            {
                double titleFade = Math.Clamp((_elapsed - 1.8) / 0.5, 0, 1);
                var textColor = Color.FromArgb((byte)(titleFade * 255), 255, 255, 255);
                var accentTextColor = Color.FromArgb((byte)(titleFade * 255), primary.R, primary.G, primary.B);
                
                DrawText(ctx, LocalizationService.Instance["Boot_Remote"] ?? "REMOTE", new Point(w / 2 - 120, h / 2 + 50), 38, textColor, true);
                DrawText(ctx, LocalizationService.Instance["Boot_Execution"] ?? "EXECUTION", new Point(w / 2 - 120, h / 2 + 92), 38, accentTextColor, true);
                DrawText(ctx, LocalizationService.Instance["Boot_CommandCenter"] ?? "⚡ COMMAND CENTER", new Point(w / 2 - 68, h / 2 + 146), 11, Color.FromArgb((byte)(titleFade * 150), 220, 240, 255), false);
            }
        }
        else
        {
            // ── ORIGINAL REMEXCOMMAND BOOT SEQUENCE ──
            
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

            // Centers
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

                // Draw the stenciled new "R" logo in place of standard "R"
                DrawStylizedRLogo(ctx, w, h, 0.8, primary, 1.0, 1.0, 0, 0, 0, new Point(w * 0.50 - 90, h * 0.48 - 65));
                DrawText(ctx, "EM", new Point(w * 0.50 - 90 + 88, h * 0.48 - 60), 54, Colors.White, true);
                DrawText(ctx, "EX", new Point(w * 0.50 - 90 + 22, h * 0.48 - 10), 54, Colors.White, true);
                DrawText(ctx, LocalizationService.Instance["Boot_CommandYourPc"] ?? "COMMAND YOUR PC", new Point(w * 0.50 - 76, h * 0.48 + 60), 14, new Color(180, 255, 255, 255), false);
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

                // Draw the stenciled new "R" logo in place of standard "R"
                DrawStylizedRLogo(ctx, w, h, 0.8, primary, 1.0, 1.0, 0, 0, 0, new Point(w * 0.50 - 90, h * 0.48 - 65));
                DrawText(ctx, "EM", new Point(w * 0.50 - 90 + 88, h * 0.48 - 60), 54, Colors.White, true);
                DrawText(ctx, "EX", new Point(w * 0.50 - 90 + 22, h * 0.48 - 10), 54, Colors.White, true);
                DrawText(ctx, LocalizationService.Instance["Boot_Ote"] ?? "ote", new Point(w * 0.50 - 90 + 88 + 74, h * 0.48 - 35), 24, primary, false);
                DrawText(ctx, LocalizationService.Instance["Boot_Ecution"] ?? "ecution", new Point(w * 0.50 - 90 + 22 + 58, h * 0.48 + 15), 24, primary, false);
                DrawText(ctx, LocalizationService.Instance["Boot_CommandYourPc"] ?? "COMMAND YOUR PC", new Point(w * 0.50 - 76, h * 0.48 + 60), 14, new Color(180, 255, 255, 255), false);

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
        }

        // Full-screen White Screen Flash overlay during lightning strike (at 1.8s)
        if (SplashStyle == "CosmicZoom" && _flashOverlay > 0)
        {
            var flashBrush = new ImmutableSolidColorBrush(Color.FromArgb((byte)(_flashOverlay * 255), 230, 248, 255));
            ctx.DrawRectangle(flashBrush, null, bounds);
        }

        // Fade overlay
        if (_fadeOverlay > 0)
        {
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb((byte)(_fadeOverlay * 255), substrate.R, substrate.G, substrate.B)), null, bounds);
        }
    }

    private void DrawStylizedRLogo(DrawingContext ctx, double w, double h, double scale, Color accentColor, double opacity, double lightningFade, double shudderX = 0, double shudderY = 0, double yOffset = -30, Point? customPos = null)
    {
        // Calculate coordinates based on centering or custom layout
        double tx = customPos.HasValue ? customPos.Value.X + shudderX : w / 2 - 54 * scale + shudderX;
        double ty = customPos.HasValue ? customPos.Value.Y + yOffset * scale + shudderY : h / 2 - 54 * scale + yOffset * scale + shudderY;

        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(tx, ty);
        using var transform = ctx.PushTransform(matrix);

        // Stenciled outline "R" geometry (at 0,0 relative origin)
        var rGeo = new StreamGeometry();
        using (var gctx = rGeo.Open())
        {
            gctx.BeginFigure(new Point(28, 90), false);
            gctx.LineTo(new Point(28, 18));
            gctx.LineTo(new Point(64, 18));
            gctx.ArcTo(new Point(64, 54), new Size(18, 18), 0, false, SweepDirection.Clockwise);
            gctx.LineTo(new Point(28, 54));
            gctx.EndFigure(false);

            gctx.BeginFigure(new Point(46, 54), false);
            gctx.LineTo(new Point(72, 90));
            gctx.EndFigure(false);
        }

        // Glow backing
        double pulse = Math.Sin(_elapsed * 8) * 1.5;
        ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(opacity * 90), accentColor.R, accentColor.G, accentColor.B)), 8.0 + pulse), rGeo);
        ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(opacity * 255), accentColor.R, accentColor.G, accentColor.B)), 4.5), rGeo);
        ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255)), 1.5), rGeo);

        // Lightning Bolt
        if (lightningFade > 0)
        {
            var lightningGeo = new StreamGeometry();
            using (var gctx = lightningGeo.Open())
            {
                gctx.BeginFigure(new Point(31.5 + 24, 9.0), true);
                gctx.LineTo(new Point(31.5 + 24, 58.5));
                gctx.LineTo(new Point(45.0 + 24, 58.5));
                gctx.LineTo(new Point(45.0 + 24, 99.0));
                gctx.LineTo(new Point(76.5 + 24, 45.0));
                gctx.LineTo(new Point(58.5 + 24, 45.0));
                gctx.LineTo(new Point(76.5 + 24, 9.0));
                gctx.EndFigure(true);
            }

            var goldBrush = new LinearGradientBrush
            {
                Opacity = opacity * lightningFade,
                StartPoint = new RelativePoint(0.3, 0.1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.7, 0.9, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#FFFFD700"), 0.0),
                    new GradientStop(Color.Parse("#FFFF8C00"), 0.5),
                    new GradientStop(Color.Parse("#FFFF4500"), 1.0)
                }
            };

            var glowBrush = new ImmutableSolidColorBrush(Color.FromArgb((byte)(opacity * lightningFade * 120), 255, 140, 0));
            ctx.DrawGeometry(null, new Pen(glowBrush, 6.0 + Math.Sin(_elapsed * 15) * 2.0), lightningGeo);
            ctx.DrawGeometry(goldBrush, new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(opacity * lightningFade * 180), 255, 255, 255)), 1.5), lightningGeo);
        }
    }

    private void DrawText(DrawingContext ctx, string text, Point pos, double size, Color color, bool bold)
    {
        FontFamily ff = FontFamily.Default;
        if (this.TryFindResource("JetBrainsMono", out object? f) && f is FontFamily customFf)
        {
            ff = customFf;
        }
        var tf = new Typeface(ff, FontStyle.Normal, bold ? FontWeight.Black : FontWeight.Medium);
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
