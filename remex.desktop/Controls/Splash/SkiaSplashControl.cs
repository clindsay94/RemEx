using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Remex.Branding;
using Remex.Desktop.Localization;
using Remex.Desktop.Services;
using SkiaSharp;

namespace Remex.Desktop.Controls.Splash;

/// <summary>
/// Hosts the SkiaSharp splash variants inside Avalonia. Owns the frame loop (DispatcherTimer + real
/// Stopwatch dt), tap-to-skip, the version label + skip hint, and completion. Leases Avalonia's Skia
/// canvas so the pure-SkiaSharp variants (remex.branding) draw straight onto the render surface.
/// Fixed brand palette by design (not theme-adaptive) — replaces the old BootSequenceControl.
/// </summary>
public sealed class SkiaSplashControl : Control, IDisposable
{
    public static readonly StyledProperty<string> SplashStyleProperty =
        AvaloniaProperty.Register<SkiaSplashControl, string>(nameof(SplashStyle), "RemexCommand");

    public string SplashStyle
    {
        get => GetValue(SplashStyleProperty);
        set => SetValue(SplashStyleProperty, value);
    }

    /// <summary>Raised once when the splash finishes (natural end or skip).</summary>
    public event Action? SequenceCompleted;

    private const double SkipFadeSeconds = 0.2;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private ISplashVariant _variant = new RemexCommandVariant();
    private double _elapsed;
    private double _lastDt;
    private bool _completed;
    private bool _skipping;
    private double _skipElapsed;

    private static bool _typefaceLoaded;
    private static readonly string VersionLabel = ResolveVersion();

    static SkiaSplashControl()
    {
        AffectsRender<SkiaSplashControl>(SplashStyleProperty);
    }

    public SkiaSplashControl()
    {
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SplashStyleProperty)
        {
            _variant = CreateVariant(SplashStyle);
            _elapsed = 0;
            _completed = false;
            _skipping = false;
            _skipElapsed = 0;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureTypeface();
        _variant = CreateVariant(SplashStyle);
        _elapsed = 0;
        _completed = false;
        _skipping = false;
        _skipElapsed = 0;
        _stopwatch.Restart();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _stopwatch.Stop();
    }

    /// <summary>Plays the current <see cref="SplashStyle"/> from the start while attached — the sheet's Preview.</summary>
    public void Restart()
    {
        _variant = CreateVariant(SplashStyle);
        _elapsed = 0;
        _completed = false;
        _skipping = false;
        _skipElapsed = 0;
        _stopwatch.Restart();
        _timer.Start();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        BeginSkip();
    }

    /// <summary>Start the short fade-out and finish (tap-to-skip).</summary>
    public void BeginSkip()
    {
        if (_completed || _skipping) return;
        _skipping = true;
        _skipElapsed = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_completed) return;
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        _lastDt = dt;
        _elapsed += dt;

        if (_skipping)
        {
            _skipElapsed += dt;
            if (_skipElapsed >= SkipFadeSeconds) { Complete(); return; }
        }
        else if (_elapsed >= _variant.Duration)
        {
            Complete();
            return;
        }

        InvalidateVisual();
    }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        _timer.Stop();
        _stopwatch.Stop();
        InvalidateVisual();
        SequenceCompleted?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        float skipAlpha = _skipping ? (float)Math.Clamp(_skipElapsed / SkipFadeSeconds, 0, 1) : 0f;
        context.Custom(new SplashDrawOp(bounds, _variant, (float)_elapsed, (float)_lastDt, skipAlpha, VersionLabel, SkipHint()));
    }

    private static ISplashVariant CreateVariant(string? style) => style switch
    {
        "CosmicZoom" => new CosmicZoomVariant(),
        "Pong" => new PongVariant(),
        _ => new RemexCommandVariant(),
    };

    private static void EnsureTypeface()
    {
        if (_typefaceLoaded) return;
        _typefaceLoaded = true;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Remex.Desktop/Assets/Fonts/victor_mono_bold.ttf"));
            SplashBrand.LoadTypeface(stream);
        }
        catch
        {
            // Fall back to the default monospace face; text still renders.
        }
    }

    private static string ResolveVersion()
    {
        // Shared with the About page so the splash and About never disagree about the version.
        var info = AppVersion.Display;
        return string.IsNullOrEmpty(info) ? "" : "v" + info;
    }

    /// <summary>
    /// Stops the frame timer and detaches its Tick handler. Previously this control had no Dispose
    /// at all: <c>_timer.Tick += OnTick</c> (constructor) was subscribed once but never had a matching
    /// <c>-=</c> anywhere, so the DispatcherTimer kept a strong reference back into this control (via
    /// OnTick's target) for as long as the timer object itself lived — a real leak for any host that
    /// creates/replaces splash instances rather than keeping exactly one for the app's lifetime.
    /// OnDetachedFromVisualTree deliberately still only calls Stop() (not this), because the control
    /// can be reattached to the visual tree afterward and OnAttachedToVisualTree relies on the same
    /// Tick subscription still being wired up to resume ticking.
    /// </summary>
    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private static string SkipHint()
    {
        try
        {
            var s = LocalizationService.Instance["Splash_SkipHint"];
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch { /* fall through */ }
        return "Click anywhere to skip";
    }

    /// <summary>The custom draw op that leases the Skia canvas and paints one frame.</summary>
    private sealed class SplashDrawOp(Rect bounds, ISplashVariant variant, float t, float dt, float skipAlpha, string version, string hint)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return; // non-Skia backend: draw nothing (splash simply doesn't animate)
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            float w = (float)bounds.Width, h = (float)bounds.Height;

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, w, h));

            variant.Render(canvas, w, h, t, dt);

            // Version label + skip hint fade in (bottom-center), matching the Android chrome.
            float versionAlpha = Math.Clamp((t - 0.2f) / 0.4f, 0f, 1f);
            float hintAlpha = Math.Clamp((t - 0.8f) / 0.5f, 0f, 0.7f);
            float baseSize = MathF.Min(w, h);
            if (versionAlpha > 0 && version.Length > 0)
                SplashBrand.DrawText(canvas, version, w / 2f, h - baseSize * 0.06f, baseSize * 0.022f, SplashBrand.SlateLo, versionAlpha);
            if (hintAlpha > 0 && hint.Length > 0)
                SplashBrand.DrawText(canvas, hint, w / 2f, h - baseSize * 0.03f, baseSize * 0.018f, SplashBrand.SlateLo, hintAlpha);

            // Tap-to-skip fade to the brand substrate.
            if (skipAlpha > 0)
            {
                using var fade = new SKPaint { Color = SplashBrand.WindowFill.WithAlpha((byte)(skipAlpha * 255f)) };
                canvas.DrawRect(0, 0, w, h, fade);
            }

            canvas.Restore();
        }
    }
}
