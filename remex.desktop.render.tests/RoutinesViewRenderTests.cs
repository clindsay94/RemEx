using Avalonia;
using Avalonia.Automation.Peers;
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

        public List<RoutineRun> History { get; } = new();

        public IReadOnlyList<RoutineRun> GetHistory(string? ownerClientId = null, string? routineId = null) => History;

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

    /// <summary>
    /// RemEx-pp0rt.16: the live UIA tree announced every card as "RoutineCardViewModel". Asked of the
    /// real automation peers, not of the XAML text, because a name on an element with no announced
    /// peer is announced by nothing (see DialogContentAccessibleNamesTests).
    /// </summary>
    [AvaloniaFact]
    public void ListItemsAndHistoryRowsAnnounceTheRoutineAndItsState()
    {
        var host = WithOneRoutine();
        host.History.Add(new RoutineRun
        {
            RunId = "run-1",
            OwnerClientId = "client-1",
            RoutineId = "r1",
            Outcome = RoutineRunOutcomes.Succeeded,
            Source = "manual.app",
            StartedAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
        });
        var (window, view, vm) = Open(host, 1280);

        var item = view.GetVisualDescendants().OfType<ListBoxItem>().Single();
        var itemName = ControlAutomationPeer.CreatePeerForElement(item).GetName();
        itemName.Should().Contain("Sleep when idle").And.NotContain("ViewModel");
        itemName.Should().Contain(vm.Groups[0].Routines[0].IsOn
            ? Remex.Desktop.Services.LocalizationService.Instance["Routines_Card_StateOn"]
            : Remex.Desktop.Services.LocalizationService.Instance["Routines_Card_StateOff"]);

        vm.Groups[0].SelectedRoutine = vm.Groups[0].Routines[0];
        Dispatcher.UIThread.RunJobs();

        var rows = view.FindControl<Border>("DetailPane")!.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("history-row"))
            .ToList();
        rows.Should().NotBeEmpty("the selected routine has one run in its history");
        foreach (var row in rows)
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            peer.GetName().Should().NotBeNullOrWhiteSpace().And.NotContain("ViewModel");
            peer.IsControlElement().Should().BeTrue("a history row must be in the control view to be announced");
            peer.GetAutomationControlType().Should().Be(AutomationControlType.ListItem);
        }

        window.Close();
    }

    /// <summary>
    /// RemEx-pp0rt.16: a paired phone with no routines still shows its group header, and on the empty
    /// page that header sat 8 px under the Learn button. The gap now matches the 32 above the empty state.
    /// </summary>
    [AvaloniaFact]
    public void TheEmptyStateLeavesRoomAboveAPhoneGroupHeader()
    {
        var host = new Host
        {
            Snapshot = new RoutinesHostSnapshot(false, false, "idle", "session",
                [new RoutineOwnerView("client-1", "Pixel", false, false, false, 0, [])], []),
        };
        var (window, view, vm) = Open(host, 1280);
        vm.IsEmpty.Should().BeTrue();

        var empty = view.FindControl<StackPanel>("EmptyState")!;
        var groups = view.FindControl<ItemsControl>("RoutineGroups")!;
        empty.IsVisible.Should().BeTrue();
        groups.ItemCount.Should().Be(1, "the paired phone's group shows even with no routines");

        var emptyBottom = empty.TranslatePoint(new Point(0, empty.Bounds.Height), view)!.Value.Y;
        var groupsTop = groups.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        (groupsTop - emptyBottom).Should().BeGreaterThanOrEqualTo(32,
            "the group header must not crowd the empty state's Learn button");
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
