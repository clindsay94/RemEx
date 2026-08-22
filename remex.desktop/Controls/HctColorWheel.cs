using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Remex.Desktop.Services;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    /// Raised when the user finishes choosing — pointer released, or an arrow key let go. The panel
    /// uses it to push the settled seed into the recently-used list, which is why it is a separate
    /// signal from the property changes a drag fires on every frame.
    /// </summary>
    public event EventHandler? SeedCommitted;

    private readonly Dictionary<int, WriteableBitmap> _discCache = new();
    private bool _isDragging;

    static HctColorWheel()
    {
        AffectsRender<HctColorWheel>(HueProperty, ChromaProperty, ToneProperty);

        // A wheel nobody can reach with Tab is not keyboard-navigable, whatever its key handling does.
        FocusableProperty.OverrideDefaultValue<HctColorWheel>(true);
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

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            SeedCommitted?.Invoke(this, EventArgs.Empty);
    }

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

    private WriteableBitmap DiscFor(double tone)
    {
        var bucket = (int)Math.Round(Math.Clamp(tone, 0.0, 100.0) / ToneBucketSize);
        if (_discCache.TryGetValue(bucket, out var cached)) return cached;

        // Bounded rather than unbounded: the cache exists to make a tone DRAG cheap, and the whole
        // tone axis is only ToneBuckets discs wide, so anything past that is a bug, not a workload.
        if (_discCache.Count >= ToneBuckets) _discCache.Clear();

        var disc = RenderDisc(bucket * ToneBucketSize);
        _discCache[bucket] = disc;
        return disc;
    }

    private static WriteableBitmap RenderDisc(double tone)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(BitmapSize, BitmapSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        var radius = BitmapSize / 2.0;

        using (var framebuffer = bitmap.Lock())
        {
            // ROW BY ROW, USING THE FRAMEBUFFER'S OWN STRIDE. A backend is free to pad rows, and a
            // single copy of a tightly packed buffer into a padded one skews the image progressively
            // — which looks like a rendering bug in the disc rather than a copy bug here.
            var row = new byte[BitmapSize * 4];

            for (var y = 0; y < BitmapSize; y++)
            {
                Array.Clear(row);

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

                    var offset = x * 4;
                    row[offset + 0] = color.B;
                    row[offset + 1] = color.G;
                    row[offset + 2] = color.R;
                    row[offset + 3] = 0xFF;
                }

                Marshal.Copy(row, 0, framebuffer.Address + (y * framebuffer.RowBytes), row.Length);
            }
        }

        return bitmap;
    }
}
