using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAssertions;
using Remex.Desktop.Views;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Covers RemEx-yzu5m: the shell's page transition is Material's shared axis rather than a random
/// pick between a full-viewport slide and a plain cross-fade.
/// </summary>
/// <remarks>
/// <para>
/// These assert the animations the transition builds, not the pixels it produces. This assembly has
/// no <c>Avalonia.Headless</c> reference, so there is no UI thread to run an animation on and no
/// renderer to read a result from — but everything the bead asks for is decided before the animation
/// starts: how far a page travels, in which direction, when each half fades, and for how long. That
/// is what is pinned here.
/// </para>
/// <para>
/// The one thing these cannot show is what it looks like, which is why the bead was also checked by
/// running the app.
/// </para>
/// </remarks>
public class SharedAxisPageTransitionTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void TheOutgoingPageTravelsAgainstTheNavigationAndIsGoneBeforeTheIncomingOneArrives()
    {
        var animation = NewTransition().BuildOutgoing(forward: true);

        Translate(animation, 0d).Should().Be(0d);
        Opacity(animation, 0d).Should().Be(1d);

        // Faded out by the crossover point, and it stays out for the rest of the run.
        Opacity(animation, SharedAxisPageTransition.FadeCue).Should().Be(0d);
        Opacity(animation, 1d).Should().Be(0d);

        // Forward navigation pushes the old page left.
        Translate(animation, 1d).Should().Be(-SharedAxisPageTransition.DefaultOffset);
    }

    [Fact]
    public void TheIncomingPageArrivesFromTheDirectionOfTravelAndOnlyFadesUpAfterTheCrossover()
    {
        var animation = NewTransition().BuildIncoming(forward: true);

        Translate(animation, 0d).Should().Be(SharedAxisPageTransition.DefaultOffset);
        Opacity(animation, 0d).Should().Be(0d);

        // Invisible until the outgoing page has gone - the two are never legible at once.
        Opacity(animation, SharedAxisPageTransition.FadeCue).Should().Be(0d);

        Translate(animation, 1d).Should().Be(0d);
        Opacity(animation, 1d).Should().Be(1d);
    }

    [Fact]
    public void NavigatingBackwardsReversesBothHalves()
    {
        var transition = NewTransition();

        Translate(transition.BuildOutgoing(forward: false), 1d)
            .Should().Be(SharedAxisPageTransition.DefaultOffset);
        Translate(transition.BuildIncoming(forward: false), 0d)
            .Should().Be(-SharedAxisPageTransition.DefaultOffset);
    }

    [Fact]
    public void TheTravelIsAShortOffsetRatherThanAWholeViewport()
    {
        // The distinction between a shared-axis transition and PageSlide, which translates by the
        // full viewport width. 30dp is Material's figure.
        SharedAxisPageTransition.DefaultOffset.Should().Be(30d);
    }

    [Fact]
    public void TheVerticalAxisAnimatesTheOtherTranslateProperty()
    {
        var horizontal = NewTransition(SharedAxis.Horizontal).BuildOutgoing(forward: true);
        var vertical = NewTransition(SharedAxis.Vertical).BuildOutgoing(forward: true);

        SetterProperties(horizontal).Should().Contain(TranslateTransform.XProperty)
            .And.NotContain(TranslateTransform.YProperty);
        SetterProperties(vertical).Should().Contain(TranslateTransform.YProperty)
            .And.NotContain(TranslateTransform.XProperty);
    }

    [Fact]
    public void BothHalvesRunForTheRequestedDurationOnMaterialsStandardEasing()
    {
        var transition = NewTransition();

        foreach (var animation in new[] { transition.BuildOutgoing(true), transition.BuildIncoming(true) })
        {
            animation.Duration.Should().Be(Duration);

            // Forward fill: the last interpolated value stays applied until the transition clears it,
            // so a page cannot flicker back to its starting offset between the animation ending and
            // the cleanup running.
            animation.FillMode.Should().Be(FillMode.Forward);
        }
    }

    [Fact]
    public void TheEasingIsOnTheKeyFramesSoThatACueStillMeansAFractionOfTheDuration()
    {
        // Avalonia eases the animation's global progress and THEN picks the key-frame segment by
        // comparing that eased value against the cue, so an animation-level easing silently moves
        // every cue: under Material's curve the crossover at 0.3 would land at 14% of the run, and
        // the outgoing fade would be over in the first ~27ms of a 200ms navigation. A key spline is
        // applied to the segment's own progress instead, after the lookup.
        var animation = NewTransition().BuildOutgoing(forward: true);

        animation.Easing.Should().BeOfType<LinearEasing>();

        // Every segment is eased - all but the frame at cue 0, which nothing runs into.
        foreach (var frame in animation.Children)
        {
            if (frame.Cue.CueValue > 0d)
            {
                frame.KeySpline.Should().NotBeNull();
            }
        }

        animation.Children.Count(f => f.KeySpline != null).Should().Be(animation.Children.Count - 1);
    }

    [Fact]
    public void AnAlreadyCancelledTransitionDoesNothingAtAll()
    {
        // Deliberate: like Avalonia's own transitions this one leaves a cancelled run's state alone,
        // which is why the shell wraps it in InterruptSafePageTransition.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var presenter = new ContentPresenter { Opacity = 0.4d, RenderTransform = new TranslateTransform(17d, 0d) };

        var running = NewTransition().Start(presenter, null, forward: true, cancelled.Token);

        running.IsCompletedSuccessfully.Should().BeTrue();
        presenter.Opacity.Should().Be(0.4d);
        presenter.RenderTransform.Should().BeOfType<TranslateTransform>();
    }

    [Fact]
    public void TheShellNavigatesOnTheSharedAxisAndGuardsIt()
    {
        var transition = ShellView.NewPageTransition(reducedMotion: false);

        transition.Should().BeOfType<InterruptSafePageTransition>()
            .Which.Inner.Should().BeOfType<SharedAxisPageTransition>();
    }

    [Fact]
    public void ReducedMotionRemovesTheTravelRatherThanShorteningIt()
    {
        // A shortened slide is still a slide. What is left for someone who asked for less motion is
        // the fade, which is the standard accommodation - not a faster version of the same movement.
        var transition = ShellView.NewPageTransition(reducedMotion: true);

        transition.Should().BeOfType<InterruptSafePageTransition>()
            .Which.Inner.Should().BeOfType<CrossFade>();
    }

    private static SharedAxisPageTransition NewTransition(SharedAxis axis = SharedAxis.Horizontal) =>
        new(Duration, axis);

    /// <summary>The translate value set at <paramref name="cue"/>, or null if that frame sets none.</summary>
    private static double? Translate(Animation animation, double cue) =>
        ValueAt(animation, cue, p => p == TranslateTransform.XProperty || p == TranslateTransform.YProperty);

    /// <summary>The opacity set at <paramref name="cue"/>, or null if that frame sets none.</summary>
    private static double? Opacity(Animation animation, double cue) =>
        ValueAt(animation, cue, p => p == Visual.OpacityProperty);

    private static double? ValueAt(Animation animation, double cue, Func<AvaloniaProperty, bool> matches)
    {
        var frame = animation.Children.Single(f => Math.Abs(f.Cue.CueValue - cue) < 0.0001d);

        return frame.Setters
            .OfType<Setter>()
            .Where(s => s.Property != null && matches(s.Property))
            .Select(s => (double?)s.Value)
            .SingleOrDefault();
    }

    private static AvaloniaProperty[] SetterProperties(Animation animation) =>
        animation.Children
            .SelectMany(f => f.Setters.OfType<Setter>())
            .Select(s => s.Property!)
            .ToArray();
}
