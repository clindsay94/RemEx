using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Views;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Covers RemEx-yj3x2: navigating again while the shell was still animating used to leave the content
/// area permanently blank.
/// </summary>
/// <remarks>
/// <para>
/// The interesting behaviour lives in Avalonia, not here, so <see cref="StrandingTransition"/> below
/// reproduces the two upstream steps that combine into the bug rather than mocking them away:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>CrossFade.Start</c> and <c>PageSlide.Start</c> (12.1.1) end with
/// <c>await Task.WhenAll(tasks); if (cancellationToken.IsCancellationRequested) return;</c> — a
/// cancelled transition returns before every line that undoes what it applied.
/// </description></item>
/// <item><description>
/// Cancelling disposes the animation, and <c>AnimationInstance.Unsubscribed</c> calls
/// <c>ApplyFinalFill</c>, which writes the last interpolated value back onto the target because both
/// transitions default to <see cref="FillMode.Forward"/>. Cancellation is therefore not neutral: it
/// actively leaves a half-finished slide or fade behind.
/// </description></item>
/// </list>
/// <para>
/// <see cref="TheUnguardedTransitionStrandsThePresenter"/> pins that the fake really does strand a
/// presenter. Without it, the guard tests would pass against a fake that never broke anything.
/// </para>
/// <para>
/// Everything here runs on the calling thread. Avalonia binds an <c>AvaloniaObject</c> to the first
/// thread that touches it and this assembly has no <c>Avalonia.Headless</c> reference to pump a real
/// UI thread, so a continuation resuming on the thread pool fails <c>VerifyAccess</c>. The fake
/// therefore returns already-completed tasks: what the guard reacts to is the state of the token, not
/// the passage of time, so nothing is lost by removing the delay.
/// </para>
/// </remarks>
public class InterruptSafePageTransitionTests
{
    /// <summary>How far off-screen a half-finished horizontal slide leaves the incoming page.</summary>
    private const double StrandedOffset = 420d;

    /// <summary>The opacity a half-finished cross-fade leaves behind.</summary>
    private const double StrandedOpacity = 0.35d;

    [Fact]
    public void TheUnguardedTransitionStrandsThePresenter()
    {
        var (from, to) = NewPresenters();

        var running = new StrandingTransition().Start(from, to, forward: true, Cancelled());

        running.IsCompletedSuccessfully.Should().BeTrue();

        // This is the bug, reproduced: the page the user navigated to sits most of a viewport away
        // and barely opaque, and nothing will move it back.
        to.RenderTransform.Should().NotBeNull();
        to.Opacity.Should().Be(StrandedOpacity);
    }

    [Fact]
    public void CancellingAGuardedTransitionLeavesBothPresentersRenderable()
    {
        var (from, to) = NewPresenters();

        var running = new InterruptSafePageTransition(new StrandingTransition())
            .Start(from, to, forward: true, Cancelled());

        running.IsCompletedSuccessfully.Should().BeTrue();

        to.RenderTransform.Should().BeNull();
        to.Opacity.Should().Be(1d);

        // The outgoing presenter is neutralised too. After a cancellation the control picks which
        // presenter is on screen with IsVisible, so either one can be the one the user ends up looking
        // at, and a stale value on either is a page that cannot be seen.
        from.RenderTransform.Should().BeNull();
        from.Opacity.Should().Be(1d);
    }

    [Fact]
    public void AFaultedTransitionIsRepairedAndStillFaults()
    {
        var (from, to) = NewPresenters();

        var running = new InterruptSafePageTransition(new StrandingTransition { ThrowAfterApplying = true })
            .Start(from, to, forward: true, CancellationToken.None);

        // PageSlide.GetVisualParent throws when the presenters have drifted apart in the visual tree,
        // and that leaves exactly the same half-applied state behind as a cancellation does.
        running.IsFaulted.Should().BeTrue();
        running.Exception!.InnerException.Should().BeOfType<InvalidOperationException>();
        to.RenderTransform.Should().BeNull();
        to.Opacity.Should().Be(1d);
    }

    [Fact]
    public void AnUninterruptedTransitionKeepsItsOwnCleanup()
    {
        var (from, to) = NewPresenters();

        var running = new InterruptSafePageTransition(new StrandingTransition())
            .Start(from, to, forward: true, CancellationToken.None);

        running.IsCompletedSuccessfully.Should().BeTrue();

        // The guard must not undo a completed transition's own work — reviving the outgoing presenter
        // would leave both pages stacked on screen.
        from.IsVisible.Should().BeFalse();
        to.RenderTransform.Should().BeNull();
        to.Opacity.Should().Be(1d);
    }

    [Fact]
    public void TheGuardKeepsAReferenceToWhatItWraps()
    {
        var inner = new CrossFade(TimeSpan.FromMilliseconds(140));

        new InterruptSafePageTransition(inner).Inner.Should().BeSameAs(inner);
    }

    private static (Visual From, Visual To) NewPresenters() => (new Visual(), new Visual());

    private static CancellationToken Cancelled() => new CancellationToken(canceled: true);

    /// <summary>
    /// Stands in for <see cref="CrossFade"/> / <see cref="PageSlide"/>: applies the animated values,
    /// then on cancellation returns without undoing them — exactly as Avalonia 12.1.1 does.
    /// </summary>
    private sealed class StrandingTransition : IPageTransition
    {
        public bool ThrowAfterApplying { get; init; }

        public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
        {
            if (to is not null)
            {
                to.RenderTransform = new TranslateTransform(StrandedOffset, 0);
                to.Opacity = StrandedOpacity;
            }

            if (from is not null)
            {
                from.Opacity = StrandedOpacity;
            }

            if (ThrowAfterApplying)
            {
                return Task.FromException(new InvalidOperationException("Controls for PageSlide must have same parent."));
            }

            // The early return that leaves ApplyFinalFill's values in place.
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (from is not null)
            {
                from.IsVisible = false;
                from.RenderTransform = null;
                from.Opacity = 1d;
            }

            if (to is not null)
            {
                to.RenderTransform = null;
                to.Opacity = 1d;
            }

            return Task.CompletedTask;
        }
    }
}
