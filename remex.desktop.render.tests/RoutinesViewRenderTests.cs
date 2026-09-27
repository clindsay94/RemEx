using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Core.Routines;
using Remex.Desktop.Services.Routines;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The PC Routines page (routines spec §2.3, S4b) against the real App resources: it paints, it has no
/// editing control, the dry-run banner shows, the 900 breakpoint switches between list-detail and one
/// column, and Space on a card toggles its switch (R-UX-48).
/// </summary>
public sealed class RoutinesViewRenderTests
{
    private sealed class Host : IRoutinesHost
    {
        public event EventHandler? Changed;

        public RoutinesHostSnapshot Snapshot { get; set; } = new(false, false, "idle", "session", [], []);

        public List<(string, string, bool)> Disabled { get; } = new();

        public RoutinesHostSnapshot GetSnapshot() => Snapshot;

        public IReadOnlyList<RoutineRun> GetHistory(string? ownerClientId = null, string? routineId = null) => [];

        public Task SetHostPausedAsync(bool paused) => Task.CompletedTask;

        public Task SetDisabledOnPcAsync(string ownerClientId, string routineId, bool disabled)
        {
            Disabled.Add((ownerClientId, routineId, disabled));
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SetBlockedAsync(string ownerClientId, bool blocked) => Task.CompletedTask;

        public Task<RoutineRun> RunNowAsync(string ownerClientId, string routineId, bool presenceConfirmed) =>
            Task.FromResult(new RoutineRun { RunId = "r", Outcome = RoutineRunOutcomes.Running });

        public bool CancelRun(string runId) => false;
    }

    private static Host WithOneRoutine(bool dryRun = false)
    {
        var routine = new Routine
        {
            Id = "r1",
            Name = "Sleep when idle",
            Enabled = true,
            Trigger = new RoutineTrigger { Type = RoutineTriggerTypes.PcIdle, IdleMinutes = 30 },
            Steps = [new RoutineStep { Type = RoutineStepTypes.Power, Verb = RoutinePowerVerbs.Sleep }],
        };
        return new Host
        {
            Snapshot = new RoutinesHostSnapshot(false, dryRun, "idle", "session",
                [new RoutineOwnerView("client-1", "Pixel", false, false, false, 0, [new RoutineHostEntry(routine, false, false)])],
                []),
        };
    }

    private static (Window Window, RoutinesView View, RoutinesViewModel Vm) Open(Host host, double width)
    {
        // No layout service: the first-visit coach stays hidden, so it does not cover what is measured.
        var vm = new RoutinesViewModel(() => host, layoutService: null, shell: null);
        var view = new RoutinesView { DataContext = vm };
        var window = new Window { Width = width, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    [AvaloniaFact]
    public void ThePagePaintsWithNoEditingControl()
    {
        var (window, view, _) = Open(WithOneRoutine(), 1280);

        view.GetVisualDescendants().OfType<TextBox>().Should().BeEmpty("the PC never edits a routine (R-UX-35)");
        view.GetVisualDescendants().OfType<ToggleSwitch>().Should().HaveCountGreaterThanOrEqualTo(2, "Pause all and the card's switch");

        var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        var pixels = FramePixels.From(frame!);
        pixels.DistinctQuantisedColours(new PixelRect(0, 0, pixels.Size.Width, pixels.Size.Height))
            .Should().BeGreaterThan(1);
        window.Close();
    }

    [AvaloniaFact]
    public void TheDryRunBannerShowsOnlyInDryRun()
    {
        var (window, view, _) = Open(WithOneRoutine(dryRun: true), 1280);
        view.FindControl<Border>("DryRunBanner")!.IsVisible.Should().BeTrue();
        window.Close();

        var (window2, view2, _) = Open(WithOneRoutine(dryRun: false), 1280);
        view2.FindControl<Border>("DryRunBanner")!.IsVisible.Should().BeFalse();
        window2.Close();
    }

    [AvaloniaTheory]
    [InlineData(1280, true)]
    [InlineData(760, false)]
    public void TheNineHundredBreakpointPicksListDetailOrOneColumn(double width, bool wide)
    {
        var (window, view, vm) = Open(WithOneRoutine(), width);

        vm.IsWideLayout.Should().Be(wide);
        view.FindControl<Border>("DetailPane")!.IsVisible.Should().Be(wide);
        window.Close();
    }

    [AvaloniaFact]
    public void SpaceOnAFocusedCardTogglesItsSwitch()
    {
        var host = WithOneRoutine();
        var (window, view, _) = Open(host, 1280);

        var item = view.GetVisualDescendants().OfType<ListBoxItem>().Single();
        item.Focus(NavigationMethod.Tab).Should().BeTrue();
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs();

        host.Disabled.Should().Equal(("client-1", "r1", true));
        window.Close();
    }
}
