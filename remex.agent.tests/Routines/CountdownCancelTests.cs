using Remex.Core.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>CountdownCancelTests</c> and <c>RunNowCountdownBypassTests</c> (§8.6, T5, T21):
/// 15 s; every cancel path; one countdown host-wide; the locked-session path proceeds unseen; only a
/// confirmed Run now skips the countdown.
/// </summary>
public sealed class CountdownCancelTests
{
    private static RoutineStep Shutdown => RoutineTestBench.PowerStep(RoutinePowerVerbs.Shutdown);

    [Fact]
    public async Task TheVerbIsIssuedOnlyAfterTheFull15Seconds()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();

        bench.Time.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(20);
        Assert.False(run.IsCompleted);
        Assert.Empty(bench.Power.Issued);

        bench.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await run;

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Single(bench.Power.Issued);
        var prompt = Assert.Single(bench.Ui.Shown);
        Assert.Equal(15, prompt.Seconds);
        Assert.Equal("Pixel", prompt.PhoneName);
        Assert.Equal([prompt.RunId], bench.Ui.Closed);
    }

    [Fact]
    public async Task CancelOnThePcStopsTheVerb()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();
        bench.Ui.LastCancel!();
        var result = await run;

        Assert.Equal(RoutineStepOutcomes.Cancelled, result.Outcome);
        Assert.Equal(RoutineReasonCodes.CancelledOnPc, result.ReasonCode);
        Assert.Equal(RoutineCancelledBy.Pc, result.CancelledBy);
        Assert.Empty(bench.Power.Issued);
        Assert.False(bench.Countdown.IsActive);
    }

    [Fact]
    public async Task TheOwnerPhoneCanCancel()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();
        Assert.True(bench.Countdown.TryCancelFromPhone("run-1", RoutineTestBench.Owner, RoutineCancelledBy.Phone));
        var result = await run;

        Assert.Equal(RoutineReasonCodes.CancelledOnPhone, result.ReasonCode);
        Assert.Equal(RoutineCancelledBy.Phone, result.CancelledBy);
        Assert.Empty(bench.Power.Issued);
    }

    [Fact]
    public async Task APhonePauseCountsAsACancelFromThePhone()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();
        Assert.True(bench.Countdown.CancelForOwner(RoutineTestBench.Owner, RoutineCancelledBy.Pause));
        var result = await run;

        Assert.Equal(RoutineReasonCodes.CancelledOnPhone, result.ReasonCode);
        Assert.Equal(RoutineCancelledBy.Pause, result.CancelledBy);
    }

    [Fact]
    public async Task AnotherPhoneCannotCancelSomeoneElsesCountdown()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();

        Assert.False(bench.Countdown.TryCancelFromPhone("run-1", RoutineTestBench.OtherOwner, RoutineCancelledBy.Phone));
        Assert.False(bench.Countdown.TryCancelFromPhone("run-other", RoutineTestBench.Owner, RoutineCancelledBy.Phone));
        Assert.False(bench.Countdown.CancelForOwner(RoutineTestBench.OtherOwner, RoutineCancelledBy.Pause));

        bench.ElapseCountdown();
        var result = await run;
        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Single(bench.Power.Issued);
    }

    [Fact]
    public async Task PcPauseAllCancelsWhateverIsCountingDown()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();
        Assert.True(bench.Countdown.CancelActive(RoutineCancelledBy.Pc));
        var result = await run;

        Assert.Equal(RoutineReasonCodes.CancelledOnPc, result.ReasonCode);
        Assert.Empty(bench.Power.Issued);
    }

    [Fact]
    public async Task OnlyOneCountdownRunsAtATime()
    {
        var bench = new RoutineTestBench();

        var first = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();
        var second = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Restart), owner: RoutineTestBench.OtherOwner, runId: "run-2"));

        Assert.Equal(RoutineStepOutcomes.Failed, second.Outcome);
        Assert.Equal(RoutineReasonCodes.ConflictCountdownActive, second.ReasonCode);

        bench.ElapseCountdown();
        await first;
        Assert.Equal([(RoutinePowerVerbs.Shutdown, (int?)null)], bench.Power.Issued);
    }

    [Fact]
    public async Task ALockedPcStillCountsDownAndProceedsUnseen()
    {
        var bench = new RoutineTestBench();
        bench.Lock.Locked = true;

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        await bench.WaitForCountdownAsync();
        bench.ElapseCountdown();
        var result = await run;

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.False(result.CountdownShown);
        Assert.Single(bench.Power.Issued);
    }

    [Fact]
    public async Task AWindowThatCannotOpenIsUnseenNotSkipped()
    {
        var bench = new RoutineTestBench();
        bench.Ui.Throw = true;

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown));
        for (var i = 0; i < 200 && !bench.Time.HasPendingTimers; i++)
        {
            await Task.Delay(5);
        }

        Assert.False(run.IsCompleted);
        bench.ElapseCountdown();
        var result = await run;

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.False(result.CountdownShown);
    }

    [Fact]
    public async Task AConfirmedRunNowSkipsTheCountdown()
    {
        var bench = new RoutineTestBench();

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(Shutdown, presenceConfirmed: true));

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Empty(bench.Ui.Shown);
        Assert.Single(bench.Power.Issued);
    }

    [Fact]
    public async Task EveryWireInitiatedStepCountsDown()
    {
        // T21: the request handler is the only wire path in S1, and it never sets presenceConfirmed.
        var bench = new RoutineTestBench();
        var handler = bench.CreateHandler();

        var run = handler.HandleStepRequestAsync(
            RoutineTestBench.Owner,
            new Remex.Core.Messages.Routines.RoutineStepRequestPayload
            {
                RunId = "run-9", RoutineId = "r", RoutineName = "Night", StepIndex = 0, Step = Shutdown,
                Source = RoutineRunSources.NfcTap,
            },
            _ => Task.CompletedTask);
        await bench.WaitForCountdownAsync();
        Assert.Empty(bench.Power.Issued);

        bench.ElapseCountdown();
        await run;
        Assert.Single(bench.Power.Issued);
    }

    [Fact]
    public void PresenceConfirmedHasNoWireRepresentation()
    {
        // No routine payload may carry a field that could express presence (T21).
        foreach (var type in new[]
        {
            typeof(Remex.Core.Messages.Routines.RoutineStepRequestPayload),
            typeof(Remex.Core.Messages.Routines.RoutineRunRequestPayload),
            typeof(Remex.Core.Messages.Routines.RoutinesSyncPayload),
        })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Presence", StringComparison.OrdinalIgnoreCase));
        }
    }
}
