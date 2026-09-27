using Remex.Core.Routines;
using Remex.Desktop.Services.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>RoutineDryRunSwitchTests</c> (§8.9, T23): only the command-line switch enables
/// dry run, the state is exposed for the banner, and destructive verbs are logged, not issued.
/// </summary>
public sealed class RoutineDryRunSwitchTests
{
    [Theory]
    [InlineData(new[] { "--routines-dry-run" }, true)]
    [InlineData(new[] { "--minimized", "--ROUTINES-DRY-RUN" }, true)]
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new[] { "--routines-dry-run=false" }, false)]
    [InlineData(new[] { "routines-dry-run" }, false)]
    [InlineData(new string[0], false)]
    public void OnlyTheExactSwitchEnablesDryRun(string[] args, bool expected)
    {
        Assert.Equal(expected, RoutineDryRunMode.FromCommandLine(args).IsEnabled);
    }

    [Fact]
    public void NullArgsAreOff()
    {
        Assert.False(RoutineDryRunMode.FromCommandLine(null).IsEnabled);
        Assert.False(RoutineDryRunMode.Off.IsEnabled);
    }

    [Fact]
    public void NothingCanFlipItAfterConstruction()
    {
        // No setter, no public constructor: argv is the only way in (T23).
        var type = typeof(RoutineDryRunMode);
        Assert.Null(type.GetProperty(nameof(RoutineDryRunMode.IsEnabled))!.SetMethod);
        Assert.Empty(type.GetConstructors());
    }

    [Fact]
    public async Task ADryRunCountsDownAndLogsInsteadOfIssuing()
    {
        var bench = new RoutineTestBench(RoutineDryRunMode.FromCommandLine(["--routines-dry-run"]));

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Shutdown)));
        await bench.WaitForCountdownAsync();
        bench.ElapseCountdown();
        var result = await run;

        Assert.Empty(bench.Power.Issued);
        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Equal(RoutineReasonCodes.DryRun, result.ReasonCode);
        Assert.True(Assert.Single(bench.Ui.Shown).DryRun);
    }

    [Fact]
    public async Task DryRunLeavesNonDestructiveStepsAlone()
    {
        var bench = new RoutineTestBench(RoutineDryRunMode.FromCommandLine(["--routines-dry-run"]));

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock)));

        Assert.Equal(RoutineReasonCodes.Ok, result.ReasonCode);
        Assert.Single(bench.Power.Issued);
    }
}
