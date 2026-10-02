using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-a9aez. The keyboard focus ring on a slider, checked against Material's real template
/// parts and in rendered pixels.
/// </summary>
/// <remarks>
/// <para>
/// Material 3.19.0's slider theme makes the Slider unfocusable and its Thumb the tab stop, so the
/// old <c>Slider:focus-visible</c> ring in App.axaml never matched and a keyboard user arrowing a
/// slider saw nothing. The ring now lives on the thumb's <c>Border#PART_HoverEffect</c>. A selector
/// that names a template part the template no longer has is silently inert, which is why this
/// renders rather than reading App.axaml: a Material update that renames or drops the part turns
/// these red instead of quietly losing the ring again.
/// </para>
/// <para>
/// Focus is moved with <c>Focus(NavigationMethod.Tab)</c> on a headless window - the same lever
/// SelectionControlRenderTests uses for the slider's arrow keys - so nothing here injects input into
/// a real desktop.
/// </para>
/// </remarks>
public sealed class SliderFocusRingRenderTests
{
    private const int WindowWidth = 320;
    private const int WindowHeight = 160;

    private static (Window Window, Slider Slider, Thumb Thumb, Border Halo) Open()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 10,
            Value = 5,
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = slider };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var thumb = slider.GetVisualDescendants().OfType<Thumb>()
            .SingleOrDefault(t => t.Name == "PART_SliderThumb");
        thumb.Should().NotBeNull("Material's slider template must still name its thumb PART_SliderThumb");
        slider.Focusable.Should().BeFalse(
            "this ring exists because Material makes the Slider unfocusable; if that changes, the "
            + "control-level ring would work again and this design should be revisited");
        thumb!.Focusable.Should().BeTrue("the thumb is the slider's keyboard tab stop");

        var halo = thumb.GetVisualDescendants().OfType<Border>()
            .SingleOrDefault(b => b.Name == "PART_HoverEffect");
        halo.Should().NotBeNull(
            "the ring is drawn on the thumb's PART_HoverEffect border; without it App.axaml's "
            + "slider ring selector matches nothing");
        return (window, slider, thumb, halo!);
    }

    private static Color Accent()
    {
        Application.Current!.TryFindResource("AccentPrimaryBrush", out var resource).Should().BeTrue();
        return resource.Should().BeAssignableTo<ISolidColorBrush>().Subject.Color;
    }

    /// <summary>
    /// Renders until <paramref name="done"/> holds or about a second passes. Material animates the
    /// halo's Opacity, so the first frame after focus can be mid-transition.
    /// </summary>
    private static FramePixels RenderUntil(Window window, Func<FramePixels, bool> done)
    {
        FramePixels? pixels = null;
        for (var i = 0; i < 40; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                pixels = FramePixels.From(frame);
                if (done(pixels)) break;
            }

            Thread.Sleep(25);
        }

        pixels.Should().NotBeNull("the headless window never produced a frame");
        return pixels!;
    }

    private static (int X, int Y) RingSample(Window window, Thumb thumb)
    {
        // Straight above the thumb's centre, 13px out: inside the 28px ring's 2px band (radius
        // 12-14) and clear of the 6px track, so before focus this pixel is plain window background.
        var centre = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        return ((int)Math.Round(centre.X), (int)Math.Round(centre.Y) - 13);
    }

    private static bool Near(Color a, Color b, int tolerance = 40) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    [AvaloniaFact]
    public void KeyboardFocusOnTheThumbDrawsAnAccentRingAroundIt()
    {
        var (window, _, thumb, halo) = Open();
        try
        {
            var accent = Accent();
            var (x, y) = RingSample(window, thumb);

            var before = RenderUntil(window, _ => true).ColorAt(x, y);
            Near(before, accent).Should().BeFalse(
                $"the sample point must not already be accent before focus, or this checks nothing (read {before})");

            thumb.Focus(NavigationMethod.Tab).Should().BeTrue();
            Dispatcher.UIThread.RunJobs();

            halo.BorderThickness.Should().Be(new Thickness(2), "the ring is the app's 2px focus ring");
            halo.BorderBrush.Should().BeAssignableTo<ISolidColorBrush>()
                .Which.Color.Should().Be(accent, "the ring uses AccentPrimaryBrush like every other ring");
            halo.Background.Should().BeAssignableTo<ISolidColorBrush>()
                .Which.Color.A.Should().Be(0, "the halo is emptied so the ring is a ring, not an accent disc");

            var after = RenderUntil(window, p => Near(p.ColorAt(x, y), accent)).ColorAt(x, y);
            Near(after, accent).Should().BeTrue(
                $"keyboard focus must paint an accent ring around the thumb (expected ~{accent}, read {after} at {x},{y})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PointerFocusLeavesNoRing()
    {
        var (window, _, thumb, halo) = Open();
        try
        {
            thumb.Focus(NavigationMethod.Pointer).Should().BeTrue();
            Dispatcher.UIThread.RunJobs();

            thumb.IsFocused.Should().BeTrue();
            halo.BorderThickness.Should().Be(default(Thickness),
                "the ring is :focus-visible, so clicking or dragging a slider must not leave one behind");
        }
        finally
        {
            window.Close();
        }
    }
}
