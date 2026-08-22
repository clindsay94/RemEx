using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Remex.Desktop.Services;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Desktop.Controls;

/// <summary>
/// The Palette Studio's seed wheel: hue around the circumference, chroma along the radius, drawn at
/// the seed's current tone. Dragging it picks a seed; the arrow keys do the same thing without a
/// mouse.
/// </summary>
/// <remarks>
/// <para>
/// HAND-DRAWN RATHER THAN <c>Avalonia.Controls.ColorPicker</c>, for two reasons. That package is not
/// in this project's restore graph — only Material.Avalonia's stale 11.3.11 copy is in the local
/// cache — so using it would add a dependency; and its spectrum is HSV, which is not the space the
/// palette is generated in. A wheel whose radius means "saturation" while the engine reads chroma
/// would put the thumb somewhere the colour is not.
/// </para>
/// <para>
/// THE DISC IS A CACHED BITMAP KEYED BY TONE, and that is a measurement, not a habit. Every pixel
/// costs an <see cref="SeedHct.ToColor"/> solve; at the rendered size that is tens of thousands of
/// them, which is far too slow to do inside <see cref="Render"/>. Hue and chroma are the two axes
/// the disc already shows, so dragging the wheel never rebuilds it — only moving TONE does, and
/// tone is bucketed so a slider drag reuses at most <see cref="ToneBuckets"/> bitmaps.
/// </para>
/// </remarks>
public class HctColorWheel : Control
{
    /// <summary>
    /// Pixels across the generated disc. Deliberately smaller than the control: a hue/chroma disc is
    /// smooth everywhere, so the upscale is invisible, and the solve cost is quadratic in this.
    /// </summary>
    private const int BitmapSize = 128;

    /// <summary>Tone is rounded to a multiple of this before it selects a cached disc.</summary>
    private const double ToneBucketSize = 4.0;

    /// <summary>How many distinct discs are kept. 100/4 + 1 covers the whole tone axis.</summary>
    private const int ToneBuckets = 26;

    /// <summary>Hue step for an arrow key, in degrees. Shift multiplies it by <see cref="CoarseKeyFactor"/>.</summary>
    private const double HueKeyStep = 2.0;

    /// <summary>Chroma step for an arrow key.</summary>
    private const double ChromaKeyStep = 2.0;

    private const double CoarseKeyFactor = 5.0;

    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<HctColorWheel, double>(nameof(Hue), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ChromaProperty =
        AvaloniaProperty.Register<HctColorWheel, double>(nameof(Chroma), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ToneProperty =
        AvaloniaProperty.Register<HctColorWheel, double>(nameof(Tone), 50.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>The seed's hue in degrees, 0–360.</summary>
    public double Hue
    {
        get => GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    /// <summary>The seed's chroma, 0–<see cref="SeedHct.MaxChroma"/>.</summary>
    public double Chroma
    {
        get => GetValue(ChromaProperty);
        set => SetValue(ChromaProperty, value);
    }

    /// <summary>The seed's tone, 0–100. Changing it redraws the disc.</summary>
    public double Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>
    /// Raised when the user finishes choosing — a pointer drag released, or keyboard focus leaving
    /// the wheel. The panel uses it to push the settled seed into the recently-used list, which is
    /// why it is a separate signal from the property changes a drag fires on every frame.
    /// </summary>
    /// <remarks>
    /// NOT ON KEY-UP. A single arrow tap is a 2° nudge, not a decision, so committing per key-up
    /// filled the recently-used row with a run of colours indistinguishable from each other. Leaving
    /// the control is the keyboard's equivalent of letting go.
    /// </remarks>
    public event EventHandler? SeedCommitted;

    private readonly Dictionary<int, WriteableBitmap> _discCache = new();
    private bool _isDragging;

    /// <summary>
    /// The seed as it stood when this control last gained focus, so <see cref="OnLostFocusCommit"/>
    /// can tell an edit from a visit.
    /// </summary>
    private (double Hue, double Chroma, double Tone)? _seedAtFocus;

    private bool _isWarming;
    private CancellationTokenSource _warmCancellation = new();

    static HctColorWheel()
    {
        AffectsRender<HctColorWheel>(HueProperty, ChromaProperty, ToneProperty);

        // A wheel nobody can reach with Tab is not keyboard-navigable, whatever its key handling does.
        FocusableProperty.OverrideDefaultValue<HctColorWheel>(true);
    }

    public HctColorWheel()
    {
        GotFocus += OnGotFocusRemember;
        LostFocus += OnLostFocusCommit;
    }

    // ═══════════════ Geometry ═══════════════

    /// <summary>
    /// Where the thumb for a given hue/chroma sits inside a square of side <paramref name="side"/>.
    /// Hue runs anticlockwise from the right, matching the disc the generator writes.
    /// </summary>
    internal static Point HueChromaToPoint(double hue, double chroma, double side)
    {
        var radius = side / 2.0;
        var normalized = Math.Clamp(chroma, 0.0, SeedHct.MaxChroma) / SeedHct.MaxChroma;
        var angle = SeedHct.NormalizeHue(hue) * Math.PI / 180.0;
        return new Point(
            radius + Math.Cos(angle) * normalized * radius,
            radius - Math.Sin(angle) * normalized * radius);
    }

    /// <summary>
    /// The hue and chroma a pointer at <paramref name="point"/> selects. Points outside the disc
    /// clamp to its rim rather than being ignored, so a drag that leaves the control keeps tracking.
    /// </summary>
    internal static (double Hue, double Chroma) PointToHueChroma(Point point, double side)
    {
        var radius = side / 2.0;
        if (radius <= 0) return (0.0, 0.0);

        var dx = point.X - radius;
        var dy = radius - point.Y;

        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var chroma = Math.Clamp(distance / radius, 0.0, 1.0) * SeedHct.MaxChroma;

        // At the exact centre every hue is equally correct and Atan2 answers 0; keeping the current
        // hue there would need state, so 0 it is — chroma is 0 anyway, i.e. the colour is grey.
        var hue = SeedHct.NormalizeHue(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        return (hue, chroma);
    }

    private double Side => Math.Min(Bounds.Width, Bounds.Height);

    // ═══════════════ Input ═══════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        e.Pointer.Capture(this);
        Focus();
        ApplyPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;
        ApplyPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging) return;

        _isDragging = false;
        e.Pointer.Capture(null);
        SeedCommitted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        // A capture stolen mid-drag (a flyout opening, the window losing focus) still ends the
        // interaction. Without this the wheel would keep following the pointer with no button down.
        if (!_isDragging) return;
        _isDragging = false;
        SeedCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var coarse = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? CoarseKeyFactor : 1.0;
        switch (e.Key)
        {
            case Key.Left:
                Hue = SeedHct.NormalizeHue(Hue + (HueKeyStep * coarse));
                break;
            case Key.Right:
                Hue = SeedHct.NormalizeHue(Hue - (HueKeyStep * coarse));
                break;
            case Key.Up:
                Chroma = Math.Clamp(Chroma + (ChromaKeyStep * coarse), 0.0, SeedHct.MaxChroma);
                break;
            case Key.Down:
                Chroma = Math.Clamp(Chroma - (ChromaKeyStep * coarse), 0.0, SeedHct.MaxChroma);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// THE KEYBOARD'S "I HAVE FINISHED" IS LEAVING THE CONTROL, NOT LIFTING A KEY, and getting that
    /// wrong was visible immediately. Committing on key-up meant five taps of Left were five separate
    /// choices: the recently-used row came back from a verification run holding #068EC6, #008EC4,
    /// #008FC1, #008FBE, #0090BC, #0090B9, #0091B2 — seven swatches nobody could tell apart, filling
    /// the row and pushing out every colour the user had actually picked. A pointer drag has a real
    /// end event and keeps using it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SUBSCRIBED RATHER THAN OVERRIDDEN: Avalonia 12 does not expose a virtual <c>OnLostFocus</c> on
    /// <c>Control</c>, so the routed event is the seam that exists.
    /// </para>
    /// <para>
    /// AND ONLY WHEN THE SEED ACTUALLY MOVED. Leaving is not the same as choosing: tabbing THROUGH the
    /// drawer would otherwise commit whatever seed happened to be loaded, evicting a colour the user
    /// had deliberately saved once the list of eight was full — from pure keyboard navigation, with
    /// nothing touched. Worse, when the departing focus is a click on the eighth recents swatch, the
    /// eviction destroys the Button under the pointer before it can be released, so that swatch
    /// silently does nothing.
    /// </para>
    /// </remarks>
    private void OnLostFocusCommit(object? sender, RoutedEventArgs e)
    {
        // NO RECORDED VISIT MEANS NO EDIT, so the default is silence rather than a commit. Focus is a
        // precondition for changing anything here — the keyboard needs it and the pointer path takes
        // it — so a LostFocus with nothing remembered cannot be carrying a user's choice.
        var moved = _seedAtFocus is { } start
            && (Math.Abs(start.Hue - Hue) > 0.001
                || Math.Abs(start.Chroma - Chroma) > 0.001
                || Math.Abs(start.Tone - Tone) > 0.001);

        _seedAtFocus = null;
        if (moved) SeedCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnGotFocusRemember(object? sender, RoutedEventArgs e) => _seedAtFocus = (Hue, Chroma, Tone);

    private void ApplyPointer(Point position)
    {
        var (hue, chroma) = PointToHueChroma(position, Side);
        Hue = hue;
        Chroma = chroma;
    }

    // ═══════════════ Rendering ═══════════════

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var side = Side;
        if (side <= 1) return;

        var disc = DiscFor(Tone);
        var destination = new Rect(0, 0, side, side);
        context.DrawImage(disc, new Rect(0, 0, BitmapSize, BitmapSize), destination);

        // The rim and the thumb both have to stay legible on whatever palette the seed just
        // produced, so they come from the theme rather than being white (RemEx-fy0a).
        var outline = ThemeResources.Brush("CardBorderBrush", new SolidColorBrush(Color.Parse("#2A2A3E")));
        var thumbRing = ThemeResources.Brush("TextPrimaryBrush", new SolidColorBrush(Color.Parse("#C0C0FF")));

        var radius = side / 2.0;
        context.DrawEllipse(null, new Pen(outline, 1), new Point(radius, radius), radius - 0.5, radius - 0.5);

        var thumb = HueChromaToPoint(Hue, Chroma, side);
        context.DrawEllipse(null, new Pen(thumbRing, 2), thumb, 7, 7);

        if (IsFocused)
        {
            var focus = ThemeResources.Brush("AccentPrimaryBrush", new SolidColorBrush(Color.Parse("#6C4CFF")));
            context.DrawEllipse(null, new Pen(focus, 2, new DashStyle(new double[] { 2, 2 }, 0)), new Point(radius, radius), radius - 2, radius - 2);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The focus ring is painted in Render, so it only appears if focus invalidates the visual.
        if (change.Property == IsFocusedProperty) InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // THE CHROME DOES NOT REPAINT ON ITS OWN. The rim, thumb and focus ring are resolved by
        // ThemeResources INSIDE Render, which reads the current value but does not subscribe the way
        // a DynamicResource binding would — and AffectsRender only covers the three seed axes. A
        // palette change that does not move the seed (picking the Dynamic preset, which deliberately
        // leaves AccentColor alone) would otherwise leave this control's chrome drawn in the previous
        // palette's brushes. Most visible on a light palette, where a dark thumb ring is the usual
        // contrast failure.
        //
        // ResourcesChanged rather than ActualThemeVariantChanged: ThemeService repaints by swapping
        // values in a merged dictionary, so the variant frequently does not change at all.
        if (Application.Current is { } app) app.ResourcesChanged += OnAppResourcesChanged;

        WarmDiscCache();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (Application.Current is { } app) app.ResourcesChanged -= OnAppResourcesChanged;

        _warmCancellation.Cancel();
        _warmCancellation.Dispose();
        _warmCancellation = new CancellationTokenSource();
        _isWarming = false;

        // WriteableBitmap owns a native surface. Navigating in and out of Personalize builds a new
        // panel and a new wheel each time, so leaving up to ToneBuckets discs (~1.7 MB of native
        // memory per wheel) to a finaliser puts an unowned cost on a process that is also running the
        // capture pipeline. Releasing at detach is deterministic; the cache rebuilds on reattach.
        ClearDiscCache();
    }

    private void OnAppResourcesChanged(object? sender, ResourcesChangedEventArgs e) => InvalidateVisual();

    private void ClearDiscCache()
    {
        foreach (var disc in _discCache.Values) disc.Dispose();
        _discCache.Clear();
    }

    private WriteableBitmap DiscFor(double tone)
    {
        var bucket = BucketFor(tone);
        if (_discCache.TryGetValue(bucket, out var cached)) return cached;

        // Bounded rather than unbounded: the cache exists to make a tone DRAG cheap, and the whole
        // tone axis is only ToneBuckets discs wide, so anything past that is a bug, not a workload.
        if (_discCache.Count >= ToneBuckets) ClearDiscCache();

        // The synchronous path stays as the fallback for a miss the warm-up has not reached yet.
        var disc = BitmapFrom(SolveDiscPixels(bucket * ToneBucketSize));
        _discCache[bucket] = disc;
        return disc;
    }

    private static int BucketFor(double tone) => (int)Math.Round(Math.Clamp(tone, 0.0, 100.0) / ToneBucketSize);

    /// <summary>
    /// Fills the whole tone axis on a background thread, so a tone drag never pays for a solve.
    /// </summary>
    /// <remarks>
    /// MEASURED, AND THE REASON THIS EXISTS. One disc is 12,892 in-disc solves and takes about 43 ms
    /// — roughly two and a half frames. Caching alone made a REPEATED tone cheap but did nothing for
    /// a MOVING one: dragging the tone slider from 0 to 100 crosses every bucket, so the dispatcher
    /// would absorb 26 cold solves, about 1.1 seconds of stalls spread over the drag, interleaved
    /// with the palette regeneration each step already triggers. The wheel drag never showed this
    /// because hue and chroma are the axes the disc already contains; only tone invalidates it.
    /// <para>
    /// ONLY THE MATHS MOVES OFF THE UI THREAD. The pixel buffers are pure computation and safe
    /// anywhere; the <see cref="WriteableBitmap"/>s are built back on the dispatcher, where they cost
    /// an allocation and a memcpy each. That keeps the threading question away from a graphics
    /// resource rather than betting on it being safe.
    /// </para>
    /// </remarks>
    private void WarmDiscCache()
    {
        if (_isWarming) return;
        _isWarming = true;

        var token = _warmCancellation.Token;

        Task.Run(() =>
        {
            for (var bucket = 0; bucket <= ToneBuckets - 1; bucket++)
            {
                if (token.IsCancellationRequested) return;

                var pixels = SolveDiscPixels(bucket * ToneBucketSize);
                var captured = bucket;

                Dispatcher.UIThread.Post(() =>
                {
                    // The control may have detached — or the cache been cleared — while this bucket
                    // was being solved. Dropping the result is correct; a later miss re-solves it.
                    if (token.IsCancellationRequested || _discCache.ContainsKey(captured)) return;

                    _discCache[captured] = BitmapFrom(pixels);
                    if (BucketFor(Tone) == captured) InvalidateVisual();
                });
            }
        }, token);
    }

    /// <summary>The expensive half: one tightly packed BGRA buffer for a disc at <paramref name="tone"/>.</summary>
    private static byte[] SolveDiscPixels(double tone)
    {
        var pixels = new byte[BitmapSize * BitmapSize * 4];
        var radius = BitmapSize / 2.0;

        for (var y = 0; y < BitmapSize; y++)
        {
            for (var x = 0; x < BitmapSize; x++)
            {
                var dx = x + 0.5 - radius;
                var dy = y + 0.5 - radius;

                // Outside the disc stays fully transparent, and premultiplied alpha means every
                // channel has to be zero too or the colour bleeds past the rim when composited.
                if ((dx * dx) + (dy * dy) > radius * radius) continue;

                // Sample the pixel CENTRE, not its corner — sampling corners shifts the whole
                // disc half a pixel up and left of the ellipse drawn over it.
                var (hue, chroma) = PointToHueChroma(new Point(x + 0.5, y + 0.5), BitmapSize);
                var color = SeedHct.ToColor(hue, chroma, tone);

                var offset = ((y * BitmapSize) + x) * 4;
                pixels[offset + 0] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 0xFF;
            }
        }

        return pixels;
    }

    /// <summary>The cheap half: wrap a solved buffer in a bitmap. Dispatcher thread only.</summary>
    private static WriteableBitmap BitmapFrom(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(BitmapSize, BitmapSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var framebuffer = bitmap.Lock())
        {
            // ROW BY ROW, USING THE FRAMEBUFFER'S OWN STRIDE. A backend is free to pad rows, and a
            // single copy of a tightly packed buffer into a padded one skews the image progressively
            // — which looks like a rendering bug in the disc rather than a copy bug here.
            for (var y = 0; y < BitmapSize; y++)
            {
                Marshal.Copy(
                    pixels,
                    y * BitmapSize * 4,
                    framebuffer.Address + (y * framebuffer.RowBytes),
                    BitmapSize * 4);
            }
        }

        return bitmap;
    }
}
