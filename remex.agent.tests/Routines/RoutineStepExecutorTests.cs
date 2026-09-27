using System.ComponentModel;
using Remex.Core.Models;
using Remex.Core.Routines;
using Remex.Core.Services.Command;
using Remex.Desktop.Services;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>RoutineStepExecutorTests</c> and <c>RoutineRunRevalidationTests</c> (T6):
/// each host step takes the existing path it is specified to, with no network hop, and everything is
/// revalidated at run time.
/// </summary>
public sealed class RoutineStepExecutorTests
{
    [Theory]
    [InlineData(RoutinePowerVerbs.Lock)]
    [InlineData(RoutinePowerVerbs.MonitorOff)]
    public async Task NonDestructivePowerGoesThroughTheSharedVerbTableWithoutACountdown(string verb)
    {
        var bench = new RoutineTestBench();

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(RoutineTestBench.PowerStep(verb)));

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Equal(RoutineReasonCodes.Ok, result.ReasonCode);
        Assert.Equal([(verb, (int?)null)], bench.Power.Issued);
        Assert.Empty(bench.Ui.Shown);
        Assert.False(result.CountdownShown);
    }

    [Fact]
    public async Task DelaySecondsIsPassedToTheVerb()
    {
        var bench = new RoutineTestBench();
        var step = RoutineTestBench.PowerStep(RoutinePowerVerbs.Shutdown) with { DelaySeconds = 30 };

        var run = bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));
        await bench.WaitForCountdownAsync();
        bench.ElapseCountdown();
        var result = await run;

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Equal([(RoutinePowerVerbs.Shutdown, (int?)30)], bench.Power.Issued);
    }

    [Fact]
    public async Task ARevokedOwnerIsRefusedBeforeAnythingRuns()
    {
        var bench = new RoutineTestBench();
        bench.Owners.Paired.Remove(RoutineTestBench.Owner);

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock)));

        Assert.Equal(RoutineReasonCodes.PcNotPaired, result.ReasonCode);
        Assert.Empty(bench.Power.Issued);
    }

    [Fact]
    public async Task AVerbThePcNoLongerAdvertisesIsPowerUnsupported()
    {
        var bench = new RoutineTestBench();
        bench.AdvertisedVerbs.Remove(RoutinePowerVerbs.Hibernate);

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Hibernate)));

        Assert.Equal(RoutineReasonCodes.PowerUnsupported, result.ReasonCode);
        Assert.Empty(bench.Power.Issued);
        Assert.Empty(bench.Ui.Shown);
    }

    [Fact]
    public async Task WakeOnLanIsNeverIssued()
    {
        var bench = new RoutineTestBench();
        bench.AdvertisedVerbs.Add(RoutinePowerVerbs.ReservedWakeOnLan);

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.ReservedWakeOnLan)));

        Assert.Equal(RoutineStepOutcomes.Failed, result.Outcome);
        Assert.Empty(bench.Power.Issued);
    }

    [Theory]
    [InlineData(RoutineStepTypes.Wake)]
    [InlineData(RoutineStepTypes.WaitOnline)]
    [InlineData(RoutineStepTypes.Delay)]
    public async Task PhoneOnlyStepsAreRefused(string type)
    {
        var bench = new RoutineTestBench();

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(new RoutineStep { Type = type }));

        Assert.Equal(RoutineReasonCodes.StepNotAllowedOnPc, result.ReasonCode);
    }

    [Fact]
    public async Task NotifyToThePhoneIsNotAHostStep()
    {
        var bench = new RoutineTestBench();
        var step = new RoutineStep { Type = RoutineStepTypes.Notify, Target = RoutineNotifyTargets.Phone, Title = "Hi" };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineReasonCodes.StepNotAllowedOnPc, result.ReasonCode);
        Assert.Empty(bench.Ui.Notifications);
    }

    [Fact]
    public async Task AnUnknownStepTypeIsUnsupported()
    {
        var bench = new RoutineTestBench();

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(new RoutineStep { Type = "teleport" }));

        Assert.Equal(RoutineReasonCodes.UnsupportedStep, result.ReasonCode);
    }

    [Fact]
    public async Task AStepThatFailsTheSchemaRulesIsRejectedWithTheValidatorsCode()
    {
        var bench = new RoutineTestBench();
        var step = new RoutineStep { Type = RoutineStepTypes.Media, MediaAction = "louder" };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineStepOutcomes.Failed, result.Outcome);
        Assert.Equal(RoutineReasonCodes.InvalidField, result.ReasonCode);
        Assert.Empty(bench.Media.Sent);
    }

    [Fact]
    public async Task LaunchAppResolvesTheIdAndLaunchesTheHostsOwnPath()
    {
        var bench = new RoutineTestBench();
        var id = Guid.NewGuid();
        bench.Launchers.Entries.Add(new AppEntry(id, "Steam", @"C:\Games\Steam\steam.exe", "#000000", null));
        var step = new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = id.ToString(), AppLabel = "Something else" };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Equal([@"C:\Games\Steam\steam.exe"], bench.Launcher.Launched);
    }

    [Fact]
    public async Task AnAppRemovedFromTheLauncherListIsNotAllowed()
    {
        var bench = new RoutineTestBench();
        var step = new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = Guid.NewGuid().ToString() };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineReasonCodes.LaunchNotAllowed, result.ReasonCode);
        Assert.Empty(bench.Launcher.Launched);
    }

    [Fact]
    public async Task APathTheLauncherGuardsRejectIsNotAllowed()
    {
        var bench = new RoutineTestBench();
        var id = Guid.NewGuid();
        bench.Launchers.Entries.Add(new AppEntry(id, "Share", @"\\server\share\x.exe", "#000000", null));
        bench.Launcher.Throw = new UnauthorizedAccessException("network path");
        var step = new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = id.ToString() };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineReasonCodes.LaunchNotAllowed, result.ReasonCode);
    }

    [Fact]
    public async Task ALaunchThatThrowsIsLaunchFailedAndRaisesAProblem()
    {
        var bench = new RoutineTestBench();
        var id = Guid.NewGuid();
        bench.Launchers.Entries.Add(new AppEntry(id, "Gone", @"C:\gone.exe", "#000000", null));
        bench.Launcher.Throw = new FileNotFoundException("missing");
        var step = new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = id.ToString() };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineReasonCodes.LaunchFailed, result.ReasonCode);
        Assert.Contains(bench.Ui.Notifications, n => n.Importance == NotificationImportance.Problem);
    }

    [Theory]
    [InlineData(RoutineMediaActions.PlayPause, 0xB3)]
    [InlineData(RoutineMediaActions.Next, 0xB0)]
    [InlineData(RoutineMediaActions.Previous, 0xB1)]
    public async Task MediaEmitsThePhonesMediaKey(string action, int expectedKey)
    {
        var bench = new RoutineTestBench();
        var step = new RoutineStep { Type = RoutineStepTypes.Media, MediaAction = action };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.Equal([expectedKey], bench.Media.Sent);
    }

    [Fact]
    public async Task MediaWithoutAnInputBackendIsMediaUnavailable()
    {
        var bench = new RoutineTestBench();
        bench.Media.Available = false;
        var step = new RoutineStep { Type = RoutineStepTypes.Media, MediaAction = RoutineMediaActions.PlayPause };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineReasonCodes.MediaUnavailable, result.ReasonCode);
    }

    [Fact]
    public async Task NotifyPcRoutesThroughTheNotificationRouterWithSanitizedText()
    {
        var bench = new RoutineTestBench();
        var step = new RoutineStep
        {
            Type = RoutineStepTypes.Notify, Target = RoutineNotifyTargets.Pc, Title = "Movie night", Body = "Lights are down",
        };

        var result = await bench.Executor.ExecuteAsync(RoutineTestBench.Execution(step));

        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        var shown = Assert.Single(bench.Ui.Notifications);
        Assert.Equal(NotificationImportance.Outcome, shown.Importance);
        Assert.Equal("Movie night", shown.Title);
        Assert.Equal("Lights are down", shown.Message);
    }

    [Fact]
    public async Task AVerbTheOsRefusesIsPowerDeniedByOs()
    {
        var bench = new RoutineTestBench();
        bench.Power.Throw = new Win32Exception(5);

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock)));

        Assert.Equal(RoutineReasonCodes.PowerDeniedByOs, result.ReasonCode);
        Assert.Contains(bench.Ui.Notifications, n => n.Importance == NotificationImportance.Problem);
    }

    [Fact]
    public async Task AVerbThatReportsFailureIsPowerFailed()
    {
        var bench = new RoutineTestBench();
        bench.Power.Respond = _ => new SharedCommandVerbs.Outcome(false, "nope");

        var result = await bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.MonitorOff)));

        Assert.Equal(RoutineReasonCodes.PowerFailed, result.ReasonCode);
    }

    [Fact]
    public async Task ADestructiveVerbAnnouncesSucceededBeforeItIsIssued()
    {
        var bench = new RoutineTestBench();
        var issuedWhenAnnounced = -1;

        var run = bench.Executor.ExecuteAsync(
            RoutineTestBench.Execution(RoutineTestBench.PowerStep(RoutinePowerVerbs.Sleep)),
            early =>
            {
                Assert.Equal(RoutineStepOutcomes.Succeeded, early.Outcome);
                issuedWhenAnnounced = bench.Power.Issued.Count;
                return Task.CompletedTask;
            });
        await bench.WaitForCountdownAsync();
        bench.ElapseCountdown();
        var result = await run;

        Assert.Equal(0, issuedWhenAnnounced);
        Assert.Single(bench.Power.Issued);
        Assert.Equal(RoutineStepOutcomes.Succeeded, result.Outcome);
        Assert.True(result.CountdownShown);
    }

    [Fact]
    public void MediaKeysAreTheRemoteControlScreensVirtualKeys()
    {
        Assert.Equal(0xB3, Remex.Agent.Services.Routines.RoutineStepExecutor.MediaVirtualKey(RoutineMediaActions.PlayPause));
        Assert.Null(Remex.Agent.Services.Routines.RoutineStepExecutor.MediaVirtualKey("stop"));
    }
}
