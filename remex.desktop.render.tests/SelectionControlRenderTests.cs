using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views.Personalize;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// Two selection-control defects that only show once Material's real templates are applied, which
/// is why they live here and not in the XAML-text tests.
/// </summary>
public sealed class SelectionControlRenderTests
{
    // ─────────────────────────── ToggleSwitch: no word on the thumb ───────────────────────────

    /// <summary>
    /// Material.Avalonia's ToggleSwitch template puts OnContent/OffContent INSIDE the 20 px thumb, so
    /// a switch given OffContent text drew "Off" over the knob while off - seen on Personalize >
    /// Layout > Reduced motion. App.axaml hides the thumb's content panel for every switch.
    /// </summary>
    /// <remarks>
    /// The first row is the defect (red without the App.axaml style). The checked rows are green
    /// either way under Material 3.19.0, which keeps the On presenter hidden; they are here so a
    /// Material update that starts showing OnContent (Avalonia defaults it to the string "On") is
    /// caught too.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(false, "On", "Off")]
    [InlineData(true, "On", "Off")]
    [InlineData(true, null, null)]
    public void NoSwitchDrawsTextOnItsThumb(bool isChecked, string? on, string? off)
    {
        var toggle = new ToggleSwitch { IsChecked = isChecked };
        if (on is not null) toggle.OnContent = on;
        if (off is not null) toggle.OffContent = off;
        var window = new Window { Width = 400, Height = 200, Content = toggle };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var thumbPresenters = toggle.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .Where(p => p.Name is "OnContentPresenter" or "OffContentPresenter")
            .ToList();
        thumbPresenters.Should().NotBeEmpty(
            "Material's template must still have its thumb presenters, or this test checks nothing");

        thumbPresenters.Where(p => p.IsEffectivelyVisible && p.Content is not null)
            .Select(p => $"{p.Name}: {p.Content}")
            .Should().BeEmpty("a word drawn inside the 20 px thumb overlaps the knob");
        window.Close();
    }

    // ─────────────────────────── Contrast slider detent ───────────────────────────

    private static (Window Window, Slider Slider, CustomizationViewModel Vm, DashboardLayoutService Layout) OpenColourTab()
    {
        App.EmbeddedHostServices = null;
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(theme);
        var vm = new CustomizationViewModel(null!, layout, theme);
        var tab = new PersonalizeColourTab { DataContext = vm };
        var window = new Window { Width = 1100, Height = 900, Content = tab };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var name = LocalizationService.Instance["Custom_ContrastLevel"];
        var slider = tab.GetVisualDescendants().OfType<Slider>()
            .Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == name);
        return (window, slider, vm, layout);
    }

    /// <summary>
    /// RemEx-4kv0g.16: a value the slider writes within 0.05 of the centre lands on 0 in the view
    /// model AND moves the thumb there through the TwoWay binding - otherwise the label reads 0.0
    /// while the thumb sits a few pixels off it.
    /// </summary>
    [AvaloniaFact]
    public void ASliderWriteNearTheCentreMovesTheThumbOntoZero()
    {
        var (window, slider, vm, layout) = OpenColourTab();
        try
        {
            slider.SetCurrentValue(RangeBase.ValueProperty, 0.03);
            Dispatcher.UIThread.RunJobs();

            vm.ThemeContrast.Should().Be(0.0);
            slider.Value.Should().Be(0.0, "the snapped value has to flow back to the thumb");

            slider.SetCurrentValue(RangeBase.ValueProperty, 0.97);
            Dispatcher.UIThread.RunJobs();
            slider.Value.Should().Be(1.0);

            slider.SetCurrentValue(RangeBase.ValueProperty, 0.4);
            Dispatcher.UIThread.RunJobs();
            slider.Value.Should().Be(0.4, "outside every detent the slider keeps exactly what was set");
            vm.ThemeContrast.Should().Be(0.4);
        }
        finally
        {
            window.Close();
            layout.Dispose();
        }
    }

    /// <summary>
    /// The detent must not trap a drag: Avalonia's Slider computes each move from the pointer's
    /// absolute position, so once the pointer leaves the band the value follows it again.
    /// </summary>
    [AvaloniaFact]
    public void ADragThroughTheCentreSnapsThenFollowsThePointerOut()
    {
        var (window, slider, vm, layout) = OpenColourTab();
        try
        {
            var track = slider.GetVisualDescendants().OfType<Track>().First();
            var thumbWidth = track.Thumb?.Bounds.Width ?? 0.0;
            var usable = track.Bounds.Width - thumbWidth;
            Point At(double value) => track.TranslatePoint(
                new Point(thumbWidth / 2 + (value + 1) / 2 * usable, track.Bounds.Height / 2), window)!.Value;

            window.MouseDown(At(0.02), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            vm.ThemeContrast.Should().Be(0.0, "a press just right of centre lands on the detent");

            window.MouseMove(At(0.5));
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(At(0.5), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            vm.ThemeContrast.Should().BeApproximately(0.5, 0.03,
                "dragging out of the detent follows the pointer instead of sticking at 0");
        }
        finally
        {
            window.Close();
            layout.Dispose();
        }
    }

    /// <summary>
    /// Keyboard stays plain: the 0.1 arrow steps never fall inside a detent, and stepping back to the
    /// centre lands on exactly 0 rather than 5.6e-17 of floating-point leftover.
    /// </summary>
    [AvaloniaFact]
    public void ArrowKeysStepByTenthsAndComeBackToExactlyZero()
    {
        var (window, slider, vm, layout) = OpenColourTab();
        try
        {
            slider.SetCurrentValue(RangeBase.ValueProperty, 0.0);
            Dispatcher.UIThread.RunJobs();
            // Material's Slider theme makes the Slider itself unfocusable and its Thumb the tab stop;
            // the arrow keys bubble from the thumb up to Slider.OnKeyDown.
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
            thumb.Focus(NavigationMethod.Tab).Should().BeTrue("the thumb is the slider's keyboard tab stop");

            void Press(Key key, PhysicalKey physical)
            {
                window.KeyPress(key, RawInputModifiers.None, physical, null);
                window.KeyRelease(key, RawInputModifiers.None, physical, null);
                Dispatcher.UIThread.RunJobs();
            }

            Press(Key.Right, PhysicalKey.ArrowRight);
            vm.ThemeContrast.Should().BeApproximately(0.1, 1e-9, "one arrow step is still one tenth");
            Press(Key.Right, PhysicalKey.ArrowRight);
            Press(Key.Right, PhysicalKey.ArrowRight);
            vm.ThemeContrast.Should().BeApproximately(0.3, 1e-9);

            Press(Key.Left, PhysicalKey.ArrowLeft);
            Press(Key.Left, PhysicalKey.ArrowLeft);
            Press(Key.Left, PhysicalKey.ArrowLeft);
            vm.ThemeContrast.Should().Be(0.0, "three tenths up and three down is the centre, exactly");
            slider.Value.Should().Be(0.0);
        }
        finally
        {
            window.Close();
            layout.Dispose();
        }
    }
}
