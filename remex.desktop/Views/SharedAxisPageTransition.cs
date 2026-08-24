using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace Remex.Desktop.Views;

/// <summary>
/// The axis a <see cref="SharedAxisPageTransition"/> travels along.
/// </summary>
internal enum SharedAxis
{
    /// <summary>Left and right — the default, and what the shell's sidebar navigation uses.</summary>
    Horizontal,

    /// <summary>Up and down.</summary>
    Vertical,
}

/// <summary>
/// Material's shared-axis transition: the outgoing page fades out while sliding a short distance
/// along one axis, and the incoming page slides in from the opposite side as it fades up.
/// </summary>
/// <remarks>
/// <para>
/// The short distance is the point. Avalonia's <see cref="PageSlide"/> translates by the whole
/// viewport, which reads as two separate screens being shoved past each other; shared axis moves by
/// <see cref="DefaultOffset"/> device-independent pixels, so the movement says "this page came from
/// over there" without the page ever appearing to leave. Material's own figure is 30dp and this
/// keeps it.
/// </para>
/// <para>
/// The two halves do not overlap. The outgoing page has faded out by <see cref="FadeCue"/> of the
/// duration and the incoming page only starts to appear after it, which is what stops the two pages
/// being legible on top of each other mid-navigation. Both use Material's standard easing curve —
/// fast to start, settling at the end — rather than a linear ramp.
/// </para>
/// <para>
/// Material.Avalonia does not ship this: its <c>TransitionAssist</c> only turns a control's own
/// transitions off. Hence the local implementation.
/// </para>
/// <para>
/// Cleanup clears the render transform on both presenters and leaves their visibility to
/// <c>TransitioningContentControl</c>, which owns it — <c>UpdateContent</c> shows the incoming one
/// and <c>HideOldPresenter</c> hides the outgoing one from this transition's continuation. As with
/// Avalonia's own transitions the cleanup is skipped when the token is cancelled, so this type is
/// expected to be wrapped in <see cref="InterruptSafePageTransition"/>.
/// </para>
/// </remarks>
internal sealed class SharedAxisPageTransition : IPageTransition
{
    /// <summary>How far a page travels, in device-independent pixels. Material's figure is 30dp.</summary>
    internal const double DefaultOffset = 30d;

    /// <summary>
    /// The point in the transition where the outgoing page has finished fading out and the incoming
    /// one begins to fade in.
    /// </summary>
    internal const double FadeCue = 0.3d;

    /// <summary>
    /// Material's standard easing — accelerates away immediately and decelerates into place.
    /// </summary>
    /// <remarks>
    /// Carried on the key frames rather than on the animation, and that is not a style choice.
    /// Avalonia eases the animation's global progress and then looks the key-frame segment up with
    /// the *eased* value (<c>AnimationInstance</c> eases, then <c>Animator.GetKeyFrames</c> compares
    /// against <c>Cue.CueValue</c>), so an animation-level easing makes every cue a position on the
    /// curve instead of a fraction of the duration. Under this curve <see cref="FadeCue"/> would
    /// land at 14% of the run rather than 30%, compressing the outgoing fade into the first ~27ms of
    /// a 200ms navigation — the old page popping out instead of crossing over. A key spline is
    /// applied to the segment's own progress, after the cue lookup, which leaves the cues meaning
    /// what they say.
    /// </remarks>
    private static KeySpline StandardEasing => new(0.2d, 0d, 0d, 1d);

    /// <param name="duration">How long the whole transition runs.</param>
    /// <param name="axis">The axis to travel along.</param>
    /// <param name="offset">How far to travel, in device-independent pixels.</param>
    internal SharedAxisPageTransition(
        TimeSpan duration,
        SharedAxis axis = SharedAxis.Horizontal,
        double offset = DefaultOffset)
    {
        Duration = duration;
        Axis = axis;
        Offset = offset;
    }

    /// <summary>How long the whole transition runs.</summary>
    internal TimeSpan Duration { get; }

    /// <summary>The axis the pages travel along.</summary>
    internal SharedAxis Axis { get; }

    /// <summary>How far the pages travel, in device-independent pixels.</summary>
    internal double Offset { get; }

    /// <summary>The transform property this axis animates.</summary>
    private AvaloniaProperty TranslateProperty =>
        Axis == SharedAxis.Horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;

    /// <inheritdoc />
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tasks = new List<Task>(2);

        if (from != null)
        {
            tasks.Add(BuildOutgoing(forward).RunAsync(from, cancellationToken));
        }

        if (to != null)
        {
            tasks.Add(BuildIncoming(forward).RunAsync(to, cancellationToken));
        }

        await Task.WhenAll(tasks);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The outgoing page keeps the opacity the animation left it at, deliberately. This runs
        // BEFORE the task completes, and therefore before TransitioningContentControl's continuation
        // hides that presenter - so restoring it to 1 here puts a fully opaque copy of the old page,
        // back at zero translation, on top of the new one for that gap. Nothing composes a frame in
        // it today, but only because of dispatcher priority ordering nothing here controls. Leaving
        // it faded out is safe because every animation sets opacity explicitly at cue 0, so the next
        // use of this presenter starts from a value of its own choosing either way.
        from?.ClearValue(Visual.RenderTransformProperty);

        if (to != null)
        {
            to.ClearValue(Visual.RenderTransformProperty);
            to.ClearValue(Visual.OpacityProperty);
        }
    }

    /// <summary>
    /// The outgoing page's animation: full opacity to nothing over the first <see cref="FadeCue"/>
    /// of the duration, travelling against the direction of navigation throughout.
    /// </summary>
    /// <param name="forward">True when navigating to a later page in the sidebar order.</param>
    internal Animation BuildOutgoing(bool forward) => new()
    {
        Duration = Duration,
        FillMode = FillMode.Forward,
        Children =
        {
            KeyFrameAt(0d, translate: 0d, opacity: 1d),
            KeyFrameAt(FadeCue, opacity: 0d),
            KeyFrameAt(1d, translate: forward ? -Offset : Offset, opacity: 0d),
        },
    };

    /// <summary>
    /// The incoming page's animation: held invisible until the outgoing one has gone, then fading up
    /// as it settles in from the direction of navigation.
    /// </summary>
    /// <param name="forward">True when navigating to a later page in the sidebar order.</param>
    internal Animation BuildIncoming(bool forward) => new()
    {
        Duration = Duration,
        FillMode = FillMode.Forward,
        Children =
        {
            KeyFrameAt(0d, translate: forward ? Offset : -Offset, opacity: 0d),
            KeyFrameAt(FadeCue, opacity: 0d),
            KeyFrameAt(1d, translate: 0d, opacity: 1d),
        },
    };

    /// <summary>Builds one key frame, setting only the properties that were given a value.</summary>
    private KeyFrame KeyFrameAt(double cue, double? translate = null, double? opacity = null)
    {
        // The spline eases the segment that ENDS at this frame, so the frame at cue 0 does not need
        // one - nothing runs into it.
        var frame = new KeyFrame { Cue = new Cue(cue) };

        if (cue > 0d)
        {
            frame.KeySpline = StandardEasing;
        }

        if (translate.HasValue)
        {
            frame.Setters.Add(new Setter(TranslateProperty, translate.Value));
        }

        if (opacity.HasValue)
        {
            frame.Setters.Add(new Setter(Visual.OpacityProperty, opacity.Value));
        }

        return frame;
    }
}
