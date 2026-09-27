using SkiaSharp;
using static Remex.Branding.LiveHandshakeDirector;

namespace Remex.Branding;

/// <summary>
/// "Live Handshake", the RemEx 3.0 default splash on the PC (RemEx-8g6n0). Not a fixed film: the mark
/// ignites, a shockwave lattice pulses, the phones this PC knows ring the mark as ghosts and the ones
/// actually linked ignite and lock on, the host's listener locks a ring onto the mark, and the splash
/// opens into the shell through a portal as soon as the director says the app is ready.
/// </summary>
/// <remarks>
/// <para>
/// PORTED FROM THE LAB. Every beat, timing and easing is <c>docs/specs/assets/live-handshake-lab.html</c>'s
/// <c>pc</c> scenario (<c>drawVectors</c>, <c>drawField</c>, <c>paintMark</c>, <c>pcGlyph</c>,
/// <c>statusText</c>, <c>portalRadius</c>). Lab sizes are in a 1280×800 reference frame; here they are
/// multiplied by a layout unit fitted to the actual window the same way the lab fits its device.
/// </para>
/// <para>
/// DETERMINISTIC GIVEN (t, snapshot). The hand-off is computed exactly from the snapshot's event times
/// (<see cref="LiveHandshakeDirector.Handoff"/>) and then latched, so a frame can be rendered at any t
/// without stepping — which is what <see cref="BrandRasterizer.RenderSplashFramePng"/> relies on. The
/// one piece of history is the pointer parallax, which is zero when no pointer was ever reported.
/// </para>
/// <para>
/// PC DIFFERENCES FROM ANDROID: there is no round-trip time, so no range rings and a linked phone
/// settles to a fixed inner radius with a "linked" suffix instead of milliseconds; the target is the
/// first phone that linked, its answer and its lock-on are the same moment, and the portal always
/// opens from the mark. The portal is a clean transparent hole with a rim — the live shell under the
/// splash shows through it; nothing is refracted.
/// </para>
/// </remarks>
public sealed class LiveHandshakeVariant : ILiveSplashVariant, IDisposable
{
    // ── Layout (PC reference frame 1280×800) ──────────────────────────────────
    private const float RefW = 1280f, RefH = 800f;
    private const float CenterYFrac = 0.46f, MarkWidthDp = 150f, OrbitRxFrac = 0.27f, OrbitRyFrac = 0.30f;
    // The console (spec, "The console"): wordmark at 0.80H, three mono lines 34 dp below it.
    private const float WordmarkYFrac = 0.80f, ConsoleGapDp = 34f, ConsoleLineDp = 21f, ConsoleColumnDp = 420f;
    private const int MaxPeers = 8;
    /// <summary>Where a linked phone settles (the orbit is 1). No RTT on the PC, so one fixed radius.</summary>
    private const float LinkedRadius = 0.72f;
    private const float WindowLen = 226f, Tau = MathF.PI * 2f;

    private static readonly SKColor DefaultPrimary = new(0xFF93A4C6);
    private static readonly SKColor DarkInk = new(0xFFF1F3F8), DarkMuted = new(0xFF8C95A8);
    private static readonly SKColor LightInk = new(0xFF1A1D24), LightMuted = new(0xFF5A6173);

    private static readonly CubicBezier EmphDec = new(0.05f, 0.7f, 0.1f, 1f);
    private static readonly CubicBezier Std = new(0.2f, 0f, 0f, 1f);
    private static readonly CubicBezier PortalEase = new(0.45f, 0f, 0.15f, 1f);

    private readonly ILiveHandshakeText _text;
    private readonly object _gate = new();

    private volatile PeerLayout _layout;
    private readonly Frame _frame = new();
    private volatile bool _reduced;
    private bool _fieldEnabled = true;
    private float _ptrX, _ptrY, _parX, _parY;
    private float? _handoff;
    private float? _skipAt;
    private float _lastT;
    private float _chrome = 1f;

    // ── Cached Skia objects (no avoidable per-frame allocation) ───────────────
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _fieldPaint = new() { IsAntialias = false };
    private readonly SKPath _clip = new();
    private readonly Dictionary<int, SKMaskFilter> _blurs = new();
    // Uniforms owned here, NOT an SKRuntimeShaderBuilder: disposing a builder disposes the effect it
    // was built on, and the effect is the process-wide LiveHandshakeField cache (measured: the next
    // variant then crashed natively in sk_runtimeeffect_get_uniform_byte_size).
    private SKRuntimeEffectUniforms? _uniforms;
    private readonly float[] _u2 = new float[2], _u3 = new float[3], _u4 = new float[4];
    private readonly float[] _rings = new float[LiveHandshakeField.MaxRings * 4];
    private SKShader? _radialAcc; private SKColor _radialAccKey;
    private SKShader? _markGrad; private SKColor _markGradA, _markGradB;
    private SKShader? _backdrop; private SKColor _backdropA, _backdropB; private float _backdropW, _backdropH;
    private SKPathEffect? _ghostDash; private float _ghostDashU;
    // Text is shaped (fallback faces + HarfBuzz), not drawn with one SKFont: the hi locale and any
    // non-Latin nickname rendered as missing-glyph boxes otherwise (RemEx-8g6n0.2 review).
    private const float LabelDp = 14f, ConsoleDp = 13.5f, WordmarkDp = 22f;
    private readonly SplashTextShaper _shaper = new();
    private readonly Dictionary<(string, float, bool), ShapedLine> _lines = new();
    private SKTypeface? _fontFace; private float _fontU;
    private readonly Dictionary<HandshakeLine, string> _lineText = new();
    private readonly HandshakeLine[] _consoleLines = new HandshakeLine[4];

    public LiveHandshakeVariant(ILiveHandshakeText? text = null)
    {
        _text = text ?? EnglishHandshakeText.Instance;
        _layout = PeerLayout.Build(HandshakeSnapshot.Empty, null, _text);
    }

    /// <summary>Upper bound only: the host finishes on <see cref="IsComplete"/>, which is event-driven.</summary>
    public float Duration => Cap + Exit;

    public float ChromeOpacity => _chrome;

    public bool ReducedMotion { set => _reduced = value; }

    public bool FieldEnabled { set => _fieldEnabled = value; }

    public void SetPointer(float nx, float ny)
    {
        _ptrX = Math.Clamp(nx, -1f, 1f);
        _ptrY = Math.Clamp(ny, -1f, 1f);
    }

    public void Update(HandshakeSnapshot snapshot)
    {
        var current = _layout;
        if (ReferenceEquals(current.Snapshot, snapshot)) return;
        _layout = PeerLayout.Build(snapshot ?? HandshakeSnapshot.Empty, current, _text);
    }

    public void RequestSkip()
    {
        lock (_gate)
        {
            if (_handoff is null && _skipAt is null) _skipAt = _lastT;
        }
    }

    public bool IsComplete(float t)
    {
        // Belt and braces: the director never hands off later than CAP, so this is unreachable unless
        // something upstream is broken - and then the shell must still open.
        if (t >= Cap + Exit + 0.5f) return true;
        return ResolveHandoff(t) is { } h && t >= h + (_reduced ? FadeExit : Exit);
    }

    /// <summary>The hand-off time once it has happened by <paramref name="t"/>; latched from then on.</summary>
    public float? HandoffAt(float t) => ResolveHandoff(t);

    private float? ResolveHandoff(float t)
    {
        lock (_gate)
        {
            if (t > _lastT) _lastT = t;
            if (_handoff is { } latched) return latched;
            var h = LiveHandshakeDirector.Handoff(Timeline(_layout, _skipAt), t);
            if (h is not null) _handoff = h;
            return h;
        }
    }

    /// <summary>
    /// The director's view of a snapshot. On the PC a link is also that phone's answer, so every
    /// linked phone is an answer (the target flagged) and the staging spaces them out for reading.
    /// </summary>
    private static HandshakeTimeline Timeline(PeerLayout layout, float? skipAt)
    {
        var s = layout.Snapshot;
        return new HandshakeTimeline(
            Peers: s.Peers.Count,
            TargetAt: s.TargetAt,
            ReadyAt: s.ReadyAt,
            LinkedAt: s.TargetLinkedAt,
            FailedAt: s.FailedAt,
            SkipAt: skipAt,
            ExitFromMark: true,
            Answers: layout.Answers);
    }

    private float? SkipAtSnapshot()
    {
        lock (_gate) return _skipAt;
    }

    // ── Frame ──────────────────────────────────────────────────────────────────

    public void Render(SKCanvas canvas, float width, float height, float t, float dt)
    {
        if (width <= 0 || height <= 0) return;
        float? handoff = ResolveHandoff(t);
        var layout = _layout;
        var snap = layout.Snapshot;
        bool reduced = _reduced;

        float u = Math.Clamp(Math.Min(width / RefW, height / RefH), 0.5f, 1.6f);
        // One Frame reused for every render (render thread only), so a frame allocates nothing here.
        var f = _frame;
        f.T = t; f.W = width; f.H = height; f.U = u; f.Reduced = reduced;
        f.C = new SKPoint(width / 2f, height * CenterYFrac);
        f.MarkW = MarkWidthDp * u;
        f.Rx = width * OrbitRxFrac; f.Ry = height * OrbitRyFrac;
        f.Handoff = handoff;
        f.N = Math.Min(snap.Peers.Count, MaxPeers);
        f.Layout = layout;
        f.Fade = 1f;
        // Primary for the lattice: the seed palette's primary (MarkStart), or the lab's slate when
        // the brand default is active (its mark start is the near-black window fill).
        f.Pri = SplashBrand.MarkStart == SplashBrand.WindowFill ? DefaultPrimary : SplashBrand.MarkStart;
        f.Acc = SplashBrand.Amber;
        bool lightBackdrop = Luma(SplashBrand.BackdropStart) > 0.55f;
        f.Ink = lightBackdrop ? LightInk : DarkInk;
        f.Muted = lightBackdrop ? LightMuted : DarkMuted;
        f.E = f.C; // the PC always opens from the mark
        f.Exiting = handoff is { } hh && t >= hh;
        f.ExitT = handoff is { } h2 ? t - h2 : -1f;
        f.ExitE = f.Exiting ? Clamp01(f.ExitT / Exit) : 0f;
        f.PortalR = PortalRadius(f);

        // Every visual fires at its SHOWN time (the director's staging), never the raw event time:
        // a linked phone ignites no sooner than ANSWER_MIN, phones ANSWER_GAP apart, the lock
        // LOCK_AFTER later, and nothing staged past the hand-off is shown at all.
        var timeline = Timeline(layout, SkipAtSnapshot());
        float hEnd = handoff ?? float.PositiveInfinity;
        Span<float> staged = stackalloc float[LiveHandshakeDirector.MaxAnswers + 1];
        LiveHandshakeDirector.StageAnswers(timeline, t, staged, out _);
        for (int i = 0; i < f.N; i++) f.Vis[i] = float.NaN;
        for (int a = 0; a < layout.AnswerPeer.Length && a < staged.Length; a++)
        {
            int p = layout.AnswerPeer[a];
            if (p < f.N && !float.IsNaN(staged[a]) && staged[a] < hEnd) f.Vis[p] = staged[a];
        }
        int ti = snap.TargetIndex;
        float? lockShown = LiveHandshakeDirector.LockShown(timeline, t);
        f.LockAt = lockShown is { } ls && ls <= hEnd ? ls : float.NaN;
        f.Target = ti >= 0 && ti < f.N && !float.IsNaN(f.LockAt) ? ti : -1;
        // The listening beat: not before ListeningMin, the same rule as its console line.
        f.ListenVis = snap.ListeningAt is { } la ? Math.Max(la, LiveHandshakeDirector.ListeningMin) : float.NaN;

        f.Pulses = PulsesStarted(t, handoff, reduced);
        float pAge = f.Pulses > 0 ? t - PulseAt(f.Pulses - 1) : 9f;
        f.Env = pAge < 0.3f ? MathF.Sin(MathF.PI * pAge / 0.3f) : 0f;
        f.Intro = reduced ? 1f : EmphDec.Ease(Clamp01(t / 0.9f));

        UpdateParallax(t, dt, reduced, u);
        EnsureCaches(f);

        if (_fieldEnabled && LiveHandshakeField.Effect is { } effect) DrawField(canvas, effect, f);
        else DrawFallbackBackdrop(canvas, f);

        DrawVectors(canvas, f);
    }

    private static float Appear(int i) => 0.16f + 0.08f * i;


    private void UpdateParallax(float t, float dt, bool reduced, float u)
    {
        if (reduced) { _parX = _parY = 0f; return; }
        // Frame-rate independent version of the lab's lerp(par, target, 0.06) per 60 Hz frame.
        float k = 1f - MathF.Pow(0.94f, Math.Clamp(dt, 0f, 0.25f) * 60f);
        _parX += (_ptrX * 9f * u - _parX) * k;
        _parY += (_ptrY * 9f * u - _parY) * k;
    }

    private float ParX(Frame f) => f.Reduced ? 0f : _parX + MathF.Sin(f.T / 3.3f) * 2.5f * f.U;
    private float ParY(Frame f) => f.Reduced ? 0f : _parY + MathF.Cos(f.T / 4.1f) * 2.5f * f.U;

    private static float PortalRadius(Frame f)
    {
        float ex = f.E.X, ey = f.E.Y, w = f.W, h = f.H;
        float far = MathF.Max(MathF.Max(Hypot(ex, ey), Hypot(w - ex, ey)), MathF.Max(Hypot(ex, h - ey), Hypot(w - ex, h - ey)))
                    + 40f * f.U;
        return Lerp(10f * f.U, far, PortalEase.Ease(f.ExitE));
    }

    private SKPoint NodePos(Frame f, int i, float t)
    {
        float r = 1f;
        float v = f.Vis[i];
        if (!float.IsNaN(v) && t >= v) r = Lerp(1f, LinkedRadius, Spring(t - v));
        float a = f.Layout.Angles[i];
        return new SKPoint(f.C.X + MathF.Cos(a) * f.Rx * r, f.C.Y + MathF.Sin(a) * f.Ry * r);
    }

    private SKPoint SettledPos(Frame f, int i)
    {
        float a = f.Layout.Angles[i];
        return new SKPoint(f.C.X + MathF.Cos(a) * f.Rx * LinkedRadius, f.C.Y + MathF.Sin(a) * f.Ry * LinkedRadius);
    }

    // ── Field (GPU shader) ──────────────────────────────────────────────────

    private void DrawField(SKCanvas canvas, SKRuntimeEffect effect, Frame f)
    {
        // Draw in device pixels so the lattice's 1-px antialiasing is a real pixel at any scaling.
        float s = DeviceScale(canvas);
        float W = f.W * s, H = f.H * s;

        _uniforms ??= new SKRuntimeEffectUniforms(effect);
        var uni = _uniforms;

        _u2[0] = W; _u2[1] = H; uni["uRes"] = _u2;
        uni["uTime"] = f.T;
        uni["uPx"] = f.U * s;
        _u2[0] = f.C.X * s; _u2[1] = f.C.Y * s; uni["uCenter"] = _u2;
        uni["uBg0"] = Rgb(SplashBrand.BackdropStart);
        uni["uBg1"] = Rgb(SplashBrand.BackdropEnd);
        uni["uPri"] = Rgb(f.Pri);
        uni["uAcc"] = Rgb(f.Acc);

        int count = CollectRings(f, s);
        uni["uRings"] = _rings;
        uni["uRingCount"] = (float)count;
        uni["uSpeed"] = MathF.Sqrt(W * W + H * H) / 1.25f;
        uni["uIntro"] = f.Intro;
        bool portal = f.Exiting && !f.Reduced;
        _u4[0] = portal ? f.E.X * s : 0f; _u4[1] = portal ? f.E.Y * s : 0f;
        _u4[2] = portal ? f.PortalR * s : 0f; _u4[3] = portal ? 1f : 0f;
        uni["uPortal"] = _u4;
        uni["uZoom"] = portal ? Std.Ease(f.ExitE) : 0f;
        uni["uStill"] = f.Reduced ? 1f : 0f;
        _u2[0] = ParX(f) * s; _u2[1] = ParY(f) * s; uni["uPar"] = _u2;
        uni["uAlpha"] = FieldAlpha(f);

        using var shader = effect.ToShader(uni);
        _fieldPaint.Shader = shader;
        canvas.Save();
        canvas.Scale(1f / s);
        canvas.DrawRect(0, 0, W, H, _fieldPaint);
        canvas.Restore();
        _fieldPaint.Shader = null;
    }

    /// <summary>
    /// The field's overall alpha. Reduced motion: the FADE_EXIT crossfade. Portal: 1 until the last
    /// fifth of the exit, then out — by then the rim is past the corners, but its wide halo still tints
    /// them faintly, and the splash must leave nothing over the shell when it reports complete.
    /// </summary>
    private static float FieldAlpha(Frame f)
    {
        if (!f.Exiting) return 1f;
        return f.Reduced ? 1f - Clamp01(f.ExitT / FadeExit) : 1f - Smooth(0.8f, 1f, f.ExitE);
    }

    private float[] Rgb(SKColor c)
    {
        // A fresh 3-array per call would allocate; the uniform setter copies the values out, so one
        // scratch array serves every float3 in turn.
        _u3[0] = c.Red / 255f; _u3[1] = c.Green / 255f; _u3[2] = c.Blue / 255f;
        return _u3;
    }

    private static float DeviceScale(SKCanvas canvas)
    {
        var m = canvas.TotalMatrix;
        float s = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        return float.IsFinite(s) && s > 0.01f ? Math.Clamp(s, 0.25f, 4f) : 1f;
    }

    /// <summary>
    /// The shockwaves the lattice carries this frame: every pulse from the mark, each linked phone's
    /// own smaller ripple, the lock-on, the listener coming up and the hand-off. The ten most recent
    /// that have started, newest first. Positions in device pixels.
    /// </summary>
    private int CollectRings(Frame f, float s)
    {
        Array.Clear(_rings);
        if (f.Reduced) return 0;
        Span<Ring> all = stackalloc Ring[24];
        int n = 0;
        for (int k = 0; k < f.Pulses && n < all.Length; k++) all[n++] = new Ring(f.C, PulseAt(k), 1.0f);
        for (int i = 0; i < f.N && n < all.Length; i++)
        {
            if (float.IsNaN(f.Vis[i])) continue;
            all[n++] = new Ring(SettledPos(f, i), f.Vis[i], 0.5f);
        }
        // The lock-on sends its own, stronger ripple from the target at the SHOWN lock time.
        if (f.Target >= 0 && n < all.Length) all[n++] = new Ring(SettledPos(f, f.Target), f.LockAt, 0.85f);
        if (!float.IsNaN(f.ListenVis) && n < all.Length) all[n++] = new Ring(f.C, f.ListenVis, 0.6f);
        if (f.Handoff is { } h && n < all.Length) all[n++] = new Ring(f.E, h, 1.4f);

        // Newest first; insertion sort over at most two dozen entries.
        int live = 0;
        for (int i = 0; i < n; i++)
        {
            if (all[i].Start > f.T) continue;
            var r = all[i];
            int j = live++;
            while (j > 0 && all[j - 1].Start < r.Start) { all[j] = all[j - 1]; j--; }
            all[j] = r;
        }
        int count = Math.Min(live, LiveHandshakeField.MaxRings);
        for (int i = 0; i < count; i++)
        {
            _rings[i * 4] = all[i].Pos.X * s;
            _rings[i * 4 + 1] = all[i].Pos.Y * s;
            _rings[i * 4 + 2] = all[i].Start;
            _rings[i * 4 + 3] = all[i].Strength;
        }
        return count;
    }

    // ── Field fallback (raster lease: no shader) ─────────────────────────────

    private void DrawFallbackBackdrop(SKCanvas canvas, Frame f)
    {
        float alpha = FieldAlpha(f);
        if (alpha <= 0.001f) return;
        bool portal = f.Exiting && !f.Reduced;

        if (_backdrop is null || _backdropA != SplashBrand.BackdropStart || _backdropB != SplashBrand.BackdropEnd
            || _backdropW != f.W || _backdropH != f.H)
        {
            _backdrop?.Dispose();
            _backdropA = SplashBrand.BackdropStart; _backdropB = SplashBrand.BackdropEnd;
            _backdropW = f.W; _backdropH = f.H;
            _backdrop = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(f.W, f.H),
                new[] { _backdropA, _backdropB }, null, SKShaderTileMode.Clamp);
        }

        canvas.Save();
        if (portal)
        {
            _clip.Rewind();
            _clip.AddCircle(f.E.X, f.E.Y, f.PortalR);
            canvas.ClipPath(_clip, SKClipOperation.Difference, true);
        }
        _fill.Shader = _backdrop;
        _fill.Color = SKColors.White.WithAlpha(Byte(alpha));
        canvas.DrawRect(0, 0, f.W, f.H, _fill);
        _fill.Shader = null;
        // The centre glow the shader would paint, so the mark still sits in light.
        DrawRadial(canvas, f.C, MathF.Min(f.W, f.H) * 0.45f, 0.10f * alpha * f.Intro);
        canvas.Restore();

        if (portal)
        {
            _stroke.PathEffect = null;
            _stroke.StrokeCap = SKStrokeCap.Butt;
            _stroke.MaskFilter = Blur(14f * f.U);
            _stroke.StrokeWidth = 24f * f.U;
            _stroke.Color = A(f.Pri, 0.30f * alpha);
            canvas.DrawCircle(f.E, f.PortalR, _stroke);
            _stroke.MaskFilter = null;
            _stroke.StrokeWidth = 2.6f * f.U;
            _stroke.Color = A(f.Acc, 0.9f * alpha);
            canvas.DrawCircle(f.E, f.PortalR, _stroke);
        }
    }

    // ── Vectors ────────────────────────────────────────────────────────────────

    private void DrawVectors(SKCanvas canvas, Frame f)
    {
        float fade = 1f;
        canvas.Save();
        if (f.Exiting)
        {
            if (f.Reduced) fade = 1f - Clamp01(f.ExitT / FadeExit);
            else
            {
                float z = 1f + 0.5f * Std.Ease(f.ExitE);
                fade = 1f - Smooth(0f, 0.55f, f.ExitE);
                canvas.Translate(f.E.X, f.E.Y);
                canvas.Scale(z);
                canvas.Translate(-f.E.X, -f.E.Y);
                _clip.Rewind();
                _clip.AddCircle(f.E.X, f.E.Y, f.PortalR / z);
                canvas.ClipPath(_clip, SKClipOperation.Difference, true);
            }
        }
        _chrome = fade;
        if (fade <= 0.001f) { canvas.Restore(); return; }
        f.Fade = fade;

        if (f.Target >= 0 && f.T >= f.LockAt) DrawLock(canvas, f);
        for (int i = 0; i < f.N; i++) DrawNode(canvas, f, i);
        DrawMarkLayer(canvas, f);
        DrawListenRing(canvas, f);
        DrawWordmarkAndConsole(canvas, f);

        canvas.Restore();
    }

    /// <summary>Beam, packets and the reticle snapping shut on the target phone.</summary>
    private void DrawLock(SKCanvas canvas, Frame f)
    {
        int ti = f.Target;
        float t = f.T, u = f.U, lv = f.LockAt, la = t - lv;
        var node = NodePos(f, ti, f.Reduced ? 99f : t);
        float dx = node.X - f.C.X, dy = node.Y - f.C.Y, len = MathF.Max(Hypot(dx, dy), 1f);
        float ux = dx / len, uy = dy / len;
        float r0 = f.MarkW * 0.52f, r1 = 16f * u;
        var p0 = new SKPoint(f.C.X + ux * r0, f.C.Y + uy * r0);
        var p1 = new SKPoint(node.X - ux * r1, node.Y - uy * r1);

        float flare = f.Reduced ? 0f : MathF.Exp(-la * 4f);
        _stroke.PathEffect = null;
        _stroke.MaskFilter = null;
        _stroke.StrokeCap = SKStrokeCap.Round;
        _stroke.StrokeWidth = (7f + 10f * flare) * u;
        _stroke.Color = A(f.Acc, (0.10f + 0.25f * flare) * f.Fade);
        canvas.DrawLine(p0, p1, _stroke);
        _stroke.StrokeWidth = 1.6f * u;
        _stroke.Color = A(f.Acc, (0.65f + 0.35f * flare) * f.Fade);
        canvas.DrawLine(p0, p1, _stroke);

        if (!f.Reduced)
        {
            for (int k = 0; k < 6; k++)
            {
                bool outbound = k < 3;
                float st = lv + (outbound ? 0f : 0.2f) + (k % 3) * 0.07f;
                float q = (t - st) / 0.24f;
                if (q < 0f || q > 1f) continue;
                float e = Std.Ease(q);
                var pp = outbound ? Lerp(p0, p1, e) : Lerp(p1, p0, e);
                DrawGlowDot(canvas, pp, 2.4f * u, outbound ? SKColors.White : f.Acc, f.Acc, f.Fade, 5f * u);
            }
        }

        // Reticle: on the PC there is no hunting phase (no connect attempt to watch), so it arrives
        // at lock-on and snaps shut on the spring.
        float kk = f.Reduced ? 1f : Spring(la);
        float size = Lerp(28f, 19f, kk) * u;
        float rot = Lerp((lv * 1.1f) % (MathF.PI / 2f), 0f, kk);
        float ra = f.Reduced ? 1f : Smooth(0f, 0.08f, la);
        float L = 7f * u;
        _stroke.StrokeWidth = 1.8f * u;
        _stroke.Color = A(f.Acc, f.Fade * ra);
        canvas.Save();
        canvas.Translate(node.X, node.Y);
        canvas.RotateRadians(rot);
        for (int q = 0; q < 4; q++)
        {
            canvas.DrawLine(size, size - L, size, size, _stroke);
            canvas.DrawLine(size, size, size - L, size, _stroke);
            canvas.RotateDegrees(90f);
        }
        canvas.Restore();
    }

    private void DrawNode(SKCanvas canvas, Frame f, int i)
    {
        float t = f.T, u = f.U;
        float appear = Appear(i);
        if (t < appear && !f.Reduced) return;
        float ga = f.Reduced ? 1f : Smooth(appear, appear + 0.3f, t);
        float v = f.Vis[i];
        bool answered = !float.IsNaN(v) && t >= v;
        float la = answered ? t - v : 0f;
        float lit = answered ? (f.Reduced ? 1f : Smooth(0f, 0.12f, la)) : 0f;
        var p = NodePos(f, i, f.Reduced && answered ? 99f : t);

        if (answered && !f.Reduced)
        {
            float fl = MathF.Exp(-la * 5f);
            DrawRadial(canvas, p, (26f + 30f * fl) * u, (0.35f * lit + 0.4f * fl) * f.Fade);
            // The echo: the link travelling home to the mark.
            float q = la / 0.3f;
            if (q < 1f)
            {
                var ep = Lerp(p, f.C, Std.Ease(q));
                DrawGlowDot(canvas, ep, 2.6f * u, f.Acc, f.Acc, f.Fade * (1f - q * 0.6f), 6f * u);
            }
        }
        else if (answered)
        {
            DrawRadial(canvas, p, 26f * u, 0.3f * f.Fade);
        }

        var peer = f.Layout.Snapshot.Peers[i];
        DrawGlyph(canvas, f, p, peer.Kind, lit, (answered ? 1f : 0.42f) * ga);

        // Label on the outer side of the orbit; the suffix types in at 40 chars/s.
        float labelSize = LabelDp * u;
        bool above = p.Y < f.C.Y - 4f * u;
        float ly = p.Y + (above ? -20f : 28f) * u;
        string full = answered ? f.Layout.LinkedLabels[i] : peer.Name;
        int suffixLen = answered ? full.Length - peer.Name.Length : 0;
        int shown = answered && !f.Reduced ? Math.Min(suffixLen, (int)MathF.Floor(la * 40f)) : suffixLen;
        // Never cut a surrogate pair while the suffix types in.
        int cut = peer.Name.Length + shown;
        if (shown < suffixLen && cut > 0 && char.IsHighSurrogate(full[cut - 1])) cut--;
        string txt = shown == suffixLen ? full : full[..cut];
        var fullLine = Line(full, labelSize, mono: true);
        var line = ReferenceEquals(txt, full) ? fullLine : Line(txt, labelSize, mono: true);
        float m = 14f * u;
        float lo = m + fullLine.Width / 2f, hi = f.W - m - fullLine.Width / 2f;
        float lx = lo <= hi ? Math.Clamp(p.X, lo, hi) : f.W / 2f;
        _fill.Shader = null;
        _fill.MaskFilter = null;
        _fill.Color = A(answered ? f.Ink : f.Pri, f.Fade * ga * (answered ? 1f : 0.4f));
        line.Draw(canvas, lx - line.Width / 2f, ly, _fill);
        if (answered && shown < suffixLen && !f.Reduced)
        {
            _fill.Color = A(f.Acc, f.Fade * ga);
            canvas.DrawRect(lx + line.Width / 2f + 2f * u, ly - 9f * u, 5f * u, 11f * u, _fill);
        }
    }

    /// <summary>The lab's <c>pcGlyph</c>: a small rounded device with an accent dot, dashed while a ghost.</summary>
    private void DrawGlyph(SKCanvas canvas, Frame f, SKPoint p, HandshakeDeviceKind kind, float lit, float ghost)
    {
        float u = f.U;
        bool handheld = kind != HandshakeDeviceKind.Pc;
        float w = (handheld ? (kind == HandshakeDeviceKind.Tablet ? 15f : 11f) : 24f) * u;
        float h = (handheld ? (kind == HandshakeDeviceKind.Tablet ? 20f : 19f) : 17f) * u;
        float r = (handheld ? 3f : 4f) * u;
        var rect = new SKRect(p.X - w / 2f, p.Y - h / 2f, p.X + w / 2f, p.Y + h / 2f);

        if (lit > 0f)
        {
            using var grad = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                new[] { A(f.Pri, 0.55f), A(f.Acc, 0.35f) }, null, SKShaderTileMode.Clamp);
            _fill.Shader = grad;
            _fill.Color = SKColors.White.WithAlpha(Byte(lit * f.Fade));
            canvas.DrawRoundRect(rect, r, r, _fill);
            _fill.Shader = null;
        }

        _stroke.MaskFilter = null;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeWidth = 1.4f * u;
        _stroke.Color = A(lit > 0.5f ? f.Acc : f.Pri, ghost * f.Fade);
        _stroke.PathEffect = lit < 0.5f ? _ghostDash : null;
        canvas.DrawRoundRect(rect, r, r, _stroke);
        _stroke.PathEffect = null;

        _fill.Color = A(lit > 0.5f ? f.Acc : f.Pri, MathF.Max(lit, ghost * 0.6f) * f.Fade);
        float dx = rect.Left + (handheld ? w / 2f : 4.2f * u);
        float dy = rect.Top + (handheld ? 3.4f : 4f) * u;
        canvas.DrawCircle(dx, dy, 1.4f * u, _fill);
    }

    private void DrawMarkLayer(SKCanvas canvas, Frame f)
    {
        float t = f.T;
        DrawRadial(canvas, f.C, f.MarkW * (1.1f + 0.25f * f.Env), (0.10f + 0.22f * f.Env) * f.Fade);

        float s = f.MarkW / 68f * (1f + 0.035f * f.Env);
        canvas.Save();
        canvas.Translate(f.C.X, f.C.Y);
        canvas.Scale(s);
        canvas.Translate(-54f, -54f);

        bool blinkOn = ((int)MathF.Floor(t / 0.53f)) % 2 == 0;
        var o = new MarkState
        {
            FillK = 1f, TraceK = 1f, Dot1 = 1f, Dot2 = 1f, Dot3 = 1f, ChevK = 1f, RK = 1f,
            CursorA = f.Reduced ? 1f : (f.Env > 0f ? 1f : (blinkOn ? 1f : 0.18f)),
            CursorGlow = f.Env,
        };
        if (!f.Reduced)
        {
            // The PC ignition: the window outline traces itself on, the dots pop, the R springs in.
            o.TraceK = EmphDec.Ease(Clamp01(t / 0.42f));
            o.FillK = Smooth(0.2f, 0.5f, t);
            o.Dot1 = Smooth(0.28f, 0.36f, t);
            o.Dot2 = Smooth(0.34f, 0.42f, t);
            o.Dot3 = Smooth(0.40f, 0.48f, t);
            o.ChevK = Smooth(0.3f, 0.5f, t);
            o.RK = t > 0.38f ? Spring(t - 0.38f) : 0f;
            if (t <= 0.5f) o.CursorA = 0f;
        }
        PaintMark(canvas, f, o, s);
        canvas.Restore();
    }

    /// <summary>The lab's <c>paintMark</c>, in the brand's 108-unit space (RemexBrandData geometry).</summary>
    private void PaintMark(SKCanvas canvas, Frame f, in MarkState o, float markScale)
    {
        float a = f.Fade;
        _fill.MaskFilter = null;
        _stroke.MaskFilter = null;
        _stroke.PathEffect = null;

        // Window fill: the mark gradient.
        if (o.FillK > 0f)
        {
            _fill.Shader = _markGrad;
            _fill.Color = SKColors.White.WithAlpha(Byte(a * o.FillK));
            canvas.DrawPath(SplashBrand.Window, _fill);
            _fill.Shader = null;
        }

        // Window outline: traced on in the accent with a glow, then the brand's quiet stroke.
        if (o.TraceK < 1f)
        {
            using var trim = SKPathEffect.CreateTrim(0f, Math.Max(o.TraceK, 0.0001f));
            _stroke.PathEffect = trim;
            _stroke.StrokeCap = SKStrokeCap.Round;
            _stroke.StrokeWidth = 1.8f;
            _stroke.Color = A(f.Acc, a);
            _stroke.MaskFilter = Blur(4f * f.U / markScale);
            canvas.DrawPath(SplashBrand.Window, _stroke);
            _stroke.MaskFilter = null;
            canvas.DrawPath(SplashBrand.Window, _stroke);
            _stroke.PathEffect = null;
        }
        else
        {
            _stroke.StrokeCap = SKStrokeCap.Butt;
            _stroke.StrokeWidth = RemexBrandData.WindowStrokeWidth;
            _stroke.Color = A(SplashBrand.WindowStroke, RemexBrandData.WindowStrokeAlpha * a);
            canvas.DrawPath(SplashBrand.Window, _stroke);
        }

        if (o.Dot1 > 0f) { _fill.Color = A(f.Acc, a * o.Dot1); canvas.DrawPath(SplashBrand.Dot1, _fill); }
        if (o.Dot2 > 0f) { _fill.Color = A(SplashBrand.SlateLo, a * o.Dot2); canvas.DrawPath(SplashBrand.Dot2, _fill); }
        if (o.Dot3 > 0f) { _fill.Color = A(SplashBrand.SlateHi, a * o.Dot3); canvas.DrawPath(SplashBrand.Dot3, _fill); }

        if (o.ChevK > 0f)
        {
            _stroke.StrokeCap = SKStrokeCap.Round;
            _stroke.StrokeJoin = SKStrokeJoin.Round;
            _stroke.StrokeWidth = RemexBrandData.ChevronStrokeWidth;
            _stroke.Color = A(f.Acc, a);
            if (o.ChevK < 1f)
            {
                using var trim = SKPathEffect.CreateTrim(0f, o.ChevK);
                _stroke.PathEffect = trim;
                canvas.DrawPath(SplashBrand.Chevron, _stroke);
                _stroke.PathEffect = null;
            }
            else canvas.DrawPath(SplashBrand.Chevron, _stroke);
            _stroke.StrokeJoin = SKStrokeJoin.Miter;
        }

        if (o.RK > 0f)
        {
            canvas.Save();
            float rs = 0.7f + 0.3f * o.RK;
            canvas.Translate(58f, 60f);
            canvas.Scale(rs);
            canvas.Translate(-58f, -60f);
            float ra = a * Clamp01(o.RK);
            _fill.Color = A(SplashBrand.OffWhite, ra);
            canvas.DrawPath(SplashBrand.RStem, _fill);
            canvas.DrawPath(SplashBrand.RBowl, _fill);
            canvas.DrawPath(SplashBrand.RLeg, _fill);
            _fill.Shader = _markGrad;
            _fill.Color = SKColors.White.WithAlpha(Byte(ra));
            canvas.DrawPath(SplashBrand.RHole, _fill);
            _fill.Shader = null;
            canvas.Restore();
        }

        if (o.CursorA > 0f)
        {
            if (o.CursorGlow > 0.01f)
            {
                _fill.MaskFilter = Blur(7f * o.CursorGlow * f.U / markScale);
                _fill.Color = A(f.Acc, a * o.CursorA);
                canvas.DrawPath(SplashBrand.Cursor, _fill);
                _fill.MaskFilter = null;
            }
            _fill.Color = A(f.Acc, a * o.CursorA);
            canvas.DrawPath(SplashBrand.Cursor, _fill);
        }
    }

    /// <summary>The host's listener coming up locks a ring of four arcs onto the mark.</summary>
    private void DrawListenRing(SKCanvas canvas, Frame f)
    {
        if (float.IsNaN(f.ListenVis) || f.T < f.ListenVis) return;
        float k = f.Reduced ? 1f : Spring(f.T - f.ListenVis);
        float R = f.MarkW * Lerp(1.0f, 0.72f, k);
        _stroke.PathEffect = null;
        _stroke.MaskFilter = null;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeWidth = 1.5f * f.U;
        _stroke.Color = A(f.Acc, 0.8f * f.Fade * Clamp01(k * 1.5f));
        var oval = new SKRect(f.C.X - R, f.C.Y - R, f.C.X + R, f.C.Y + R);
        const float gapDeg = 0.25f * 180f / MathF.PI;
        for (int q = 0; q < 4; q++)
            canvas.DrawArc(oval, q * 90f + gapDeg, 90f - 2f * gapDeg, false, _stroke);
    }

    /// <summary>
    /// The wordmark and, under it, the console: a three-line terminal readout narrating the staged
    /// events (the lab's <c>drawConsole</c>). Newest line at full ink (accent when hot) typing in at
    /// 55 chars/s with the block cursor, the two above at 55 % and 30 %, the stack sliding up one line
    /// over 0.18 s as each line arrives. Reduced motion: whole lines, no slide, same timing.
    /// </summary>
    private void DrawWordmarkAndConsole(SKCanvas canvas, Frame f)
    {
        float u = f.U;
        float lh = ConsoleLineDp * u;
        float by = f.H * WordmarkYFrac;
        float top = by + ConsoleGapDp * u;
        // Keep the console's last line (and its cursor) clear of the host's version label and skip
        // hint, which sit in the bottom ~9 % of the shorter side (SkiaSplashControl.SplashDrawOp).
        float limit = f.H - MathF.Min(f.W, f.H) * 0.105f;
        float overflow = top + 2f * lh + 3f * u - limit;
        if (overflow > 0f) { by -= overflow; top -= overflow; }

        float alpha = f.Fade * f.Intro;
        if (alpha <= 0.001f) return;
        _fill.Shader = null;
        _fill.MaskFilter = null;

        var rem = Line("Rem", WordmarkDp * u, mono: false);
        var ex = Line("Ex", WordmarkDp * u, mono: false);
        float wx = f.C.X - (rem.Width + ex.Width) / 2f;
        _fill.Color = A(f.Ink, alpha);
        rem.Draw(canvas, wx, by, _fill);
        _fill.Color = A(f.Acc, alpha);
        ex.Draw(canvas, wx + rem.Width, by, _fill);

        var snap = f.Layout.Snapshot;
        var lines = _consoleLines;
        int count = LiveHandshakeDirector.PcConsole(
            snap.Peers.Count, snap.ListeningAt, snap.ListeningPort,
            f.Target >= 0 ? snap.Peers[f.Target].Name : null,
            float.IsNaN(f.LockAt) ? null : f.LockAt, f.Handoff, lines);
        int shown = 0;
        while (shown < count && lines[shown].At <= f.T) shown++;
        if (shown == 0) return;

        float colW = MathF.Min(f.W - 48f * u, ConsoleColumnDp * u);
        float x0 = f.C.X - colW / 2f;
        float age = f.T - lines[shown - 1].At;
        float slide = f.Reduced ? 0f : lh * (1f - Std.Ease(Clamp01(age / 0.18f)));
        float size = ConsoleDp * u;
        var prompt = Line("›", size, mono: true);
        int first = Math.Max(0, shown - 4);
        for (int i = first; i < shown; i++)
        {
            int k = shown - 1 - i; // 0 = newest
            if (k >= 3 && slide <= 0.5f) continue; // scrolled out
            float y = top + (2 - k) * lh + slide;
            float a = k == 0 ? 1f : k == 1 ? 0.55f : k == 2 ? 0.3f : 0.3f * (slide / lh);
            string text = LineText(lines[i]);
            int chars = k == 0 && !f.Reduced ? Math.Min(text.Length, (int)MathF.Floor(age * 55f)) : text.Length;
            if (chars > 0 && chars < text.Length && char.IsHighSurrogate(text[chars - 1])) chars--;
            var typed = Line(chars == text.Length ? text : text[..chars], size, mono: true);

            _fill.Color = A(f.Acc, alpha * a);
            prompt.Draw(canvas, x0, y, _fill);
            _fill.Color = A(k == 0 && lines[i].Hot ? f.Acc : (k == 0 ? f.Ink : f.Muted), alpha * a);
            float tx = x0 + 14f * u;
            typed.Draw(canvas, tx, y, _fill);
            if (k == 0 && (chars < text.Length || ((int)MathF.Floor(f.T / 0.53f)) % 2 == 0))
            {
                _fill.Color = A(f.Acc, alpha);
                canvas.DrawRect(tx + typed.Width + 3f * u, y - 10f * u, 6f * u, 12f * u, _fill);
            }
        }
    }

    /// <summary>A console line's localized text, cached per line identity (not its time).</summary>
    private string LineText(HandshakeLine line)
    {
        var key = line with { At = 0f };
        if (_lineText.TryGetValue(key, out var text)) return text;
        text = _text.Line(key);
        _lineText[key] = text;
        return text;
    }

    /// <summary>
    /// A shaped line from the cache (render thread only). Shaping is the expensive part — font
    /// fallback lookup plus HarfBuzz — so each distinct (text, size, face) is shaped once; the typing
    /// console lines and node suffixes are the only strings that change during a splash.
    /// </summary>
    private ShapedLine Line(string text, float size, bool mono)
    {
        var key = (text, size, mono);
        if (_lines.TryGetValue(key, out var line)) return line;
        if (_lines.Count >= 256) ClearLines();
        line = _shaper.Shape(text, mono ? MonoFace : (_fontFace ?? SKTypeface.Default), size);
        _lines[key] = line;
        return line;
    }

    private void ClearLines()
    {
        foreach (var l in _lines.Values) l.Dispose();
        _lines.Clear();
    }

    /// <summary>
    /// The console and node labels are monospace (the spec). The brand's Victor Mono when it loaded,
    /// else the first installed monospace family; the shaper still falls back per glyph for scripts
    /// a monospace face does not cover.
    /// </summary>
    private static SKTypeface MonoFace => SplashBrand.Typeface ?? MonoFallback.Value;

    private static readonly Lazy<SKTypeface> MonoFallback = new(() =>
    {
        foreach (var family in new[] { "Cascadia Mono", "Consolas", "JetBrains Mono", "DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono", "Menlo" })
        {
            var face = SKTypeface.FromFamilyName(family);
            if (face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return face;
            // NOT disposed: for a missing family the font manager hands back its default face, and
            // SkiaSharp maps one native typeface to one shared managed wrapper - disposing it here
            // would dispose the default face out from under every other user in the process.
        }
        return SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
    });

    // ── Small drawing helpers ─────────────────────────────────────────────────

    /// <summary>A soft accent disc fading to nothing at <paramref name="radius"/> (the lab's radial gradients).</summary>
    private void DrawRadial(SKCanvas canvas, SKPoint c, float radius, float alpha)
    {
        if (alpha <= 0.001f || radius <= 0f || _radialAcc is null) return;
        canvas.Save();
        canvas.Translate(c.X, c.Y);
        canvas.Scale(radius);
        _fill.MaskFilter = null;
        _fill.Shader = _radialAcc;
        _fill.Color = SKColors.White.WithAlpha(Byte(alpha));
        canvas.DrawCircle(0, 0, 1f, _fill);
        _fill.Shader = null;
        canvas.Restore();
    }

    private void DrawGlowDot(SKCanvas canvas, SKPoint p, float radius, SKColor core, SKColor glow, float alpha, float sigma)
    {
        _fill.Shader = null;
        _fill.MaskFilter = Blur(sigma);
        _fill.Color = A(glow, alpha);
        canvas.DrawCircle(p, radius * 1.6f, _fill);
        _fill.MaskFilter = null;
        _fill.Color = A(core, alpha);
        canvas.DrawCircle(p, radius, _fill);
    }

    private SKMaskFilter Blur(float sigma)
    {
        int key = Math.Max(1, (int)MathF.Round(sigma * 4f));
        if (!_blurs.TryGetValue(key, out var mf))
        {
            mf = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, key / 4f);
            _blurs[key] = mf;
        }
        return mf;
    }

    private void EnsureCaches(Frame f)
    {
        if (_radialAcc is null || _radialAccKey != f.Acc)
        {
            _radialAcc?.Dispose();
            _radialAccKey = f.Acc;
            _radialAcc = SKShader.CreateRadialGradient(
                new SKPoint(0, 0), 1f, new[] { f.Acc, f.Acc.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        }
        if (_markGrad is null || _markGradA != SplashBrand.MarkStart || _markGradB != SplashBrand.MarkEnd)
        {
            _markGrad?.Dispose();
            _markGradA = SplashBrand.MarkStart;
            _markGradB = SplashBrand.MarkEnd;
            _markGrad = SKShader.CreateLinearGradient(
                new SKPoint(20f + 68f * 0.056f, 26f + 56f * 0.056f),
                new SKPoint(20f + 68f * 0.944f, 26f + 56f * 0.944f),
                new[] { _markGradA, _markGradB }, null, SKShaderTileMode.Clamp);
        }
        if (_ghostDash is null || _ghostDashU != f.U)
        {
            _ghostDash?.Dispose();
            _ghostDashU = f.U;
            _ghostDash = SKPathEffect.CreateDash(new[] { 2.2f * f.U, 2.6f * f.U }, 0f);
        }
        var face = SplashBrand.Typeface ?? SKTypeface.Default;
        if (!ReferenceEquals(face, _fontFace) || _fontU != f.U)
        {
            ClearLines();
            _fontFace = face;
            _fontU = f.U;
        }
    }

    public void Dispose()
    {
        _uniforms?.Dispose();
        _fill.Dispose(); _stroke.Dispose(); _fieldPaint.Dispose(); _clip.Dispose();
        foreach (var mf in _blurs.Values) mf.Dispose();
        _blurs.Clear();
        _radialAcc?.Dispose(); _markGrad?.Dispose(); _backdrop?.Dispose(); _ghostDash?.Dispose();
        ClearLines();
        _shaper.Dispose();
    }

    // ── Math ───────────────────────────────────────────────────────────────────

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static SKPoint Lerp(SKPoint a, SKPoint b, float t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t));
    private static float Hypot(float x, float y) => MathF.Sqrt(x * x + y * y);

    private static float Smooth(float a, float b, float x)
    {
        float t = Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    /// <summary>Under-damped spring, 0 → 1 with ~16 % overshoot, settled by ~0.4 s.</summary>
    internal static float Spring(float tau) => tau <= 0f ? 0f : 1f - MathF.Exp(-tau * 9f) * MathF.Cos(tau * 16f);

    private static byte Byte(float a) => (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);

    private static SKColor A(SKColor c, float a) => c.WithAlpha(Byte(a * (c.Alpha / 255f)));

    private static float Luma(SKColor c) => (0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue) / 255f;

    // ── Per-frame state ────────────────────────────────────────────────────────

    private sealed class Frame
    {
        public float T, W, H, U, MarkW, Rx, Ry, ExitT, ExitE, PortalR, Env, Intro, ListenVis, LockAt, Fade = 1f;
        public bool Reduced, Exiting;
        public SKPoint C, E;
        public float? Handoff;
        public int N, Target, Pulses;
        public PeerLayout Layout = null!;
        public SKColor Pri, Acc, Ink, Muted;
        public readonly float[] Vis = new float[MaxPeers];
    }

    private struct MarkState
    {
        public float FillK, TraceK, Dot1, Dot2, Dot3, ChevK, RK, CursorA, CursorGlow;
    }

    private readonly record struct Ring(SKPoint Pos, float Start, float Strength);

    /// <summary>
    /// The snapshot plus everything derived from its peer list alone: orbit angles and the finished
    /// "name  linked" labels (rebuilt only when the peer names change, so a poll that only moves a
    /// timestamp does not re-hash anything), plus the director's answers: on the PC every linked phone
    /// answered at its link time, and <see cref="AnswerPeer"/> maps each answer back to its peer.
    /// </summary>
    private sealed class PeerLayout
    {
        public HandshakeSnapshot Snapshot { get; private init; } = HandshakeSnapshot.Empty;
        public float[] Angles { get; private init; } = Array.Empty<float>();
        public string[] LinkedLabels { get; private init; } = Array.Empty<string>();
        public HandshakeAnswer[] Answers { get; private init; } = Array.Empty<HandshakeAnswer>();
        public int[] AnswerPeer { get; private init; } = Array.Empty<int>();
        private string Signature { get; init; } = string.Empty;

        public static PeerLayout Build(HandshakeSnapshot snapshot, PeerLayout? previous, ILiveHandshakeText text)
        {
            int n = Math.Min(snapshot.Peers.Count, MaxPeers);
            var sig = string.Join("|", snapshot.Peers.Take(n).Select(p => p.Name));
            var (answers, answerPeer) = BuildAnswers(snapshot, n);
            if (previous is not null && previous.Signature == sig && previous.Angles.Length == n)
                return new PeerLayout
                {
                    Snapshot = snapshot, Angles = previous.Angles, LinkedLabels = previous.LinkedLabels,
                    Answers = answers, AnswerPeer = answerPeer, Signature = sig,
                };

            // Peer angles: base = -pi/2 + 0.62 + hash(names) * 0.5, evenly spaced (+0.4 rad when
            // there are exactly two) — the spec's layout rule, the lab's layoutNodes.
            float baseAngle = -MathF.PI / 2f + 0.62f + HashStr(sig) * 0.5f;
            var angles = new float[n];
            var labels = new string[n];
            for (int i = 0; i < n; i++)
            {
                angles[i] = baseAngle + i * Tau / Math.Max(n, 1) + (n == 2 ? 0.4f : 0f);
                labels[i] = snapshot.Peers[i].Name + "  " + text.LinkedSuffix;
            }
            return new PeerLayout
            {
                Snapshot = snapshot, Angles = angles, LinkedLabels = labels,
                Answers = answers, AnswerPeer = answerPeer, Signature = sig,
            };
        }

        private static (HandshakeAnswer[], int[]) BuildAnswers(HandshakeSnapshot snapshot, int n)
        {
            int count = 0;
            for (int i = 0; i < n; i++) if (snapshot.Peers[i].LinkedAt is not null) count++;
            if (count == 0) return (Array.Empty<HandshakeAnswer>(), Array.Empty<int>());
            var answers = new HandshakeAnswer[count];
            var peers = new int[count];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (snapshot.Peers[i].LinkedAt is not { } at) continue;
                answers[k] = new HandshakeAnswer(at, i == snapshot.TargetIndex);
                peers[k++] = i;
            }
            return (answers, peers);
        }

        /// <summary>The lab's FNV-1a <c>hashStr</c>, mapped to [0, 1).</summary>
        private static float HashStr(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return (h % 10000u) / 10000f;
        }
    }
}

/// <summary>A CSS-style cubic-bezier easing curve (x solved by Newton's method, as the lab does).</summary>
internal readonly struct CubicBezier
{
    private readonly float _ax, _bx, _cx, _ay, _by, _cy;

    public CubicBezier(float x1, float y1, float x2, float y2)
    {
        _cx = 3f * x1; _bx = 3f * (x2 - x1) - _cx; _ax = 1f - _cx - _bx;
        _cy = 3f * y1; _by = 3f * (y2 - y1) - _cy; _ay = 1f - _cy - _by;
    }

    public float Ease(float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        float t = x;
        for (int i = 0; i < 8; i++)
        {
            float d = (3f * _ax * t + 2f * _bx) * t + _cx;
            if (MathF.Abs(d) < 1e-6f) break;
            t -= (((_ax * t + _bx) * t + _cx) * t - x) / d;
        }
        t = Math.Clamp(t, 0f, 1f);
        return ((_ay * t + _by) * t + _cy) * t;
    }
}
