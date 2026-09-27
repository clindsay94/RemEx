using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Routines;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;
using Remex.Desktop.Tests.Routines;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The PC Routines page view model (routines spec §2.3, S4b): the enable switch (R-UX-37), Run now with
/// presence (R-UX-36, T21), Pause all (R-UX-21), blocking, the coach mark (R-UX-40), and the lazily
/// resolved host.
/// </summary>
public sealed class RoutinesViewModelTests : IAsyncLifetime
{
    private const string Owner = "client-1";

    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layout;
    private readonly FakeRoutinesHost _host = new();
    private readonly List<(string Title, string Message, string Button, string Classes)> _confirms = new();

    public RoutinesViewModelTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-routines-vm-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layout = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public Task InitializeAsync() => _layout.LoadAsync();

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private RoutinesViewModel Create(bool confirm = true)
    {
        var vm = new RoutinesViewModel(() => _host, _layout, shell: null, post: a => a());
        vm.OnConfirmationRequested = (title, message, button, classes) =>
        {
            _confirms.Add((title, message, button, classes));
            return Task.FromResult(confirm);
        };
        return vm;
    }

    private RoutineCardViewModel OnlyCard(RoutinesViewModel vm) => vm.Groups.Single().Routines.Single();

    // ═══ List and state ═══

    [Fact]
    public void RoutinesAreGroupedByPhoneWithChipsAndState()
    {
        _host.SetOwners(
            FakeRoutinesHost.Owner(Owner, "Pixel",
                FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Sleep when idle", FakeRoutinesHost.Power(RoutinePowerVerbs.Sleep)))),
            FakeRoutinesHost.Owner("client-2", null,
                FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r2", "Bedtime", FakeRoutinesHost.Notify(), FakeRoutinesHost.Power(RoutinePowerVerbs.Shutdown)))));

        var vm = Create();

        vm.IsAvailable.Should().BeTrue();
        vm.HasRoutines.Should().BeTrue();
        vm.IsEmpty.Should().BeFalse();
        vm.Groups.Should().HaveCount(2);
        vm.Groups[0].PhoneName.Should().Be("Pixel");
        vm.Groups[1].PhoneName.Should().Be(LocalizationService.Instance["Routine_Countdown_PhoneUnknown"]);

        var bedtime = vm.Groups[1].Routines.Single();
        bedtime.Chips.Select(c => c.Text).Should().Equal(
            LocalizationService.Instance["Routine_Step_NotifyPc"],
            LocalizationService.Instance["Routine_Action_SHUTDOWN"]);
        bedtime.Chips[1].IsDestructive.Should().BeTrue("shut down discards unsaved work (R-UX-54)");
        bedtime.Chips[0].IsDestructive.Should().BeFalse();
        vm.Groups[0].Routines.Single().Chips.Single().IsDestructive.Should().BeFalse("sleep keeps the session");
    }

    [Fact]
    public void AnEmptyHostShowsTheEmptyStateAndNoHostShowsUnavailable()
    {
        var vm = Create();
        vm.IsEmpty.Should().BeTrue();

        var none = new RoutinesViewModel(() => null, _layout, shell: null, post: a => a());
        none.IsAvailable.Should().BeFalse();
        none.IsEmpty.Should().BeFalse("the unavailable notice explains it, not the empty state");
    }

    [Fact]
    public void AHostPublishedAfterThePageWasBuiltIsPickedUpOnTheNextRefresh()
    {
        // The embedded host publishes its container after it starts; a page built first must not
        // cache "no host" for the session (RemEx-n8xk).
        var source = new HostSource();
        var vm = new RoutinesViewModel(() => source.Host, _layout, shell: null, post: a => a());
        vm.IsAvailable.Should().BeFalse();

        source.Host = _host;
        vm.Refresh();
        vm.IsAvailable.Should().BeTrue();

        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()))));
        _host.RaiseChanged();
        vm.HasRoutines.Should().BeTrue("Changed was subscribed once the host was found");
    }

    [Fact]
    public void GroupStateLinesSayPausedBlockedAndSuspended()
    {
        var loc = LocalizationService.Instance;
        _host.SetOwners(
            FakeRoutinesHost.Owner("a", "A") with { Paused = true },
            FakeRoutinesHost.Owner("b", "B") with { BlockedByPc = true },
            FakeRoutinesHost.Owner("c", "C") with { Suspended = true });

        var vm = Create();

        vm.Groups[0].StateText.Should().Be(string.Format(loc.Culture, loc["Routines_Group_Paused"], "A"));
        vm.Groups[1].StateText.Should().Be(string.Format(loc.Culture, loc["Routines_Group_Blocked"], "B"));
        vm.Groups[1].BlockButtonText.Should().Be(loc["Routines_Unblock"]);
        vm.Groups[2].StateText.Should().StartWith(loc["Routines_Group_Suspended"].Split('{')[0]);
    }

    [Fact]
    public void RefreshKeepsTheSameCardInstancesSoFocusAndSelectionSurvive()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()))));
        var vm = Create();
        var card = OnlyCard(vm);
        vm.SelectedRoutine = card;

        vm.Refresh();

        OnlyCard(vm).Should().BeSameAs(card);
        vm.SelectedRoutine.Should().BeSameAs(card);
        card.IsSelected.Should().BeTrue();
    }

    // ═══ Enable switch (R-UX-37) ═══

    [Fact]
    public void SwitchingARoutineOffGoesToTheHost()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()))));
        var vm = Create();
        var card = OnlyCard(vm);
        card.IsOn.Should().BeTrue();

        card.IsOn = false;

        _host.DisabledCalls.Should().Equal((Owner, "r1", true));
        OnlyCard(vm).IsOn.Should().BeFalse();
    }

    [Fact]
    public void AFailedSaveFlipsTheSwitchBackAndSaysSo()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()))));
        var vm = Create();
        _host.ThrowOnMutate = true;

        OnlyCard(vm).IsOn = false;

        OnlyCard(vm).IsOn.Should().BeTrue("the host is the truth and it did not save");
        vm.StatusMessage.Should().Be(LocalizationService.Instance["Routines_ActionFailed"]);
    }

    [Fact]
    public void ARefreshNeverEchoesTheHostsOwnStateBackToIt()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()), disabledOnPc: true)));
        _host.Snapshot = _host.Snapshot with { HostPaused = true };

        var vm = Create();
        vm.Refresh();

        vm.HostPaused.Should().BeTrue();
        OnlyCard(vm).IsOn.Should().BeFalse();
        _host.PausedCalls.Should().BeEmpty();
        _host.DisabledCalls.Should().BeEmpty();
    }

    [Fact]
    public void ABlockedPhonesAndADamagedRoutinesSwitchesAreDisabledWithTheReason()
    {
        var loc = LocalizationService.Instance;
        _host.SetOwners(
            FakeRoutinesHost.Owner("a", "A", FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()))) with { BlockedByPc = true },
            FakeRoutinesHost.Owner("b", "B", FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r2", "Two", FakeRoutinesHost.Notify()) with { IsMalformed = true })));

        var vm = Create();

        var blocked = vm.Groups[0].Routines.Single();
        blocked.CanToggle.Should().BeFalse();
        blocked.Note.Should().Be(loc["Routines_Card_Blocked"]);

        var damaged = vm.Groups[1].Routines.Single();
        damaged.CanToggle.Should().BeFalse();
        damaged.Note.Should().Be(loc["Routines_Card_Malformed"]);
        damaged.NoteIsProblem.Should().BeTrue();

        damaged.ToggleEnabledCommand.Execute(null);
        _host.DisabledCalls.Should().BeEmpty();
    }

    [Fact]
    public void ARoutineSwitchedOffOnThePhoneSaysSo()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify()) with { Enabled = false })));
        var vm = Create();
        var loc = LocalizationService.Instance;
        OnlyCard(vm).Note.Should().Be(string.Format(loc.Culture, loc["Routines_Card_OffOnPhone"], "Pixel"));
        OnlyCard(vm).CanToggle.Should().BeTrue("the PC's own switch is independent of the phone's");
    }

    [Fact]
    public void ASensorRoutineWhoseSensorStoppedReportingSaysItIsWaiting()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "One", FakeRoutinesHost.Notify())) with { WaitingForSensor = true }));
        var vm = Create();

        OnlyCard(vm).Note.Should().Be(LocalizationService.Instance["Routines_Card_WaitingForSensor"]);
        OnlyCard(vm).NoteIsProblem.Should().BeTrue();
    }

    // ═══ Run now (R-UX-36, §8.6, D3, T21) ═══

    [Fact]
    public async Task RunNowWithoutAPowerStepRunsWithoutAskingAndWithoutPresence()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Hello", FakeRoutinesHost.Notify()))));
        var vm = Create();

        await OnlyCard(vm).RunNowCommand.ExecuteAsync(null);

        _confirms.Should().BeEmpty();
        _host.RunNowCalls.Should().Equal((Owner, "r1", false));
    }

    [Theory]
    [InlineData(RoutinePowerVerbs.Shutdown, "primary danger")]
    [InlineData(RoutinePowerVerbs.SignOut, "primary danger")]
    [InlineData(RoutinePowerVerbs.Sleep, "primary warning")]
    [InlineData(RoutinePowerVerbs.Hibernate, "primary warning")]
    public async Task RunNowWithADestructiveStepConfirmsAndAConfirmationIsPresence(string verb, string classes)
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Bedtime", FakeRoutinesHost.Notify(), FakeRoutinesHost.Power(verb)))));
        var vm = Create(confirm: true);

        await OnlyCard(vm).RunNowCommand.ExecuteAsync(null);

        _confirms.Should().ContainSingle();
        _confirms[0].Classes.Should().Be(classes);
        _confirms[0].Title.Should().Contain("Bedtime");
        _confirms[0].Message.Should().Contain(LocalizationService.Instance["Routine_Action_" + verb]);
        _host.RunNowCalls.Should().Equal((Owner, "r1", true));
    }

    [Fact]
    public async Task ADeclinedConfirmationRunsNothing()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Bedtime", FakeRoutinesHost.Power(RoutinePowerVerbs.Shutdown)))));
        var vm = Create(confirm: false);

        await OnlyCard(vm).RunNowCommand.ExecuteAsync(null);

        _confirms.Should().ContainSingle();
        _host.RunNowCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task NoConfirmationWiredMeansNoRunFailClosed()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Bedtime", FakeRoutinesHost.Power(RoutinePowerVerbs.Restart)))));
        var vm = Create();
        vm.OnConfirmationRequested = null;

        await OnlyCard(vm).RunNowCommand.ExecuteAsync(null);

        _host.RunNowCalls.Should().BeEmpty("a destructive run with no way to confirm must not run, and never with presence");
    }

    [Fact]
    public async Task ASkippedRunNowSaysWhy()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Hello", FakeRoutinesHost.Notify()))));
        _host.RunNowResult = (o, id) => new RoutineRun
        {
            RunId = "x", OwnerClientId = o, RoutineId = id,
            Outcome = RoutineRunOutcomes.Skipped, ReasonCode = RoutineReasonCodes.AlreadyRunning,
        };
        var vm = Create();

        await OnlyCard(vm).RunNowCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().Contain(LocalizationService.Instance["Routine_History_already_running"]);
    }

    // ═══ Pause all (R-UX-21) and block ═══

    [Fact]
    public void ThePauseAllSwitchAndThePaletteCommandsGoToTheHost()
    {
        var vm = Create();

        vm.HostPaused = true;
        vm.ResumeAllCommand.Execute(null);
        vm.PauseAllCommand.Execute(null);

        _host.PausedCalls.Should().Equal(true, false, true);
        vm.HostPaused.Should().BeTrue();
    }

    [Fact]
    public async Task BlockingConfirmsFirstAndUnblockingDoesNot()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel"));
        var vm = Create(confirm: true);

        await vm.Groups[0].ToggleBlockCommand.ExecuteAsync(null);
        _confirms.Should().ContainSingle();
        _host.BlockedCalls.Should().Equal((Owner, true));

        await vm.Groups[0].ToggleBlockCommand.ExecuteAsync(null);
        _confirms.Should().ContainSingle("unblocking only gives back what the phone asked for");
        _host.BlockedCalls.Should().Equal((Owner, true), (Owner, false));
    }

    [Fact]
    public async Task ADeclinedBlockDoesNothing()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel"));
        var vm = Create(confirm: false);

        await vm.Groups[0].ToggleBlockCommand.ExecuteAsync(null);

        _host.BlockedCalls.Should().BeEmpty();
    }

    // ═══ History (§8.8, R-UX-50) ═══

    [Fact]
    public void SelectingACardLoadsItsHistoryWithStepsAndReasonText()
    {
        var loc = LocalizationService.Instance;
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Bedtime", FakeRoutinesHost.Notify(), FakeRoutinesHost.Power(RoutinePowerVerbs.Shutdown)))));
        _host.Runs.Add(new RoutineRun
        {
            RunId = "run-1", OwnerClientId = Owner, RoutineId = "r1",
            Source = RoutineRunSources.PcIdle, StartedAtUnixMs = 1_758_000_000_000,
            Outcome = RoutineRunOutcomes.Cancelled, ReasonCode = RoutineReasonCodes.CancelledOnPc,
            Attributes = [RoutineRunAttributes.DryRun],
            Steps =
            [
                new RoutineRunStep { Index = 0, Kind = RoutineStepTypes.Notify, Status = RoutineStepStatuses.Succeeded },
                new RoutineRunStep { Index = 1, Kind = RoutineStepTypes.Power, Status = RoutineStepStatuses.Cancelled, ReasonCode = RoutineReasonCodes.CancelledOnPc },
            ],
        });

        var vm = Create();
        var card = OnlyCard(vm);
        card.LastRunText.Should().Contain(loc["Routine_History_cancelled_on_pc"]);

        vm.SelectedRoutine = card;

        var run = card.History.Single();
        run.StatusText.Should().Be(loc["Routine_History_cancelled_on_pc"]);
        run.SourceText.Should().Be(loc["Routine_Source_PcIdle"]);
        run.AttributesText.Should().Be(loc["Routine_History_dry_run"]);
        run.Kind.Should().Be("cancelled");
        run.Steps.Select(s => s.Label).Should().Equal(loc["Routine_Step_NotifyPc"], loc["Routine_Action_SHUTDOWN"]);
        run.Steps[1].ReasonText.Should().Be(loc["Routine_History_cancelled_on_pc"]);

        run.ToggleExpandedCommand.Execute(null);
        run.IsExpanded.Should().BeTrue();
        vm.Refresh();
        card.History.Single().IsExpanded.Should().BeTrue("a refresh keeps an expanded row expanded");
    }

    [Fact]
    public void ALiveRunLightsTheChipsAndCanBeCancelled()
    {
        _host.SetOwners(FakeRoutinesHost.Owner(Owner, "Pixel",
            FakeRoutinesHost.Entry(FakeRoutinesHost.Routine("r1", "Bedtime", FakeRoutinesHost.Notify(), FakeRoutinesHost.Power(RoutinePowerVerbs.Sleep)), running: true)));
        _host.Runs.Add(new RoutineRun
        {
            RunId = "live", OwnerClientId = Owner, RoutineId = "r1", Outcome = RoutineRunOutcomes.Running,
            Steps =
            [
                new RoutineRunStep { Index = 0, Status = RoutineStepStatuses.Succeeded },
                new RoutineRunStep { Index = 1, Status = RoutineStepStatuses.Running },
            ],
        });

        var vm = Create();
        var card = OnlyCard(vm);
        card.Running.Should().BeTrue();
        card.Chips[0].IsDone.Should().BeTrue();
        card.Chips[1].IsActive.Should().BeTrue();

        vm.SelectedRoutine = card;
        card.History.Single().IsRunning.Should().BeTrue();
        card.History.Single().CancelCommand.Execute(null);
        _host.CancelCalls.Should().Equal("live");
    }

    [Fact]
    public void AnUnknownReasonCodeNeverShowsARawKey()
    {
        RoutinePresentation.ReasonText("from_a_newer_version").Should().BeNull();
        RoutinePresentation.ReasonText(RoutineReasonCodes.Ok).Should().BeNull("the outcome already says Done");
    }

    // ═══ Coach mark (R-UX-40, §5.3) ═══

    [Fact]
    public void TheCoachShowsOnceThenReplaysFromTheQuestionMark()
    {
        var vm = Create();
        vm.ShowCoachMark.Should().BeTrue();
        vm.IsCoachStepOne.Should().BeTrue();

        vm.NextCoachStepCommand.Execute(null);
        vm.IsCoachStepTwo.Should().BeTrue();
        vm.DismissCoachMarkCommand.Execute(null);
        vm.ShowCoachMark.Should().BeFalse();

        _layout.CurrentProfile.SeenCoachMarks.Should().Contain(RoutinesViewModel.CoachKey);
        _layout.CurrentProfile.SeenCoachMarks.Should().Contain("routines", "the key named by §5.3");

        var again = Create();
        again.ShowCoachMark.Should().BeFalse("seen once is seen");

        again.ReplayCoachMarkCommand.Execute(null);
        again.ShowCoachMark.Should().BeTrue();
        again.IsCoachStepOne.Should().BeTrue();
    }

    private sealed class HostSource
    {
        public IRoutinesHost? Host { get; set; }
    }
}
