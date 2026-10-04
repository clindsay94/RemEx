using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Controls;
using Remex.Desktop.Styles;
using Xunit;

namespace Remex.Desktop.Tests.Styles;

/// <summary>
/// The PC motion tokens (RemEx-pp4cm.1): M3's curves and durations in one place, and the reduced
/// motion collapse that makes every transition using them instant rather than merely faster.
/// </summary>
/// <remarks>
/// Nothing here touches the process-wide <see cref="Motion.IsReducedMotion"/> flag: ShellViewModel
/// tests running in parallel write it. The easing is exercised through its injectable source instead,
/// and the live flag end to end in remex.desktop.render.tests (MotionRenderTests), which runs on a
/// single UI thread.
/// </remarks>
public class MotionTests
{
    [Fact]
    public void TheDurationScaleIsMaterial3s()
    {
        Motion.Short1.Should().Be(TimeSpan.FromMilliseconds(50));
        Motion.Short2.Should().Be(TimeSpan.FromMilliseconds(100));
        Motion.Short3.Should().Be(TimeSpan.FromMilliseconds(150));
        Motion.Short4.Should().Be(TimeSpan.FromMilliseconds(200));
        Motion.Medium1.Should().Be(TimeSpan.FromMilliseconds(250));
        Motion.Medium2.Should().Be(TimeSpan.FromMilliseconds(300));
        Motion.Medium3.Should().Be(TimeSpan.FromMilliseconds(350));
        Motion.Medium4.Should().Be(TimeSpan.FromMilliseconds(400));
        Motion.Long1.Should().Be(TimeSpan.FromMilliseconds(450));
        Motion.Long2.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void EnterAndExitAreTheBriefsFigures()
    {
        Motion.Enter.Should().Be(TimeSpan.FromMilliseconds(400), "M3 emphasized-decelerate enter");
        Motion.Exit.Should().Be(TimeSpan.FromMilliseconds(200), "M3 emphasized-accelerate exit");
        Motion.InPlace.Should().Be(TimeSpan.FromMilliseconds(300), "M3 standard in-place change");
    }

    [Fact]
    public void StateFeedbackIsShortSoItNeverTrailsTheInput()
    {
        Motion.StateChange.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100))
            .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        Motion.Selection.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200));
    }

    [Theory]
    [InlineData(nameof(Motion.Standard), 0.2, 0.0, 0.0, 1.0)]
    [InlineData(nameof(Motion.StandardDecelerate), 0.0, 0.0, 0.0, 1.0)]
    [InlineData(nameof(Motion.StandardAccelerate), 0.3, 0.0, 1.0, 1.0)]
    [InlineData(nameof(Motion.EmphasizedDecelerate), 0.05, 0.7, 0.1, 1.0)]
    [InlineData(nameof(Motion.EmphasizedAccelerate), 0.3, 0.0, 0.8, 0.15)]
    public void EachEasingIsTheM3CubicBezier(string name, double x1, double y1, double x2, double y2)
    {
        var easing = typeof(Motion).GetField(name)!.GetValue(null).Should().BeOfType<MotionEasing>().Subject;

        easing.X1.Should().Be(x1);
        easing.Y1.Should().Be(y1);
        easing.X2.Should().Be(x2);
        easing.Y2.Should().Be(y2);
    }

    [Fact]
    public void TheKeySplinesMatchTheEasings()
    {
        var decel = Motion.EmphasizedDecelerateSpline();
        (decel.ControlPointX1, decel.ControlPointY1, decel.ControlPointX2, decel.ControlPointY2)
            .Should().Be((0.05, 0.7, 0.1, 1.0));

        var accel = Motion.EmphasizedAccelerateSpline();
        (accel.ControlPointX1, accel.ControlPointY1, accel.ControlPointX2, accel.ControlPointY2)
            .Should().Be((0.3, 0.0, 0.8, 0.15));

        Motion.StandardSpline().Should().NotBeSameAs(Motion.StandardSpline(),
            "key splines are mutable, so every caller gets its own");
    }

    [Fact]
    public void UnderReducedMotionAnEasingIsAlreadyFinishedFromTheFirstFrame()
    {
        var easing = new MotionEasing(0.2, 0, 0, 1, () => true);

        easing.Ease(0d).Should().Be(1d, "instant: the property is at its end value on frame one");
        easing.Ease(0.25d).Should().Be(1d);
        easing.Ease(1d).Should().Be(1d);
    }

    [Fact]
    public void WithoutReducedMotionAnEasingFollowsItsCurve()
    {
        var easing = new MotionEasing(0.2, 0, 0, 1, () => false);

        easing.Ease(0d).Should().BeApproximately(0d, 1e-6);
        easing.Ease(1d).Should().BeApproximately(1d, 1e-6);
        // Standard decelerates: well past halfway by the midpoint of the run.
        easing.Ease(0.5d).Should().BeGreaterThan(0.5d).And.BeLessThan(1d);
    }

    [Fact]
    public void TheReducedMotionSourceIsReadOnEveryFrameSoTheSwitchIsLive()
    {
        var reduced = false;
        var easing = new MotionEasing(0.2, 0, 0, 1, () => reduced);

        easing.Ease(0d).Should().BeApproximately(0d, 1e-6);
        reduced = true;
        easing.Ease(0d).Should().Be(1d, "flipping the preference takes effect without rebuilding anything");
        reduced = false;
        easing.Ease(0d).Should().BeApproximately(0d, 1e-6);
    }

    [Fact]
    public void ResolveCollapsesToZeroUnderReducedMotion()
    {
        Motion.Resolve(Motion.Enter, reducedMotion: true).Should().Be(TimeSpan.Zero);
        Motion.Resolve(Motion.Enter, reducedMotion: false).Should().Be(Motion.Enter);
    }

    [Fact]
    public void TheStaggerIsAFixedStepCappedShortAndZeroUnderReducedMotion()
    {
        Motion.StaggerDelay(0, false).Should().Be(TimeSpan.Zero);
        Motion.StaggerDelay(1, false).Should().Be(Motion.StaggerStep);
        Motion.StaggerDelay(3, false).Should().Be(Motion.StaggerStep * 3);

        var cap = Motion.StaggerStep * Motion.MaxStaggerSteps;
        Motion.StaggerDelay(Motion.MaxStaggerSteps, false).Should().Be(cap);
        Motion.StaggerDelay(500, false).Should().Be(cap, "a long burst never waits longer than the cap");
        cap.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200));

        Motion.StaggerDelay(3, true).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(1, false, true, true)]
    [InlineData(Motion.MaxAnimatedBatch, false, true, true)]
    [InlineData(Motion.MaxAnimatedBatch + 1, false, true, false)] // a reload, not rows appearing
    [InlineData(0, false, true, false)]
    [InlineData(1, true, true, false)]  // reduced motion
    [InlineData(1, false, false, false)] // window hidden or minimised
    public void OnlyASmallBurstOnAVisibleWindowAnimates(int batch, bool reduced, bool visible, bool expected)
    {
        Motion.ShouldAnimateBatch(batch, reduced, visible).Should().Be(expected);
    }

    [Fact]
    public void TheListEntranceArrivesOnEmphasizedDecelerateAndHoldsItsStaggerInvisible()
    {
        var animation = ListEntrance.BuildEntrance(Motion.StaggerDelay(2, false));

        animation.Duration.Should().Be(Motion.ListItemEnter);
        animation.Delay.Should().Be(Motion.StaggerStep * 2);
        animation.FillMode.Should().Be(Avalonia.Animation.FillMode.Backward,
            "the backward fill keeps a staggered row invisible until its turn");

        var end = animation.Children.Single(f => f.Cue.CueValue == 1d);
        end.KeySpline.Should().NotBeNull();
        (end.KeySpline!.ControlPointX1, end.KeySpline.ControlPointY1).Should().Be((0.05, 0.7));

        // Never RenderTransform itself: no key-frame animator, crashes before first paint (RemEx-qolhg).
        animation.Children.SelectMany(f => f.Setters).OfType<Avalonia.Styling.Setter>()
            .Select(s => s.Property).Should().NotContain(Avalonia.Visual.RenderTransformProperty)
            .And.Contain(Avalonia.Media.TranslateTransform.YProperty);
    }

    /// <summary>
    /// The "one place" guard: no transition in remex.desktop spells its own curve or timing.
    /// </summary>
    [Fact]
    public void NoTransitionInTheDesktopXamlCarriesALiteralCurveOrDuration()
    {
        // Allowlisted, each for a reason that is not "nobody got round to it":
        //  - RemoteDesktopView: the stream page is regression-guarded; its crosshair fade is left alone.
        //  - CommandPaletteWindow: CommandPaletteMaterialSurfaceTests pins its open durations under
        //    150 ms as literals (its easing IS tokenised).
        var allow = new[] { "RemoteDesktopView.axaml", "CommandPaletteWindow.axaml" };

        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty("anti-vacuity: the scan must actually find the desktop XAML");

        var transitions = 0;
        foreach (var file in files)
        {
            var xaml = StripComments(File.ReadAllText(file));
            var name = Path.GetFileName(file);

            xaml.Should().NotContain("CubicEaseOut", $"{name} should use a Motion easing token");
            xaml.Should().NotContain("SpringEasing", $"{name}: the PC has no bounce");

            foreach (Match m in Regex.Matches(xaml, @"<(\w+Transition|CrossFade)\s[^>]*>"))
            {
                transitions++;
                if (allow.Contains(name))
                {
                    continue;
                }

                m.Value.Should().NotMatchRegex(@"Duration=""\d", $"{name}: {m.Value} should take its Duration from Motion");
                m.Value.Should().Contain("motion:Motion.", $"{name}: {m.Value} should use Motion tokens");
            }
        }

        transitions.Should().BeGreaterThan(20, "anti-vacuity: the regex must actually match the app's transitions");
    }

    private static string StripComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
