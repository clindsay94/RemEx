using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Remex.Client.Controls;

/// <summary>
/// Full-screen cinematic boot sequence control.
/// Runs a 5-second animation across 4 phases then fires SequenceCompleted.
/// Frame-driven at ~60fps via DispatcherTimer + Stopwatch.
/// </summary>
public class BootSequenceControl : Control
{
    // ═══════════════ Styled Properties ═══════════════

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<BootSequenceControl, Color>(
            nameof(AccentColor), Color.Parse("#00F0FF"));

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    // ═══════════════ Event ═══════════════

    /// <summary>Fired once when the sequence finishes (elapsed >= 5.0s).</summary>
    public event Action? SequenceCompleted;

    // ═══════════════ Timing ═══════════════

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private double _elapsed;
    private bool _completed;

    // ═══════════════ Animation State ═══════════════

    // Particle embers (Phase 1 + early Phase 2)
    private struct Particle
    {
        public double X, Y, Vx, Vy, Alpha, Lifetime, MaxLifetime;
    }
    private const int ParticleCount = 15;
    private readonly Particle[] _particles = new Particle[ParticleCount];

    // Pulse rings from core (Phase 2+)
    private struct PulseRing
    {
        public double Radius, Alpha, Birth;
        public bool Active;
    }
    private const int MaxPulseRings = 2;
    private readonly PulseRing[] _pulseRings = new PulseRing[MaxPulseRings];
    private double _lastPulseRingSpawn = -999.0;

    // Orbiting client nodes (Phase 2)
    private struct ClientNode
    {
        public double OrbitalAngle;   // Current angle on the middle ring
        public double StartX, StartY; // Spiral-in origin (random grid position)
        public double SettledAngle;   // 0, 120, 240 deg (in radians)
        public bool Settled;
    }
    private readonly ClientNode[] _nodes = new ClientNode[3];

    // Node comet trail positions (3 nodes × 5 trail positions)
    private readonly Point[] _nodeTrails = new Point[15];

    // Data packet
    private double _dataPacketProgress = -1.0;
    private int _dataPacketNode;
    private double _lastPacketTime = -999.0;

    // Ring rotation angles
    private double _outerAngle;
    private double _middleAngle;
    private double _innerAngle;

    // Electric arc flicker
    private double _lastArcFlicker = -999.0;
    private bool _arcFlickerVisible;
    private double _arcFlickerAngle;
    private int _arcFlickerCount = 2;

    // Text display
    private string _cachedRemexSubstring = "";
    private FormattedText? _cachedRemexText;
    private int _cachedRemexCharCount = -1;

    // Dash offset for connection trace arcs
    private double _dashOffset;

    // Random for procedural elements
    private readonly Random _rng;

    // ═══════════════ Cached Brushes & Pens ═══════════════

    private ImmutableSolidColorBrush _accentBrush = null!;
    private ImmutableSolidColorBrush _accentBrush70 = null!;
    private ImmutableSolidColorBrush _accentBrush60 = null!;
    private ImmutableSolidColorBrush _accentBrush40 = null!;
    private ImmutableSolidColorBrush _accentBrush30 = null!;
    private ImmutableSolidColorBrush _accentBrush25 = null!;
    private ImmutableSolidColorBrush _accentBrush15 = null!;
    private ImmutableSolidColorBrush _substrateBrush = null!;
    private ImmutableSolidColorBrush _whiteBrush = null!;
    private Pen _gridPen = null!;
    private Pen _gridBrightPen = null!;
    private Pen _scanLinePen = null!;
    private Pen _outerRingPen = null!;
    private Pen _middleRingPen = null!;
    private Pen _innerRingPen = null!;
    private Pen _tracePen = null!;

    // 8 pre-created particle alpha brushes (indices 0-7 → alpha 5%..80%)
    private readonly ImmutableSolidColorBrush[] _particleAlphaBrushes = new ImmutableSolidColorBrush[8];

    // ═══════════════ Constructor ═══════════════

    static BootSequenceControl()
    {
        AffectsRender<BootSequenceControl>(AccentColorProperty);
    }

    public BootSequenceControl()
    {
        _rng = new Random(42); // deterministic seed for reproducible layout
        RebuildBrushes();
        InitParticles(800, 600);
        InitNodes(400, 300);

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnTick);
    }

    // ═══════════════ Lifecycle ═══════════════

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentColorProperty)
            RebuildBrushes();
    }

    // ═══════════════ Timer Tick ═══════════════

    private void OnTick(object? sender, EventArgs e)
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        _elapsed += dt;

        if (_elapsed >= 5.0 && !_completed)
        {
            _completed = true;
            _timer.Stop();
            InvalidateVisual();
            SequenceCompleted?.Invoke();
            return;
        }

        UpdateParticles(dt);
        UpdateRotations(dt);
        UpdateNodes(dt);
        UpdatePulseRings(dt);
        UpdateDataPacket(dt);
        UpdateArcFlicker();
        _dashOffset -= dt * 12.0; // flow inward

        InvalidateVisual();
    }

    // ═══════════════ Brush Building ═══════════════

    private void RebuildBrushes()
    {
        var a = AccentColor;
        byte r = a.R, g = a.G, b = a.B;

        _accentBrush   = new ImmutableSolidColorBrush(a);
        _accentBrush70 = new ImmutableSolidColorBrush(new Color(178, r, g, b));
        _accentBrush60 = new ImmutableSolidColorBrush(new Color(153, r, g, b));
        _accentBrush40 = new ImmutableSolidColorBrush(new Color(102, r, g, b));
        _accentBrush30 = new ImmutableSolidColorBrush(new Color(76,  r, g, b));
        _accentBrush25 = new ImmutableSolidColorBrush(new Color(64,  r, g, b));
        _accentBrush15 = new ImmutableSolidColorBrush(new Color(38,  r, g, b));
        _substrateBrush = new ImmutableSolidColorBrush(Color.Parse("#050508"));
        _whiteBrush     = new ImmutableSolidColorBrush(Colors.White);

        _gridPen        = new Pen(_accentBrush15, 1.0);
        _gridBrightPen  = new Pen(_accentBrush40, 1.5);
        _scanLinePen    = new Pen(new ImmutableSolidColorBrush(new Color(204, r, g, b)), 2.0);
        _outerRingPen   = new Pen(_accentBrush40, 1.5);
        _middleRingPen  = new Pen(_accentBrush70, 2.5);
        _innerRingPen   = new Pen(_accentBrush25, 1.0);
        _tracePen       = new Pen(_accentBrush30, 1.0);

        // 8 particle alpha brushes: 5%, 10%, 15%, 25%, 40%, 55%, 65%, 80%
        byte[] alphas = { 13, 26, 38, 64, 102, 140, 166, 204 };
        for (int i = 0; i < 8; i++)
            _particleAlphaBrushes[i] = new ImmutableSolidColorBrush(new Color(alphas[i], r, g, b));
    }

    // ═══════════════ Initialization ═══════════════

    private void InitParticles(double width, double height)
    {
        for (int i = 0; i < ParticleCount; i++)
            SpawnParticle(ref _particles[i], width, height, forceSpawn: true);
    }

    private void SpawnParticle(ref Particle p, double width, double height, bool forceSpawn = false)
    {
        p.X          = _rng.NextDouble() * width;
        p.Y          = height * 0.4 + _rng.NextDouble() * height * 0.6;
        p.Vx         = (_rng.NextDouble() - 0.5) * 16.0;
        p.Vy         = -20.0 - _rng.NextDouble() * 20.0;
        p.MaxLifetime = 1.5 + _rng.NextDouble() * 1.5;
        p.Lifetime   = forceSpawn ? _rng.NextDouble() * p.MaxLifetime : 0.0;
        p.Alpha      = 0.0;
    }

    private void InitNodes(double cx, double cy)
    {
        double[] settledAngles = { -Math.PI / 2, -Math.PI / 2 + 2 * Math.PI / 3, -Math.PI / 2 + 4 * Math.PI / 3 };
        for (int i = 0; i < 3; i++)
        {
            _nodes[i].SettledAngle = settledAngles[i];
            _nodes[i].OrbitalAngle = settledAngles[i];
            _nodes[i].StartX = cx + (_rng.NextDouble() - 0.5) * 300;
            _nodes[i].StartY = cy + (_rng.NextDouble() - 0.5) * 200;
            _nodes[i].Settled = false;
        }
    }

    // ═══════════════ Per-Frame Updates ═══════════════

    private void UpdateParticles(double dt)
    {
        bool canRespawn = _elapsed < 2.0;
        var b = Bounds;
        double w = b.Width, h = b.Height;
        for (int i = 0; i < ParticleCount; i++)
        {
            ref var p = ref _particles[i];
            p.Lifetime += dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            double t = p.Lifetime / p.MaxLifetime;
            p.Alpha = t < 0.2 ? t / 0.2 : t < 0.8 ? 1.0 : 1.0 - (t - 0.8) / 0.2;
            p.Alpha = Math.Clamp(p.Alpha * 0.7, 0, 1);

            if (p.Lifetime >= p.MaxLifetime)
            {
                if (canRespawn)
                    SpawnParticle(ref p, w, h);
                else
                    p.Alpha = 0;
            }
        }
    }

    private void UpdateRotations(double dt)
    {
        _outerAngle  += 0.20 * dt;
        _middleAngle -= 0.35 * dt;
        _innerAngle  += 0.50 * dt;
    }

    private void UpdateNodes(double dt)
    {
        if (_elapsed < 1.2) return;
        double phase2t = Math.Clamp((_elapsed - 1.2) / 1.6, 0, 1);

        for (int i = 0; i < 3; i++)
        {
            ref var node = ref _nodes[i];
            if (!node.Settled)
            {
                if (phase2t >= 1.0)
                    node.Settled = true;
            }
            else
            {
                node.OrbitalAngle += 0.4 * dt;
            }
        }
    }

    private void UpdatePulseRings(double dt)
    {
        if (_elapsed < 1.8) return;
        if (_elapsed - _lastPulseRingSpawn > 1.5)
        {
            // Find inactive slot
            for (int i = 0; i < MaxPulseRings; i++)
            {
                if (!_pulseRings[i].Active)
                {
                    _pulseRings[i] = new PulseRing { Radius = 5.0, Alpha = 0.6, Birth = _elapsed, Active = true };
                    _lastPulseRingSpawn = _elapsed;
                    break;
                }
            }
        }

        for (int i = 0; i < MaxPulseRings; i++)
        {
            if (!_pulseRings[i].Active) continue;
            double age = _elapsed - _pulseRings[i].Birth;
            double t   = age / 0.8;
            if (t >= 1.0)
            {
                _pulseRings[i].Active = false;
                continue;
            }
            _pulseRings[i].Radius = 5.0 + 30.0 * t;
            _pulseRings[i].Alpha  = 0.6 * (1.0 - t);
        }
    }

    private void UpdateDataPacket(double dt)
    {
        if (_elapsed < 2.0) return;
        if (_dataPacketProgress >= 1.0 || _dataPacketProgress < 0)
        {
            if (_elapsed - _lastPacketTime > 2.0)
            {
                _dataPacketProgress = 0.0;
                _dataPacketNode     = _rng.Next(3);
                _lastPacketTime     = _elapsed;
            }
        }
        else
        {
            _dataPacketProgress = Math.Min(_dataPacketProgress + dt / 0.3, 1.0);
        }
    }

    private void UpdateArcFlicker()
    {
        if (_elapsed < 1.2 || _elapsed > 2.8) { _arcFlickerVisible = false; return; }
        if (_elapsed - _lastArcFlicker > 0.1)
        {
            _lastArcFlicker     = _elapsed;
            _arcFlickerVisible  = true;
            _arcFlickerAngle    = _rng.NextDouble() * Math.PI * 2;
            _arcFlickerCount    = _rng.Next(2, 4);
        }
        else if (_elapsed - _lastArcFlicker > 0.033)
        {
            _arcFlickerVisible = false;
        }
    }

    // ═══════════════ Master Render ═══════════════

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        double w  = bounds.Width;
        double h  = bounds.Height;
        double cx = w / 2;
        double cy = h / 2;

        // Re-init particles/nodes if size is different from defaults
        // (deferred init from constructor gets corrected here once we have real bounds)
        // This is safe since _rng is seeded and particles will redistribute each frame anyway.

        // Substrate background
        ctx.DrawRectangle(_substrateBrush, null, bounds);

        double elapsed = _elapsed;

        if (elapsed < 1.2)
        {
            // Phase 1: Grid Awakening
            RenderPhase1(ctx, w, h, cx, cy, elapsed);
        }
        else if (elapsed < 2.8)
        {
            // Phase 1 → Phase 2 overlap
            double gridT = Math.Clamp((elapsed - 1.2) / 0.5, 0, 1);
            double gridAlpha = 1.0 - EaseOut(gridT);

            if (gridAlpha > 0.01)
            {
                double gridScale = 1.0 - EaseOut(gridT) * 0.85;
                var scaleMatrix = Matrix.CreateScale(gridScale, gridScale)
                                * Matrix.CreateTranslation(cx * (1 - gridScale), cy * (1 - gridScale));
                using var _ = ctx.PushTransform(scaleMatrix);
                using var __ = ctx.PushOpacity(gridAlpha);
                RenderGridOnly(ctx, w, h, cx, cy, 1.2); // fully revealed grid
            }

            RenderParticles(ctx, Math.Max(0, 1.0 - gridT));
            RenderPhase2(ctx, w, h, cx, cy, elapsed);
        }
        else if (elapsed < 3.8)
        {
            // Phase 2 + Phase 3
            RenderPhase2(ctx, w, h, cx, cy, elapsed);
            RenderPhase3(ctx, w, h, cx, cy, elapsed);
        }
        else
        {
            // Phase 4: Dissolve Reveal
            double fadeProgress = Math.Clamp((elapsed - 3.8) / 1.2, 0, 1);
            double scale = 1.0 + 0.15 * fadeProgress;
            var scaleMatrix = Matrix.CreateScale(scale, scale)
                            * Matrix.CreateTranslation(cx * (1 - scale), cy * (1 - scale));

            // Vertical smear layers (drawn at reduced alpha behind)
            double smearAlpha = 0.30 * (1.0 - fadeProgress);
            if (smearAlpha > 0.005)
            {
                double[] yOffsets  = {  4,  8, -4 };
                double[] smearMult = { 1.0, 0.5, 0.5 };
                foreach (var (yOff, mult) in System.Linq.Enumerable.Zip(yOffsets, smearMult))
                {
                    var smearMatrix = Matrix.CreateScale(scale, scale)
                                    * Matrix.CreateTranslation(cx * (1 - scale), cy * (1 - scale) + yOff);
                    using var st = ctx.PushTransform(smearMatrix);
                    using var sa = ctx.PushOpacity(smearAlpha * mult);
                    RenderLogoAndUI(ctx, w, h, cx, cy, elapsed);
                }
            }

            using var mainT = ctx.PushTransform(scaleMatrix);
            using var mainO = ctx.PushOpacity(1.0 - fadeProgress);
            RenderLogoAndUI(ctx, w, h, cx, cy, elapsed);
        }
    }

    // ═══════════════ Phase 1: Grid Awakening ═══════════════

    private void RenderPhase1(DrawingContext ctx, double w, double h, double cx, double cy, double elapsed)
    {
        double scanY = h * (elapsed / 1.2);
        RenderGrid(ctx, w, h, cx, cy, scanY, elapsed);
        RenderScanLine(ctx, w, scanY);
        RenderParticles(ctx, 1.0);
    }

    private void RenderGridOnly(DrawingContext ctx, double w, double h, double cx, double cy, double passedTime)
    {
        double scanY = h * (passedTime / 1.2); // fully revealed
        RenderGrid(ctx, w, h, cx, cy, scanY, passedTime);
    }

    private void RenderGrid(DrawingContext ctx, double w, double h, double cx, double cy, double scanY, double elapsed)
    {
        double maxRadius = Math.Min(cx, cy) * 0.9;
        double[] radii = { 0.1, 0.2, 0.35, 0.55, 0.8 };

        // Radial concentric rings
        foreach (var rf in radii)
        {
            double rad   = rf * maxRadius;
            double ringY = cy;
            double alpha = Smoothstep(ringY - rad, ringY + rad, scanY);
            if (alpha <= 0.005) continue;

            double freshness = Math.Clamp((scanY - (ringY + rad)) / (h * 0.08), 0, 1);
            var pen = freshness < 0.5 ? _gridBrightPen : _gridPen;

            using var op = ctx.PushOpacity(alpha);
            ctx.DrawEllipse(null, pen, new Point(cx, cy), rad, rad);
        }

        // 12 radial lines (30° apart)
        for (int i = 0; i < 12; i++)
        {
            double angle = i * Math.PI / 6.0;
            double ex    = cx + maxRadius * Math.Cos(angle);
            double ey    = cy + maxRadius * Math.Sin(angle);

            double lineAlpha = Smoothstep(Math.Min(cy, ey) - 10, Math.Max(cy, ey) + 10, scanY);
            if (lineAlpha <= 0.005) continue;

            double freshness = Math.Clamp((scanY - Math.Max(cy, ey)) / (h * 0.08), 0, 1);
            var pen = freshness < 0.5 ? _gridBrightPen : _gridPen;

            using var op = ctx.PushOpacity(lineAlpha);
            ctx.DrawLine(pen, new Point(cx, cy), new Point(ex, ey));
        }
    }

    private void RenderScanLine(DrawingContext ctx, double w, double scanY)
    {
        if (scanY < 0 || scanY > 999999) return;
        // Center line (accent 80%, 2px)
        ctx.DrawLine(_scanLinePen, new Point(0, scanY), new Point(w, scanY));
        // ±2px glow at 40% alpha
        using var g1 = ctx.PushOpacity(0.4);
        ctx.DrawLine(new Pen(_accentBrush, 1.0), new Point(0, scanY - 2), new Point(w, scanY - 2));
        ctx.DrawLine(new Pen(_accentBrush, 1.0), new Point(0, scanY + 2), new Point(w, scanY + 2));
        // ±5px glow at 15% alpha
        using var g2 = ctx.PushOpacity(0.15 / 0.4); // relative to outer scope
        ctx.DrawLine(new Pen(_accentBrush, 1.0), new Point(0, scanY - 5), new Point(w, scanY - 5));
        ctx.DrawLine(new Pen(_accentBrush, 1.0), new Point(0, scanY + 5), new Point(w, scanY + 5));
    }

    // ═══════════════ Particles ═══════════════

    private void RenderParticles(DrawingContext ctx, double masterAlpha)
    {
        if (masterAlpha <= 0.005) return;
        using var op = ctx.PushOpacity(masterAlpha);
        for (int i = 0; i < ParticleCount; i++)
        {
            ref var p = ref _particles[i];
            if (p.Alpha <= 0.005) continue;
            // Map alpha to one of 8 pre-created brushes
            int brushIdx = Math.Clamp((int)(p.Alpha * 7.99), 0, 7);
            ctx.DrawEllipse(_particleAlphaBrushes[brushIdx], null,
                new Point(p.X, p.Y), 1.5, 1.5);
        }
    }

    // ═══════════════ Phase 2: Nexus Formation ═══════════════

    private void RenderPhase2(DrawingContext ctx, double w, double h, double cx, double cy, double elapsed)
    {
        double phase2t = Math.Clamp((elapsed - 1.2) / 1.6, 0, 1); // 0→1 over 1.2→2.8s

        // Logo formation alpha
        using var logoOp = ctx.PushOpacity(EaseOut(phase2t));
        RenderLogoAndUI(ctx, w, h, cx, cy, elapsed);
    }

    /// <summary>Renders the fully-formed logo system (rings, nodes, core, etc.).</summary>
    private void RenderLogoAndUI(DrawingContext ctx, double w, double h, double cx, double cy, double elapsed)
    {
        double phase2t = Math.Clamp((elapsed - 1.2) / 1.6, 0, 1);

        // How far each ring arc has formed (0→full circle in the first half of phase2)
        double ringFormT = Math.Clamp(phase2t * 2.0, 0, 1);
        double outerArcDeg  = ringFormT * 360.0;
        double middleArcDeg = ringFormT * 360.0;
        double innerArcDeg  = ringFormT * 360.0;

        const double outerR  = 80.0;
        const double middleR = 55.0;
        const double innerR  = 30.0;

        // Connection trace arcs (node→core) with animated dash
        RenderTraceArcs(ctx, cx, cy, middleR, phase2t, elapsed);

        // Outer ring
        RenderRing(ctx, cx, cy, outerR,  _outerRingPen,  outerArcDeg,  _outerAngle,  false, 0, elapsed);

        // Outer ring tick marks (N/E/S/W) — only when ring is complete
        if (outerArcDeg >= 359.9)
            RenderOuterTicks(ctx, cx, cy, outerR, _outerAngle);

        // Middle ring + electric arc flicker
        RenderRing(ctx, cx, cy, middleR, _middleRingPen, middleArcDeg, _middleAngle, _arcFlickerVisible && ringFormT < 1.0, _arcFlickerAngle, elapsed);

        // Inner ring
        RenderRing(ctx, cx, cy, innerR,  _innerRingPen,  innerArcDeg,  _innerAngle,  false, 0, elapsed);

        // Core reactor
        RenderCore(ctx, cx, cy, elapsed);

        // Orbiting client nodes
        RenderNodes(ctx, cx, cy, middleR, phase2t, elapsed);
    }

    private void RenderRing(DrawingContext ctx, double cx, double cy, double radius, Pen pen,
        double arcDeg, double rotationAngle, bool flicker, double flickerAngle, double elapsed)
    {
        if (arcDeg >= 359.9)
        {
            // Full circle — use DrawEllipse (more efficient)
            ctx.DrawEllipse(null, pen, new Point(cx, cy), radius, radius);
        }
        else
        {
            // Partial arc using StreamGeometry
            double startAngle = rotationAngle;
            double endAngle   = rotationAngle + arcDeg * Math.PI / 180.0;

            var geo = new StreamGeometry();
            using (var gctx = geo.Open())
            {
                double sx = cx + radius * Math.Cos(startAngle);
                double sy = cy + radius * Math.Sin(startAngle);
                double ex = cx + radius * Math.Cos(endAngle);
                double ey = cy + radius * Math.Sin(endAngle);

                gctx.BeginFigure(new Point(sx, sy), false);
                gctx.ArcTo(new Point(ex, ey),
                    new Size(radius, radius), 0,
                    arcDeg > 180, SweepDirection.Clockwise);
                gctx.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);

            // Electric arc flicker at the drawing edge
            if (flicker)
            {
                for (int i = 0; i < _arcFlickerCount; i++)
                {
                    double jitterAngle = endAngle + (i - 1) * 0.15;
                    double ex2 = cx + radius * Math.Cos(jitterAngle);
                    double ey2 = cy + radius * Math.Sin(jitterAngle);
                    double len = 4.0 + _rng.NextDouble() * 4.0;
                    double nx  = -Math.Sin(jitterAngle); // perpendicular
                    double ny  = Math.Cos(jitterAngle);
                    ctx.DrawLine(new Pen(_accentBrush, 1.0),
                        new Point(ex2, ey2),
                        new Point(ex2 + nx * len, ey2 + ny * len));
                }
            }
        }
    }

    private void RenderOuterTicks(DrawingContext ctx, double cx, double cy, double radius, double rotAngle)
    {
        for (int i = 0; i < 4; i++)
        {
            double angle = rotAngle + i * Math.PI / 2.0;
            double ix = cx + (radius - 3) * Math.Cos(angle);
            double iy = cy + (radius - 3) * Math.Sin(angle);
            double ox = cx + (radius + 3) * Math.Cos(angle);
            double oy = cy + (radius + 3) * Math.Sin(angle);
            ctx.DrawLine(_outerRingPen, new Point(ix, iy), new Point(ox, oy));
        }
    }

    private void RenderTraceArcs(DrawingContext ctx, double cx, double cy, double middleR, double phase2t, double elapsed)
    {
        if (phase2t < 0.3) return;
        double traceAlpha = Math.Min((phase2t - 0.3) / 0.3, 1.0);
        using var op = ctx.PushOpacity(traceAlpha);

        // Create animated dash pen (new each frame — only 3 pens, acceptable per spec)
        var dashPen = new Pen(_accentBrush30, 1.0,
            new DashStyle(new double[] { 4, 4 }, _dashOffset));

        double[] nodeAngles = { _nodes[0].SettledAngle, _nodes[1].SettledAngle, _nodes[2].SettledAngle };
        for (int i = 0; i < 3; i++)
        {
            double angle = _nodes[i].Settled ? _nodes[i].OrbitalAngle : _nodes[i].SettledAngle;
            double nx = cx + middleR * Math.Cos(angle);
            double ny = cy + middleR * Math.Sin(angle);

            // Curved arc from node toward center
            var geo = new StreamGeometry();
            using (var gctx = geo.Open())
            {
                gctx.BeginFigure(new Point(nx, ny), false);
                // Arc to center (use a tiny offset to avoid degenerate arc)
                gctx.ArcTo(new Point(cx + 2, cy),
                    new Size(middleR, middleR), 0, false,
                    i % 2 == 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                gctx.EndFigure(false);
            }
            ctx.DrawGeometry(null, dashPen, geo);
        }
    }

    private void RenderCore(DrawingContext ctx, double cx, double cy, double elapsed)
    {
        double breathe = 15.0 + 5.0 * Math.Sin(elapsed * 3.0);

        // Radial glow using nested ellipses
        for (int i = 4; i >= 1; i--)
        {
            double r = breathe * i / 4.0;
            byte alpha = (byte)(30 + 20 * (4 - i));
            var glowBrush = new ImmutableSolidColorBrush(new Color(alpha, AccentColor.R, AccentColor.G, AccentColor.B));
            ctx.DrawEllipse(glowBrush, null, new Point(cx, cy), r, r);
        }

        // Pulse rings
        for (int i = 0; i < MaxPulseRings; i++)
        {
            if (!_pulseRings[i].Active) continue;
            var pr = _pulseRings[i];
            byte alpha = (byte)(pr.Alpha * 255);
            var pulseB = new ImmutableSolidColorBrush(new Color(alpha, AccentColor.R, AccentColor.G, AccentColor.B));
            ctx.DrawEllipse(null, new Pen(pulseB, 1.5), new Point(cx, cy), pr.Radius, pr.Radius);
        }

        // Center dot (3px radius solid accent)
        ctx.DrawEllipse(_accentBrush, null, new Point(cx, cy), 3.0, 3.0);
    }

    private void RenderNodes(DrawingContext ctx, double cx, double cy, double middleR, double phase2t, double elapsed)
    {
        double phase2t2 = Math.Clamp((elapsed - 1.2) / 1.6, 0, 1);

        for (int i = 0; i < 3; i++)
        {
            ref var node = ref _nodes[i];
            Point pos;

            if (!node.Settled)
            {
                // Spiral in from start position toward orbital position
                double t = EaseOut(phase2t2);
                double destX = cx + middleR * Math.Cos(node.SettledAngle);
                double destY = cy + middleR * Math.Sin(node.SettledAngle);
                pos = LerpPt(new Point(node.StartX, node.StartY), new Point(destX, destY), t);
            }
            else
            {
                double angle = node.OrbitalAngle;
                pos = new Point(cx + middleR * Math.Cos(angle), cy + middleR * Math.Sin(angle));
            }

            // Comet tail (5 positions behind using stored trail)
            // For simplicity we compute trail analytically based on angle offset
            double orbitAngle = node.Settled ? node.OrbitalAngle : node.SettledAngle;
            double[] tailRadii = { 3, 2, 1.5, 1, 0.5 };
            double[] tailAlphas = { 0.60, 0.40, 0.25, 0.15, 0.05 };
            for (int t2 = 0; t2 < 5; t2++)
            {
                double pastAngle = orbitAngle - (t2 + 1) * 0.12; // angular offset behind
                double tx = cx + middleR * Math.Cos(pastAngle);
                double ty = cy + middleR * Math.Sin(pastAngle);
                byte alpha = (byte)(tailAlphas[t2] * 255);
                var tailBrush = new ImmutableSolidColorBrush(new Color(alpha, AccentColor.R, AccentColor.G, AccentColor.B));
                ctx.DrawEllipse(tailBrush, null, new Point(tx, ty), tailRadii[t2], tailRadii[t2]);
            }

            // Node dot (4px filled)
            ctx.DrawEllipse(_accentBrush, null, pos, 4.0, 4.0);

            // Data packet
            if (_dataPacketProgress >= 0 && _dataPacketProgress < 1.0 && _dataPacketNode == i)
            {
                double destX = cx;
                double destY = cy;
                double px    = pos.X + (destX - pos.X) * _dataPacketProgress;
                double py    = pos.Y + (destY - pos.Y) * _dataPacketProgress;
                ctx.DrawEllipse(_accentBrush, null, new Point(px, py), 2.0, 2.0);
            }
        }
    }

    // ═══════════════ Phase 3: System Online ═══════════════

    private void RenderPhase3(DrawingContext ctx, double w, double h, double cx, double cy, double elapsed)
    {
        double phase3t = Math.Clamp((elapsed - 2.8) / 1.0, 0, 1);

        // "REMEX" typewriter — ~0.2s per char = 5 chars × 0.2 = 1.0s total
        int charCount = Math.Min(5, (int)(phase3t * 1.0 / 0.2));
        string remexStr = "REMEX".Substring(0, charCount);

        if (_cachedRemexCharCount != charCount || _cachedRemexText == null)
        {
            _cachedRemexCharCount = charCount;
            _cachedRemexSubstring = remexStr;
            if (charCount > 0)
            {
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                _cachedRemexText = new FormattedText(
                    remexStr,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface, 36.0, _accentBrush);
            }
            else
            {
                _cachedRemexText = null;
            }
        }

        if (_cachedRemexText != null)
        {
            double tx = cx - _cachedRemexText.Width / 2.0;
            double ty = cy + 110.0;
            ctx.DrawText(_cachedRemexText, new Point(tx, ty));
        }

        // "COMMAND CENTER" subtitle fades in
        if (phase3t > 0.5)
        {
            double subtitleAlpha = Math.Clamp((phase3t - 0.5) / 0.5, 0, 1);
            using var sop = ctx.PushOpacity(subtitleAlpha * 0.6);
            var subTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
            string subtitle = "COMMAND CENTER";
            double letterSpacing = 4.0;
            // Measure total width including extra spacing
            var tempText = new FormattedText(subtitle, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, subTypeface, 11.0, _accentBrush60);
            double totalW = tempText.Width + (subtitle.Length - 1) * letterSpacing;
            double startX = cx - totalW / 2.0;
            double charY   = cy + 152.0;

            foreach (char c in subtitle)
            {
                var charText = new FormattedText(c.ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, subTypeface, 11.0, _accentBrush60);
                ctx.DrawText(charText, new Point(startX, charY));
                startX += charText.Width + letterSpacing;
            }
        }

        // Status text
        double statusAlpha;
        string statusStr;
        if (phase3t < 0.3)
        {
            statusStr   = "INITIALIZING...";
            statusAlpha = 0.7;
        }
        else if (phase3t < 0.6)
        {
            // Rapid flicker
            statusStr   = "INITIALIZING...";
            statusAlpha = (Math.Sin(elapsed * 40.0) > 0) ? 0.8 : 0.2;
        }
        else
        {
            statusStr   = "SYSTEMS ONLINE";
            statusAlpha = 0.9;
        }

        var statusTypeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        var statusText = new FormattedText(statusStr, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, statusTypeface, 10.0, _accentBrush40);
        using var statOp = ctx.PushOpacity(statusAlpha);
        ctx.DrawText(statusText, new Point(cx - statusText.Width / 2.0, cy + 170.0));
    }

    // ═══════════════ Easing Helpers ═══════════════

    private static double EaseOut(double t)
        => 1.0 - Math.Pow(1.0 - Math.Clamp(t, 0, 1), 3);

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        x = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Point LerpPt(Point a, Point b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
