using Remex.Core.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>TestRunSimulationTests</c> (D7, T22): a test run never issues a destructive
/// verb, still shows the countdown, and records <c>simulated</c>; non-destructive steps run for real.
/// </summary>
public sealed class TestRunSimulationTests
{
    public static TheoryData<string> DestructiveVerbs()
    {
        var data = new TheoryData<string>();
        foreach (var verb in RoutinePowerVerbs.Destructive)
        {
            data.Add(verb);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DestructiveVerbs))]
    public async Task ATestRunNeverIssuesADestructiveVerb(string verb)
    {
        var bench = new RoutineTestBench();
        var announced = false;

        var run = bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(verb), testRun: true),
            _ =>
            {
                announced = true;
                return Task.CompletedTask;
            });
        await bench.WaitForCountdownAsync();
        bench.ElapseCountdown();
        var result = await run;

        Assert.Empty(bench.Power.Issued);
        Assert.False(announced);
        Assert.Equal(RoutineStepOutcomes.Simulated, result.Outcome);
        Assert.Equal(RoutineReasonCodes.Simulated, result.ReasonCode);
        Assert.True(result.CountdownShown);
        Assert.True(Assert.Single(bench.Ui.Shown).TestRun);
    }

    [Fact]
    public async Task ATestRunNeverIssuesADestructiveVerbEvenWithPresence()
    {
        var bench = new RoutineTestBench();

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Shutdown), testRun: true, presenceConfirmed: true));

        Assert.Empty(bench.Power.Issued);
        Assert.Equal(RoutineStepOutcomes.Simulated, result.Outcome);
    }

    [Fact]
    public async Task ACancelledTestRunIsCancelledNotSimulated()
    {
        var bench = new RoutineTestBench();

        var run = bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Sleep), testRun: true));
        await bench.WaitForCountdownAsync();
        bench.Ui.LastCancel!();
        var result = await run;

        Assert.Equal(RoutineStepOutcomes.Cancelled, result.Outcome);
        Assert.Empty(bench.Power.Issued);
    }

    [Fact]
    public async Task NonDestructiveStepsRunForRealInATest()
    {
        var bench = new RoutineTestBench();

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock), testRun: true));
        var media = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(
            new RoutineStep { Type = RoutineStepTypes.Media, MediaAction = RoutineMediaActions.Next }, testRun: true));

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Equal(RoutineStepOutcomes.Succeeded, media.Outcome);
        Assert.Single(bench.Power.Issued);
        Assert.Single(bench.Media.Sent);
    }
}
