using Remex.Agent.Services.Routines;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;
using Remex.Core.Services.Command;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>Routines spec §13.2 <c>RoutineRunnerStateMachineTests</c>: §8.1 / §8.4.</summary>
public sealed class RoutineRunnerStateMachineTests
{
    [Fact]
    public async Task StepsRunInOrderAndTheRunSucceeds()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc("one"), Media(), NotifyPc("two")));

        var handle = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        var final = await handle.Completion;

        Assert.Equal(RoutineRunOutcomes.Succeeded, final.Outcome);
        Assert.All(final.Steps!, s => Assert.Equal(RoutineStepStatuses.Succeeded, s.Status));
        Assert.Equal(new[] { "one", "two" }, bench.Core.Ui.Notifications.Select(n => n.Title).ToArray());
        Assert.Single(bench.Core.Media.Sent);
    }

    [Fact]
    public async Task TheRunIsSavedAsRunningBeforeItsFirstStep()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Delay(30), NotifyPc()));

        var handle = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        await bench.WaitForTimerAsync();

        Assert.Equal(RoutineRunOutcomes.Running, handle.Initial.Outcome);
        Assert.Equal(RoutineRunOutcomes.Running, bench.Runs.Find(handle.Initial.RunId!)!.Outcome);
        Assert.Contains("\"running\"", bench.Files.Files[RoutineRunStore.FileName], StringComparison.Ordinal);

        bench.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(RoutineRunOutcomes.Succeeded, (await handle.Completion).Outcome);
    }

    [Fact]
    public async Task TheFirstFailedStepStopsTheRunAndSkipsTheRest()
    {
        var bench = new RoutineHostTestBench();
        bench.Core.Media.Available = false;
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc(), Media(), NotifyPc("never")));

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineRunOutcomes.Failed, final.Outcome);
        Assert.Equal(RoutineReasonCodes.MediaUnavailable, final.ReasonCode);
        Assert.Equal(
            new string?[] { RoutineStepStatuses.Succeeded, RoutineStepStatuses.Failed, RoutineStepStatuses.Skipped },
            final.Steps!.Select(s => s.Status).ToArray());
        Assert.DoesNotContain(bench.Core.Ui.Notifications, n => n.Title == "never");
    }

    [Fact]
    public async Task ASecondTriggerWhileRunningIsSkippedAlreadyRunning()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Delay(30)));
        var first = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        await bench.WaitForTimerAsync();

        var second = await bench.StartAsync(1, RoutineRunSources.ManualApp);

        Assert.True(first.Started);
        Assert.Equal(RoutineRunOutcomes.Skipped, second.Initial.Outcome);
        Assert.Equal(RoutineReasonCodes.AlreadyRunning, second.Initial.ReasonCode);
        bench.Time.Advance(TimeSpan.FromSeconds(30));
        await first.Completion;
    }

    [Fact]
    public async Task AtMostFourRunsAtOnceOnThePc()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Enumerable.Range(1, 5).Select(i => Routine(i, IdleTrigger(), Delay(30))).ToArray());

        var handles = new List<RoutineRunHandle>();
        for (var i = 1; i <= 5; i++)
        {
            handles.Add(await bench.StartAsync(i, RoutineRunSources.ManualApp));
        }

        Assert.Equal(4, handles.Count(h => h.Started));
        Assert.Equal(RoutineReasonCodes.RateLimited, handles[4].Initial.ReasonCode);
        await bench.AdvanceUntilAsync(Task.WhenAll(handles.Select(h => h.Completion)), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ThirtyRunsPerOwnerPerHour()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        for (var i = 0; i < RoutineHostRunner.MaxRunsPerOwnerPerHour; i++)
        {
            await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;
        }

        var over = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        Assert.Equal(RoutineReasonCodes.RateLimited, over.Initial.ReasonCode);

        bench.Time.Advance(TimeSpan.FromHours(1));
        Assert.True((await bench.StartAsync(1, RoutineRunSources.ManualApp)).Started);
    }

    [Fact]
    public async Task AnAutomaticSourceRunsARoutineAtMostOncePerMinute()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await (await bench.StartAsync(1)).Completion;

        Assert.Equal(RoutineReasonCodes.Cooldown, (await bench.StartAsync(1)).Initial.ReasonCode);
        bench.Time.Advance(RoutineHostRunner.AutomaticMinInterval);
        Assert.True((await bench.StartAsync(1)).Started);
    }

    [Fact]
    public async Task ANotifyPhoneStepThatCannotBeDeliveredIsRecordedAndTheRunGoesOn()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPhone("away"), NotifyPc("after")));

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineRunOutcomes.Succeeded, final.Outcome);
        Assert.Equal(RoutineStepStatuses.Expired, final.Steps![0].Status);
        Assert.Equal(RoutineReasonCodes.NotifyExpired, final.Steps[0].ReasonCode);
        Assert.Contains(bench.Core.Ui.Notifications, n => n.Title == "away");
    }
}

/// <summary>Routines spec §13.2 <c>RunNowCountdownBypassTests</c> and T21.</summary>
public sealed class RunNowCountdownBypassTests
{
    [Fact]
    public async Task AConfirmedRunNowSkipsTheCountdown()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));

        var started = await bench.Service.RunNowAsync(Owner, Id(1), presenceConfirmed: true);
        await WaitUntilAsync(() => bench.Core.Power.Issued.Count == 1, "the verb was never issued");

        Assert.Equal(RoutineRunSources.ManualPcRunNow, started.Source);
        Assert.Empty(bench.Core.Ui.Shown);
    }

    [Fact]
    public async Task AnUnconfirmedRunNowCountsDown()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));

        await bench.Service.RunNowAsync(Owner, Id(1), presenceConfirmed: false);
        await bench.Core.WaitForCountdownAsync();

        Assert.Empty(bench.Core.Power.Issued);
        bench.Core.ElapseCountdown();
        await WaitUntilAsync(() => bench.Core.Power.Issued.Count == 1, "the verb was never issued after the countdown");
    }

    [Fact]
    public async Task APhoneRunRequestAlwaysCountsDown()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Shutdown)));

        await bench.Messages.HandleRunRequestAsync(Owner, new RoutineRunRequestPayload { RunId = Guid.NewGuid().ToString(), RoutineId = Id(1) }, _ => Task.CompletedTask);
        await bench.Core.WaitForCountdownAsync();

        Assert.Empty(bench.Core.Power.Issued);
        bench.Core.ElapseCountdown();
        await WaitUntilAsync(() => bench.Core.Power.Issued.Count == 1, "the verb was never issued");
    }

    [Fact]
    public async Task ATriggerAlwaysCountsDown()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, SessionTrigger(locked: true), Power(RoutinePowerVerbs.Sleep)));

        var handle = await bench.StartAsync(1, RoutineRunSources.PcSession);
        await bench.Core.WaitForCountdownAsync();
        bench.Core.ElapseCountdown();
        var final = await handle.Completion;

        Assert.Equal(RoutineRunOutcomes.Succeeded, final.Outcome);
        Assert.True(final.Countdown!.Shown);
        Assert.Single(bench.Core.Power.Issued);
    }

    [Fact]
    public void NoWirePayloadCanExpressPresence()
    {
        // T21: the flag exists only on the in-process start record and the executor's execution record.
        foreach (var type in new[] { typeof(RoutineRunRequestPayload), typeof(RoutineStepRequestPayload), typeof(RoutinesSyncPayload) })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Presence", StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>Routines spec §13.2 <c>RoutineRunRequestTests</c>: §7.3.8, T24.</summary>
public sealed class RoutineRunRequestTests
{
    private static async Task<RoutineRun> RequestAsync(RoutineHostTestBench bench, string routineId, string owner = Owner, string? runId = null)
    {
        RoutineRunReportPayload? report = null;
        await bench.Messages.HandleRunRequestAsync(
            owner,
            new RoutineRunRequestPayload { RunId = runId ?? Guid.NewGuid().ToString(), RoutineId = routineId },
            m => { report = m.RoutineRunReport; return Task.CompletedTask; });
        Assert.NotNull(report);
        Assert.True(report!.Live);
        return Assert.Single(report.Runs!);
    }

    [Fact]
    public async Task RunsTheSendersStoredCopy()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc("stored")));

        var run = await RequestAsync(bench, Id(1));
        await WaitUntilAsync(() => bench.Core.Ui.Notifications.Count == 1, "the stored step never ran");

        Assert.Equal(RoutineRunOutcomes.Running, run.Outcome);
        Assert.Equal(RoutineRunSources.ManualApp, run.Source);
        Assert.Equal("stored", bench.Core.Ui.Notifications[0].Title);
    }

    [Fact]
    public async Task AnUnknownIdOrAnotherOwnersRoutineIsNotFound()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineReasonCodes.RoutineNotFound, (await RequestAsync(bench, Id(9))).ReasonCode);
        Assert.Equal(RoutineReasonCodes.RoutineNotFound, (await RequestAsync(bench, Id(1), OtherOwner)).ReasonCode);
    }

    [Fact]
    public async Task PauseDoesNotStopAPhoneRequestButBlockAndPcDisableDo()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, paused: true, routines: [Routine(1, IdleTrigger(), NotifyPc()), Routine(2, IdleTrigger(), NotifyPc())]);
        await bench.Service.SetHostPausedAsync(true);

        Assert.Equal(RoutineRunOutcomes.Running, (await RequestAsync(bench, Id(1))).Outcome);

        await bench.Service.SetDisabledOnPcAsync(Owner, Id(2), disabled: true);
        Assert.Equal(RoutineReasonCodes.DisabledOnPc, (await RequestAsync(bench, Id(2))).ReasonCode);

        await bench.Service.SetBlockedAsync(Owner, blocked: true);
        Assert.Equal(RoutineReasonCodes.BlockedByPc, (await RequestAsync(bench, Id(1))).ReasonCode);
    }

    [Fact]
    public async Task AResentRequestNeverStartsASecondRun()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Delay(30)));
        var runId = Guid.NewGuid().ToString();

        var first = await RequestAsync(bench, Id(1), runId: runId);
        var again = await RequestAsync(bench, Id(1), runId: runId);

        Assert.Equal(first.RunId, again.RunId);
        Assert.Equal(RoutineRunOutcomes.Running, again.Outcome);
        Assert.Single(bench.Runs.Query(Owner, null));
        await bench.WaitForTimerAsync();
        bench.Time.Advance(TimeSpan.FromSeconds(30));
    }
}

/// <summary>Routines spec §13.2 <c>PauseAllPropagationTests</c> and the PC-side <c>CountdownCancelTests</c> paths (§8.7).</summary>
public sealed class PauseAllPropagationTests
{
    [Fact]
    public async Task ThePhonesPausePausesOnlyThatOwnersAutomaticRoutines()
    {
        var bench = new RoutineHostTestBench();
        var result = await bench.SyncAsync(1, paused: true, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await bench.SyncAsync(1, owner: OtherOwner, routines: Routine(2, IdleTrigger(), NotifyPc()));

        Assert.True(result.OwnerPaused);
        Assert.Equal(RoutineReasonCodes.PausedOnPhone, (await bench.StartAsync(1)).Initial.ReasonCode);
        Assert.True((await bench.StartAsync(2, owner: OtherOwner)).Started);
        Assert.True((await bench.StartAsync(1, RoutineRunSources.ManualApp)).Started);
    }

    [Fact]
    public async Task ThePcsPauseSkipsEveryOwnersAutomaticRoutinesAndIsEchoed()
    {
        var bench = new RoutineHostTestBench();
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        await bench.Service.SetHostPausedAsync(true);

        Assert.Equal(RoutineReasonCodes.PausedOnPc, (await bench.StartAsync(1)).Initial.ReasonCode);
        var unsolicited = Assert.Single(bench.Channel.SyncResults(Owner));
        Assert.True(unsolicited.Unsolicited);
        Assert.True(unsolicited.HostPaused);
        Assert.True((await bench.SyncAsync(2, routines: Routine(1, IdleTrigger(), NotifyPc()))).HostPaused);
    }

    [Fact]
    public async Task PcPauseCancelsACountdownFromThePcSide()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));
        var handle = await bench.StartAsync(1);
        await bench.Core.WaitForCountdownAsync();

        await bench.Service.SetHostPausedAsync(true);
        var final = await handle.Completion;

        Assert.Equal(RoutineRunOutcomes.Cancelled, final.Outcome);
        Assert.Equal(RoutineReasonCodes.CancelledOnPc, final.ReasonCode);
        Assert.Equal(RoutineCancelledBy.Pause, final.CancelledBy);
        Assert.Empty(bench.Core.Power.Issued);
    }

    [Fact]
    public async Task PhonePauseArrivingDuringACountdownCancelsItFromThePhoneSide()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));
        var handle = await bench.StartAsync(1);
        await bench.Core.WaitForCountdownAsync();

        await bench.SyncAsync(2, paused: true, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));
        var final = await handle.Completion;

        Assert.Equal(RoutineReasonCodes.CancelledOnPhone, final.ReasonCode);
        Assert.Equal(RoutineCancelledBy.Pause, final.CancelledBy);
        Assert.Empty(bench.Core.Power.Issued);
    }

    [Fact]
    public async Task APhonesCancelStopsItsOwnHostRunOnly()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Delay(60), NotifyPc()));
        var handle = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        await bench.WaitForTimerAsync();

        Assert.False(bench.Messages.HandleCancel(OtherOwner, new RoutineCancelPayload { RunId = handle.Initial.RunId }));
        Assert.True(bench.Messages.HandleCancel(Owner, new RoutineCancelPayload { RunId = handle.Initial.RunId, Reason = "user" }));
        var final = await handle.Completion;

        Assert.Equal(RoutineRunOutcomes.Cancelled, final.Outcome);
        Assert.Equal(RoutineReasonCodes.CancelledOnPhone, final.ReasonCode);
        Assert.All(final.Steps!, s => Assert.Equal(RoutineStepStatuses.Cancelled, s.Status));
    }
}

/// <summary>Routines spec §13.2 <c>UnsolicitedSyncResultTests</c> (R-UX-37).</summary>
public sealed class UnsolicitedSyncResultTests
{
    [Fact]
    public async Task APcToggleReachesAConnectedOwnerAtOnce()
    {
        var bench = new RoutineHostTestBench();
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(3, routines: Routine(1, IdleTrigger(), NotifyPc()));

        await bench.Service.SetDisabledOnPcAsync(Owner, Id(1), disabled: true);

        var result = Assert.Single(bench.Channel.SyncResults(Owner));
        Assert.True(result.Unsolicited);
        Assert.Equal(3, result.Revision);
        Assert.Equal(3, result.StoredRevision);
        Assert.Equal([Id(1)], result.PcDisabled!.ToArray());
    }

    [Fact]
    public async Task ABlockReachesTheOwnerAndNothingGoesToAPhoneThatCannotTakeIt()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await bench.Service.SetDisabledOnPcAsync(Owner, Id(1), disabled: true);
        Assert.Empty(bench.Channel.Sent);

        bench.Channel.Reachable.Add(Owner);
        await bench.Service.SetBlockedAsync(Owner, blocked: true);

        var unsolicited = Assert.Single(bench.Channel.SyncResults(Owner));
        Assert.True(unsolicited.Unsolicited);
        Assert.Equal(RoutineSyncStatuses.BlockedByPc, unsolicited.Status);
        Assert.Equal(RoutineSyncStatuses.BlockedByPc, (await bench.SyncAsync(2)).Status);
    }

    [Fact]
    public async Task ThePcSwitchSurvivesALaterSyncAndIsDroppedWithItsRoutine()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: [Routine(1, IdleTrigger(), NotifyPc()), Routine(2, IdleTrigger(), NotifyPc())]);
        await bench.Service.SetDisabledOnPcAsync(Owner, Id(1), disabled: true);
        await bench.Service.SetDisabledOnPcAsync(Owner, Id(2), disabled: true);

        var result = await bench.SyncAsync(2, routines: Routine(1, IdleTrigger(), NotifyPc()));

        Assert.Equal([Id(1)], result.PcDisabled!.ToArray());
    }
}

/// <summary>Routines spec §13.2 <c>LiveRunReportTests</c> (§7.3.6, UX need 13).</summary>
public sealed class LiveRunReportTests
{
    [Fact]
    public async Task StepTransitionsAreLiveAndTheEndIsAPersistedReport()
    {
        var bench = new RoutineHostTestBench();
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc(), NotifyPc()));

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        var reports = bench.Channel.Reports(Owner);
        Assert.True(reports.Count >= 3);
        Assert.All(reports.Take(reports.Count - 1), r => Assert.True(r.Live));
        var last = reports[^1];
        Assert.False(last.Live);
        Assert.Equal(RoutineRunOutcomes.Succeeded, Assert.Single(last.Runs!).Outcome);
        Assert.All(reports, r => Assert.Null(Assert.Single(r.Runs!).OwnerClientId));

        // Live updates never moved the cursor: the stored record is the only one past it.
        var (page, more) = bench.Runs.Page(Owner, 0);
        Assert.False(more);
        Assert.Equal(final.Seq, Assert.Single(page).Seq);
    }

    [Fact]
    public async Task ACountdownStartIsReportedLive()
    {
        var bench = new RoutineHostTestBench();
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));

        var handle = await bench.StartAsync(1);
        await bench.Core.WaitForCountdownAsync();

        Assert.Contains(bench.Channel.Reports(Owner), r => r.Live && r.Runs![0].Countdown is not null);
        bench.Core.ElapseCountdown();
        await handle.Completion;
    }
}

/// <summary>Routines spec §13.2 <c>RoutineRunRevalidationTests</c> (T6).</summary>
public sealed class RoutineRunRevalidationTests
{
    [Fact]
    public async Task ARevokedOwnersRoutineDoesNotRun()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        bench.Core.Owners.Paired.Remove(Owner);

        Assert.Equal(RoutineReasonCodes.PcNotPaired, (await bench.StartAsync(1)).Initial.ReasonCode);
    }

    [Fact]
    public async Task AVerbThatVanishedAfterTheSyncFailsAtRunTime()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Lock)));
        bench.Core.AdvertisedVerbs.Remove(RoutinePowerVerbs.Lock);

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineReasonCodes.PowerUnsupported, final.ReasonCode);
        Assert.Empty(bench.Core.Power.Issued);
    }

    [Fact]
    public async Task AnAppRemovedAfterTheSyncFailsAtRunTime()
    {
        var bench = new RoutineHostTestBench();
        var app = Guid.NewGuid();
        bench.Core.Launchers.Entries.Add(new Remex.Core.Models.AppEntry(app, "Steam", Path.Combine(Path.GetTempPath(), "steam.exe"), "#000000", null));
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = app.ToString() }));
        bench.Core.Launchers.Entries.Clear();

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineReasonCodes.LaunchNotAllowed, final.ReasonCode);
        Assert.Empty(bench.Core.Launcher.Launched);
    }

    [Fact]
    public async Task AFailedVerbIsReportedAsFailed()
    {
        var bench = new RoutineHostTestBench();
        bench.Core.Power.Respond = _ => new SharedCommandVerbs.Outcome(false, "no");
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Lock)));

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineRunOutcomes.Failed, final.Outcome);
        Assert.Equal(RoutineReasonCodes.PowerFailed, final.ReasonCode);
    }
}
