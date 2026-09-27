using System.Runtime.CompilerServices;
using Remex.Agent.Handlers;
using Remex.Agent.Services.Routines;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Models;
using Remex.Core.Routines;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>Routines spec §13.2 <c>RoutineSyncRevisionTests</c>: §7.4.2 steps 3-10 and T4.</summary>
public sealed class RoutineSyncRevisionTests
{
    [Fact]
    public async Task AGreaterRevisionIsValidatedSavedAndAnsweredOk()
    {
        var bench = new RoutineHostTestBench();

        var result = await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.Ok, result.Status);
        Assert.Equal(1, result.StoredRevision);
        Assert.True(Assert.Single(result.Results!).Accepted);
        Assert.Contains(Id(1), bench.Files.Files[RoutineHostStore.FileName], StringComparison.Ordinal);
        Assert.Equal(Id(1), Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!).Id);
    }

    [Fact]
    public async Task AnOlderRevisionIsStaleAndChangesNothing()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(5, routines: Routine(1, IdleTrigger(), NotifyPc()));

        var result = await bench.SyncAsync(4);

        Assert.Equal(RoutineSyncStatuses.StaleRevision, result.Status);
        Assert.Equal(5, result.StoredRevision);
        Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!);
    }

    [Fact]
    public async Task TheSameRevisionWithTheSameContentResendsTheStoredResult()
    {
        var bench = new RoutineHostTestBench();
        var routine = Routine(1, IdleTrigger(), NotifyPc());
        await bench.SyncAsync(2, routines: routine);
        var writes = bench.Files.Writes;

        var retry = await bench.SyncAsync(2, routines: routine);

        Assert.Equal(RoutineSyncStatuses.Ok, retry.Status);
        Assert.True(Assert.Single(retry.Results!).Accepted);
        Assert.Equal(2, retry.StoredRevision);
        // Only last-seen moved; the set itself was not re-applied.
        Assert.Equal(writes + 1, bench.Files.Writes);
    }

    [Fact]
    public async Task TheSameRevisionWithDifferentContentIsAConflict()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(2, routines: Routine(1, IdleTrigger(), NotifyPc()));

        var result = await bench.SyncAsync(2, routines: Routine(2, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.RevisionConflict, result.Status);
        Assert.Equal(Id(1), Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!).Id);
    }

    [Fact]
    public async Task AFailedSaveAnswersInternalErrorAndKeepsTheOldSet()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        bench.Files.FailWrites = true;

        var result = await bench.SyncAsync(2, routines: Routine(2, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.InternalError, result.Status);
        Assert.Equal(1, result.StoredRevision);
        Assert.Equal(Id(1), Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!).Id);
    }

    [Fact]
    public async Task ARejectedRoutineIsNotStoredAndItsOldVersionIsRemoved()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        var result = await bench.SyncAsync(2, routines: Routine(1, IdleTrigger(), Power("WAKEONLAN")) with { Revision = 2 });

        Assert.Equal(RoutineSyncStatuses.Partial, result.Status);
        Assert.False(Assert.Single(result.Results!).Accepted);
        Assert.Empty(bench.Store.Current.Owner(Owner)!.Routines!);
    }

    [Fact]
    public async Task ANewerSchemaIsRefusedWhole()
    {
        var bench = new RoutineHostTestBench();

        var result = await bench.Sync.ProcessAsync(Owner, Payload(1, false, Routine(1, IdleTrigger(), NotifyPc())) with
        {
            SchemaVersion = RoutineSchema.CurrentVersion + 1,
        });

        Assert.Equal(RoutineSyncStatuses.SchemaTooNew, result.Status);
        Assert.Null(bench.Store.Current.Owner(Owner));
    }

    [Fact]
    public async Task AnOversizedSyncIsRefusedBeforeAnythingElse()
    {
        var bench = new RoutineHostTestBench();
        var huge = Enumerable.Range(1, 400).Select(i => Routine(i, IdleTrigger(), NotifyPc(new string('x', 40)))).ToArray();
        var replies = new List<RemexMessage>();

        await bench.Sync.HandleSyncAsync(Owner, Payload(1, false, huge), m => { replies.Add(m); return Task.CompletedTask; });

        Assert.Equal(RoutineSyncStatuses.PayloadTooLarge, Assert.Single(replies).RoutineSyncResult!.Status);
        Assert.Null(bench.Store.Current.Owner(Owner));
    }

    [Fact]
    public async Task ForgetDeletesTheOwnersRoutinesAndRuns()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        var result = await bench.Sync.ProcessAsync(Owner, Payload(2, false) with { Forget = true });

        Assert.Equal(RoutineSyncStatuses.Ok, result.Status);
        Assert.Null(bench.Store.Current.Owner(Owner));
        Assert.Empty(bench.Runs.Query(Owner, null));
    }

    [Fact]
    public async Task TheReplyIsFollowedByTheHistoryThePhoneHasNotSeen()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;
        var replies = new List<RemexMessage>();

        await bench.Sync.HandleSyncAsync(Owner, Payload(2, false, Routine(1, IdleTrigger(), NotifyPc())) with { RunCursor = 0 },
            m => { replies.Add(m); return Task.CompletedTask; });

        Assert.Equal(MessageTypes.RoutineSyncResult, replies[0].Type);
        var report = Assert.Single(replies.Skip(1)).RoutineRunReport!;
        Assert.False(report.Live);
        Assert.Null(Assert.Single(report.Runs!).OwnerClientId);
    }
}

/// <summary>Routines spec §13.2 <c>RoutineHostValidationTests</c>: the host checks of §7.4.2 step 7.</summary>
public sealed class RoutineHostValidationTests
{
    private static async Task<string?> RejectionOf(RoutineHostTestBench bench, Routine routine)
    {
        var result = await bench.SyncAsync(1, routines: routine);
        var item = Assert.Single(result.Results!);
        return item.Accepted ? null : item.ReasonCode;
    }

    [Fact]
    public async Task ARoutineForAnotherPcIsWrongPc() =>
        Assert.Equal(RoutineReasonCodes.WrongPc,
            await RejectionOf(new RoutineHostTestBench(), Routine(1, IdleTrigger(), NotifyPc()) with { HostIdentity = "0123456789abcdef" }));

    [Fact]
    public async Task APhoneTriggerIsNotAPcRoutine() =>
        Assert.Equal(RoutineReasonCodes.TriggerNotPc,
            await RejectionOf(new RoutineHostTestBench(), Routine(1, new RoutineTrigger { Type = RoutineTriggerTypes.Manual }, NotifyPc())));

    [Fact]
    public async Task AVerbThisPcDoesNotAdvertiseIsPowerUnsupported()
    {
        var bench = new RoutineHostTestBench();
        bench.Core.AdvertisedVerbs.Remove(RoutinePowerVerbs.Hibernate);

        Assert.Equal(RoutineReasonCodes.PowerUnsupported,
            await RejectionOf(bench, Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Hibernate))));
    }

    [Fact]
    public async Task AnAppIdNotInTheLauncherListIsLaunchNotAllowed() =>
        Assert.Equal(RoutineReasonCodes.LaunchNotAllowed, await RejectionOf(new RoutineHostTestBench(),
            Routine(1, IdleTrigger(), new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = Guid.NewGuid().ToString() })));

    [WindowsOnlyFact("UNC paths are a Windows construct; IsRejectedNetworkPath is the Windows guard")]
    public async Task ALauncherEntryOnANetworkPathIsLaunchNotAllowed()
    {
        var bench = new RoutineHostTestBench();
        var networkApp = Guid.NewGuid();
        bench.Core.Launchers.Entries.Add(new AppEntry(networkApp, "Share", @"\\server\share\app.exe", "#000000", null));

        Assert.Equal(RoutineReasonCodes.LaunchNotAllowed, await RejectionOf(bench,
            Routine(1, IdleTrigger(), new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = networkApp.ToString() })));
    }

    [Fact]
    public async Task AKnownLocalAppIsAccepted()
    {
        var bench = new RoutineHostTestBench();
        var app = Guid.NewGuid();
        bench.Core.Launchers.Entries.Add(new AppEntry(app, "Steam", Path.Combine(Path.GetTempPath(), "steam.exe"), "#000000", null));

        Assert.Null(await RejectionOf(bench, Routine(1, IdleTrigger(), new RoutineStep { Type = RoutineStepTypes.LaunchApp, AppId = app.ToString() })));
    }

    [Fact]
    public async Task TriggersWithoutASourceOnThisPcAreRejected()
    {
        var noIdle = new RoutineHostTestBench();
        noIdle.Availability.SetIdleSource(null);
        var noSession = new RoutineHostTestBench();
        noSession.Availability.SetSessionSource(null);

        Assert.Equal(RoutineReasonCodes.IdleSourceUnavailable, await RejectionOf(noIdle, Routine(1, IdleTrigger(), NotifyPc())));
        Assert.Equal(RoutineReasonCodes.SessionSourceUnavailable, await RejectionOf(noSession, Routine(2, SessionTrigger(locked: true), NotifyPc())));
        Assert.Equal(RoutineReasonCodes.SensorUnavailable, await RejectionOf(new RoutineHostTestBench(), Routine(3,
            new RoutineTrigger { Type = RoutineTriggerTypes.PcSensor, SensorId = "cpu", Direction = "above", Threshold = 80, SustainSeconds = 60 },
            NotifyPc())));
    }

    [Fact]
    public async Task PhoneOnlyStepsAreNotAllowedOnThePc() =>
        Assert.Equal(RoutineReasonCodes.StepNotAllowedOnPc, await RejectionOf(new RoutineHostTestBench(),
            Routine(1, IdleTrigger(), new RoutineStep { Type = RoutineStepTypes.WaitOnline, TimeoutSeconds = 60 })));
}

/// <summary>Routines spec §13.2 <c>RoutinesSyncGateTests</c>, the S4a half: the new phone→host types are gated.</summary>
public sealed class RoutinesSyncGateTests
{
    [Theory]
    [InlineData(MessageTypes.RoutinesSync)]
    [InlineData(MessageTypes.RoutineRunRequest)]
    [InlineData(MessageTypes.RoutineNotifyAck)]
    public void RoutineMessagesRequirePairing(string type)
    {
        Assert.True(PingPongHandler.RequiresPairing(type));
        Assert.False(PingPongHandler.RequiresLoopback(type));
    }

    [Fact]
    public void SyncAndRunRequestAreDetachedAndScopedToAProvenIdentity()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Handlers", "PingPongHandler.cs"));
        var start = source.IndexOf("case MessageTypes.RoutinesSync:", StringComparison.Ordinal);
        var end = source.IndexOf("case MessageTypes.RoutineNotifyAck:", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the routine cases moved; update this pin");
        var block = source[start..end];

        Assert.Equal(2, CountOf(block, "RunDetachedAsync("));
        Assert.Equal(2, CountOf(block, "!identityProven"));
        Assert.Contains("SetSupportsRoutines(session, capabilities.SupportsRoutines)", source, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}

/// <summary>Routines spec §13.2 <c>RoutineRateLimitTests</c>, sync coalescing: one per 2 s, the latest wins.</summary>
public sealed class RoutineSyncCoalescingTests
{
    [Fact]
    public async Task ABurstIsCoalescedToTheLatestRevision()
    {
        var bench = new RoutineHostTestBench();
        var replies = new List<RoutineSyncResultPayload>();
        Task Send(RemexMessage m)
        {
            lock (replies)
            {
                replies.Add(m.RoutineSyncResult!);
            }

            return Task.CompletedTask;
        }

        await bench.Sync.HandleSyncAsync(Owner, Payload(1, false), Send);
        var second = bench.Sync.HandleSyncAsync(Owner, Payload(2, false), Send);
        await bench.WaitForTimerAsync();
        await bench.Sync.HandleSyncAsync(Owner, Payload(3, false), Send);

        bench.Time.Advance(RoutineSyncHandler.MinInterval);
        await second;

        Assert.Equal([1L, 3L], replies.Select(r => r.Revision).ToArray());
        Assert.Equal(3, bench.Store.Current.Owner(Owner)!.Revision);
    }
}
