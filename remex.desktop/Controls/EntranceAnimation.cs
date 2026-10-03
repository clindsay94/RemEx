using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Desktop.Styles;

namespace Remex.Desktop.Controls;

/// <summary>
/// The one "something arrives" animation the PC plays from code (RemEx-pp4cm.1): fade up from
/// nothing while settling a few pixels into place, on M3's emphasized-decelerate curve. Used for list
/// rows (<see cref="ListEntrance"/>) and the tray flyout opening (<see cref="EntranceRunner"/>).
/// Whoever runs it owns the cleanup, and only the run that is still current may clear the target.
/// </summary>
/// <remarks>
/// Key frames target <c>Opacity</c> and <c>TranslateTransform.Y</c> — never <c>RenderTransform</c>
/// itself, which has no key-frame animator and crashes before first paint (RemEx-qolhg). The curve is
/// a key spline on the end frame, so the delay and duration mean exactly what they say.
/// </remarks>
internal static class EntranceAnimation
{
    /// <summary>Builds the animation. Internal so tests can inspect it.</summary>
    /// <param name="duration">How long the arrival takes.</param>
    /// <param name="delay">How long to hold the target invisible first (a stagger).</param>
    /// <param name="offset">How far below its resting place the target starts, in DIPs.</param>
    internal static Animation Build(TimeSpan duration, TimeSpan delay, double offset) => new()
    {
        Duration = duration,
        Delay = delay,
        // Backward: hold the first frame (invisible, offset) through the delay. Not Forward: the end
        // values are the resting ones, and the runner clears both properties afterwards anyway.
        FillMode = FillMode.Backward,
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 0d),
                    new Setter(TranslateTransform.YProperty, offset),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1d),
                KeySpline = Motion.EmphasizedDecelerateSpline(),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 1d),
                    new Setter(TranslateTransform.YProperty, 0d),
                },
            },
        },
    };

    /// <summary>Removes whatever the entrance left on <paramref name="target"/>.</summary>
    internal static void Clear(Control target)
    {
        target.ClearValue(Visual.OpacityProperty);
        target.ClearValue(Visual.RenderTransformProperty);
    }
}
