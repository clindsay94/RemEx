using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;

namespace Remex.Desktop.Views;

/// <summary>
/// Wraps a page transition so that interrupting one cannot strand a page off-screen or transparent.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12.1.1's <c>TransitioningContentControl</c> cancels the in-flight transition whenever new
/// content arrives (<c>ArrangeOverride</c> calls <c>_currentTransition?.Cancel()</c>). Both
/// <see cref="CrossFade"/> and <see cref="PageSlide"/> handle that cancellation the same way:
/// <c>await Task.WhenAll(tasks); if (cancellationToken.IsCancellationRequested) return;</c> — the
/// early return skips every line that undoes what they applied.
/// </para>
/// <para>
/// Cancelling the underlying animation is not neutral either. It disposes the animation instance,
/// which runs <c>AnimationInstance.Unsubscribed</c> → <c>ApplyFinalFill</c>, and because both
/// transitions default to <see cref="FillMode.Forward"/> that method writes the last interpolated
/// value back onto the target. So the presenter is left frozen exactly where the animation happened
/// to be: translated most of a viewport off-screen, or at a fraction of full opacity. To the user the
/// page transitions and then stays blank (RemEx-yj3x2).
/// </para>
/// <para>
/// It takes two navigations inside one transition's duration, which is why clicking straight through
/// Home → App Launcher → File Transfer reproduces it while a single unhurried navigation never does.
/// </para>
/// <para>
/// <c>ApplyFinalFill</c> writes at local-value priority, so clearing the two properties from here is
/// sufficient — there is no surviving animation-priority value to fight. Both visuals are neutralised
/// rather than only the incoming one: after a cancellation the control decides which presenter is on
/// screen through <c>IsVisible</c>, which makes opacity and render transform pure animation artefacts
/// on both sides, and a stale value on either is a page the user cannot see. A successor transition
/// re-animates from this clean base — its first keyframe sets an explicit value at cue 0 — so
/// nothing flashes.
/// </para>
/// </remarks>
/// <param name="inner">The transition to guard.</param>
internal sealed class InterruptSafePageTransition(IPageTransition inner) : IPageTransition
{
    /// <summary>The guarded transition. Exposed so tests can assert what was wrapped.</summary>
    internal IPageTransition Inner { get; } = inner;

    /// <inheritdoc />
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            await Inner.Start(from, to, forward, cancellationToken);
            completed = !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            // Covers the faulted path as well as the cancelled one. PageSlide.GetVisualParent throws
            // when the two presenters have drifted apart in the visual tree, and an exception leaves
            // exactly the same half-applied state behind as a cancellation does.
            if (!completed)
            {
                Neutralise(from);
                Neutralise(to);
            }
        }
    }

    private static void Neutralise(Visual? visual)
    {
        if (visual is null)
        {
            return;
        }

        // Cleared rather than set, so a presenter that has been through a cancelled transition does
        // not carry a local value that outranks a style or binding for the rest of its life.
        visual.ClearValue(Visual.RenderTransformProperty);
        visual.ClearValue(Visual.OpacityProperty);
    }
}
