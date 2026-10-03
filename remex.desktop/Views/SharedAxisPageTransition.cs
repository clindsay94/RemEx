using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Desktop.Styles;

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
/// M3 TIMING (RemEx-pp4cm.1). The two halves run on different clocks and curves, as M3 specifies
/// for a transition between two screens: the outgoing page leaves over <see cref="Motion.Exit"/>
/// (200 ms) on emphasized-accelerate, and the incoming page arrives over <see cref="Motion.Enter"/>
/// (400 ms) on emphasized-decelerate. They still do not overlap: the outgoing page is fully faded by
/// <see cref="ExitFadeCue"/> of its run, and the incoming animation is delayed until exactly that
/// moment (<see cref="IncomingDelay"/>), holding its first frame (invisible, offset) through the
/// delay. So the two pages are never legible on top of each other, and the whole navigation settles
/// in <see cref="TotalDuration"/>.
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
    /// The point in the OUTGOING animation where that page has finished fading out. The incoming
    /// page starts at this moment and not before.
    /// </summary>
    internal const double ExitFadeCue = 0.5d;

    /// <summary>
    /// The easing curves are carried on the key frames rather than on the animation, and that is not
    /// a style choice.
    /// </summary>
    /// <remarks>
    /// Avalonia eases the animation's global progress and then looks the key-frame segment up with
    /// the *eased* value (<c>AnimationInstance</c> eases, then <c>Animator.GetKeyFrames</c> compares
    /// against <c>Cue.CueValue</c>), so an animation-level easing makes every cue a position on the
    /// curve instead of a fraction of the duration. Under emphasized-accelerate
    /// <see cref="ExitFadeCue"/> would land well past the middle of the run, leaving the old page
    /// legible after the new one has started. A key spline is applied to the segment's own progress,
    /// after the cue lookup, which leaves the cues meaning what they say.
    /// </remarks>
    private static KeySpline ExitSpline => Motion.EmphasizedAccelerateSpline();

    private static KeySpline EnterSpline => Motion.EmphasizedDecelerateSpline();

    /// <summary>The shell's transition: M3's enter and exit durations.</summary>
    /// <param name="axis">The axis to travel along.</param>
    /// <param name="offset">How far to travel, in device-independent pixels.</param>
    internal SharedAxisPageTransition(SharedAxis axis = SharedAxis.Horizontal, double offset = DefaultOffset)
        : this(Motion.Enter, Motion.Exit, axis, offset)
    {
    }

    /// <param name="enterDuration">How long the incoming page takes to arrive.</param>
    /// <param name="exitDuration">How long the outgoing page takes to leave.</param>
    /// <param name="axis">The axis to travel along.</param>
    /// <param name="offset">How far to travel, in device-independent pixels.</param>
    internal SharedAxisPageTransition(
        TimeSpan enterDuration,
        TimeSpan exitDuration,
        SharedAxis axis = SharedAxis.Horizontal,
        double offset = DefaultOffset)
    {
        EnterDuration = enterDuration;
        ExitDuration = exitDuration;
        Axis = axis;
        Offset = offset;
    }

    /// <summary>How long the incoming page takes to arrive, after <see cref="IncomingDelay"/>.</summary>
    internal TimeSpan EnterDuration { get; }

    /// <summary>How long the outgoing page takes to leave.</summary>
    internal TimeSpan ExitDuration { get; }

    /// <summary>When the incoming page starts: the moment the outgoing one has faded out.</summary>
    internal TimeSpan IncomingDelay => ExitDuration * ExitFadeCue;

    /// <summary>From the navigation to the incoming page settling.</summary>
    internal TimeSpan TotalDuration
    {
        get
        {
            var incomingEnd = IncomingDelay + EnterDuration;
            return incomingEnd > ExitDuration ? incomingEnd : ExitDuration;
        }
    }

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
    /// The outgoing page's animation: full opacity to nothing by <see cref="ExitFadeCue"/>, travelling
    /// against the direction of navigation for the whole <see cref="ExitDuration"/>, all on
    /// emphasized-accelerate.
    /// </summary>
    /// <param name="forward">True when navigating to a later page in the sidebar order.</param>
    internal Animation BuildOutgoing(bool forward) => new()
    {
        Duration = ExitDuration,
        FillMode = FillMode.Forward,
        Children =
        {
            KeyFrameAt(0d, ExitSpline, translate: 0d, opacity: 1d),
            KeyFrameAt(ExitFadeCue, ExitSpline, opacity: 0d),
            KeyFrameAt(1d, ExitSpline, translate: forward ? -Offset : Offset, opacity: 0d),
        },
    };

    /// <summary>
    /// The incoming page's animation: held invisible and offset through <see cref="IncomingDelay"/>
    /// (the backward fill), then fading up as it settles in from the direction of navigation over
    /// <see cref="EnterDuration"/> on emphasized-decelerate.
    /// </summary>
    /// <param name="forward">True when navigating to a later page in the sidebar order.</param>
    internal Animation BuildIncoming(bool forward) => new()
    {
        Duration = EnterDuration,
        Delay = IncomingDelay,
        FillMode = FillMode.Both,
        Children =
        {
            KeyFrameAt(0d, EnterSpline, translate: forward ? Offset : -Offset, opacity: 0d),
            KeyFrameAt(1d, EnterSpline, translate: 0d, opacity: 1d),
        },
    };

    /// <summary>Builds one key frame, setting only the properties that were given a value.</summary>
    private KeyFrame KeyFrameAt(double cue, KeySpline spline, double? translate = null, double? opacity = null)
    {
        // The spline eases the segment that ENDS at this frame, so the frame at cue 0 does not need
        // one - nothing runs into it.
        var frame = new KeyFrame { Cue = new Cue(cue) };

        if (cue > 0d)
        {
            frame.KeySpline = spline;
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

/// <summary>
/// A page transition that completes at once: the new page simply replaces the old one.
/// </summary>
/// <remarks>
/// Used for reduced motion and while the window is off screen (RemEx-pp4cm.1). It is a real
/// transition rather than a null <c>PageTransition</c> so that <c>TransitioningContentControl</c>
/// still runs its continuation — hiding the old presenter and raising <c>TransitionCompleted</c>,
/// which is what releases <see cref="PageHostSequencer"/> for the next navigation.
/// </remarks>
internal sealed class InstantPageTransition : IPageTransition
{
    /// <inheritdoc />
    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        // Defensive: a presenter that was mid-animation when motion got reduced must not keep a
        // stale offset or opacity.
        from?.ClearValue(Visual.RenderTransformProperty);
        to?.ClearValue(Visual.RenderTransformProperty);
        to?.ClearValue(Visual.OpacityProperty);
        return Task.CompletedTask;
    }
}
