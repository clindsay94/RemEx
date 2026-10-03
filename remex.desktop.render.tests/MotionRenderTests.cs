using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAssertions;
using Remex.Desktop.Controls;
using Remex.Desktop.Styles;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The motion pass end to end on a real (headless) UI thread (RemEx-pp4cm.1): a transition on the
/// Motion tokens collapses to instant the moment Reduced motion is switched on, with no rebuild, and
/// <see cref="ListEntrance"/> animates rows that arrive but not a reload.
/// </summary>
/// <remarks>
/// These flip the process-wide <see cref="Motion.IsReducedMotion"/> flag, which remex.desktop.tests
/// deliberately never does (its ShellViewModel tests write it from parallel threads). Here every test
/// runs on the one headless dispatcher thread, and each restores the flag in <c>finally</c>.
/// </remarks>
public sealed class MotionRenderTests
{
    [AvaloniaFact]
    public void ATokenTransitionIsInstantUnderReducedMotionAndAnimatesWithoutIt_LiveWithNoRebuild()
    {
        try
        {
            Motion.SetReducedMotion(false);
            var border = new Border
            {
                Width = 100,
                Height = 100,
                Background = Brushes.Red,
                Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = Motion.Enter,
                        Easing = Motion.Standard,
                    },
                },
            };
            var window = new Window { Width = 200, Height = 200, Content = border };
            window.Show();
            Pump();

            // Deliberately NO frame is pumped after each change below. A transition publishes its
            // progress-0 value the moment it starts, eased; that first value is all these need, and
            // it keeps the test off the headless render timer, which a preceding render test can
            // leave stalled (measured: a 20 s fade not moving for 5 s after SliderFocusRingRenderTests).

            // Motion on: the fade starts where the property was. Nothing has jumped.
            border.Opacity = 0d;
            border.Opacity.Should().Be(1d, "with motion on, the fade begins from the old value");

            // The same transition object, untouched: switching the preference is all it takes.
            Motion.SetReducedMotion(true);
            border.Opacity = 0.5d;
            border.Opacity.Should().Be(0.5d, "under reduced motion the very first value is the end value");

            border.Opacity = 1d;
            border.Opacity.Should().Be(1d);

            // And back: motion on again, the next change animates from where it was.
            Motion.SetReducedMotion(false);
            border.Opacity = 0d;
            border.Opacity.Should().Be(1d, "turning reduced motion off restores the fade, live");
        }
        finally
        {
            Motion.SetReducedMotion(false);
        }
    }

    [AvaloniaFact]
    public void ARowAddedToAListOnScreenFadesIn()
    {
        try
        {
            Motion.SetReducedMotion(false);
            var (window, list, items) = NewList(initialCount: 3);

            items.Add("new row");
            window.UpdateLayout();
            Pump();

            var container = list.ContainerFromIndex(items.Count - 1);
            container.Should().NotBeNull();
            container!.Opacity.Should().BeLessThan(1d, "the new row starts invisible and fades up");

            list.ContainerFromIndex(0)!.Opacity.Should().Be(1d, "rows that were already there do not move");
        }
        finally
        {
            Motion.SetReducedMotion(false);
        }
    }

    [AvaloniaFact]
    public void ABulkReloadAppearsAtOnce()
    {
        try
        {
            Motion.SetReducedMotion(false);
            var (window, list, items) = NewList(initialCount: 0);

            // A Clear-then-Add-each refresh: a Reset followed by more rows than the cap.
            items.Clear();
            for (var i = 0; i < Motion.MaxAnimatedBatch + 5; i++)
            {
                items.Add($"row {i}");
            }

            window.UpdateLayout();
            Pump();

            Enumerable.Range(0, items.Count)
                .Select(i => list.ContainerFromIndex(i))
                .Where(c => c is not null)
                .Should().NotBeEmpty()
                .And.OnlyContain(c => c!.Opacity == 1d, "a reload is not rows appearing");
        }
        finally
        {
            Motion.SetReducedMotion(false);
        }
    }

    [AvaloniaFact]
    public void NoRowAnimatesUnderReducedMotion()
    {
        try
        {
            Motion.SetReducedMotion(true);
            var (window, list, items) = NewList(initialCount: 2);

            items.Add("new row");
            window.UpdateLayout();
            Pump();

            list.ContainerFromIndex(items.Count - 1)!.Opacity.Should().Be(1d);
        }
        finally
        {
            Motion.SetReducedMotion(false);
        }
    }

    private static (Window Window, ItemsControl List, ObservableCollection<string> Items) NewList(int initialCount)
    {
        var items = new ObservableCollection<string>(Enumerable.Range(0, initialCount).Select(i => $"seed {i}"));
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, Height = 20 }),
        };
        ListEntrance.SetIsEnabled(list, true);

        var window = new Window { Width = 300, Height = 800, Content = list };
        window.Show();
        Pump();
        return (window, list, items);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
