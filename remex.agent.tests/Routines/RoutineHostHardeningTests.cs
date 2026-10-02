using System.Security.AccessControl;
using System.Security.Principal;
using Remex.Agent.Services.Routines;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>Nothing is applied before the stores are loaded and the trigger sources probed.</summary>
public sealed class RoutineHostReadinessTests
{
    private static Func<RemexMessage, Task> Collect(List<RemexMessage> into) => m =>
    {
        lock (into)
        {
            into.Add(m);
        }

        return Task.CompletedTask;
    };

    [Fact]
    public async Task ASyncDuringStartUpWaitsForItThenApplies()
    {
        var bench = new RoutineHostTestBench(ready: false);
        var replies = new List<RemexMessage>();

        var pending = bench.Sync.HandleSyncAsync(Owner, Payload(1, false, Routine(1, IdleTrigger(), NotifyPc())), Collect(replies));
        await Task.Delay(20);
        Assert.False(pending.IsCompleted);
        Assert.Null(bench.Store.Current.Owner(Owner));

        await bench.InitializeAsync();
        await pending;

        Assert.Equal(RoutineSyncStatuses.Ok, Assert.Single(replies).RoutineSyncResult!.Status);
    }

    [Fact]
    public async Task ASyncThatOutwaitsStartUpIsAskedToRetryAndChangesNothing()
    {
        var bench = new RoutineHostTestBench(ready: false);
        bench.Files.Files[RoutineHostStore.FileName] = "{\"fileVersion\":1,\"owners\":{}}";
        var replies = new List<RemexMessage>();

        var pending = bench.Sync.HandleSyncAsync(Owner, Payload(1, false, Routine(1, IdleTrigger(), NotifyPc())), Collect(replies));
        await bench.WaitForTimerAsync();
        bench.Time.Advance(RoutineHostReadiness.MaxWait);
        await pending;

        var result = Assert.Single(replies).RoutineSyncResult!;
        Assert.Equal(RoutineSyncStatuses.RateLimited, result.Status);
        Assert.Empty(result.Results!);
        Assert.Equal("{\"fileVersion\":1,\"owners\":{}}", bench.Files.Files[RoutineHostStore.FileName]);
        Assert.Equal(0, bench.Files.Writes);
    }

    [Fact]
    public async Task ARunRequestThatOutwaitsStartUpIsSkippedRetryably()
    {
        var bench = new RoutineHostTestBench(ready: false);
        var replies = new List<RemexMessage>();

        var pending = bench.Messages.HandleRunRequestAsync(
            Owner, new RoutineRunRequestPayload { RunId = Guid.NewGuid().ToString(), RoutineId = Id(1) }, Collect(replies));
        await bench.WaitForTimerAsync();
        bench.Time.Advance(RoutineHostReadiness.MaxWait);
        await pending;

        var run = Assert.Single(Assert.Single(replies).RoutineRunReport!.Runs!);
        Assert.Equal(RoutineRunOutcomes.Skipped, run.Outcome);
        Assert.Equal(RoutineReasonCodes.RateLimited, run.ReasonCode);
        Assert.Empty(bench.Runs.Query(null, null));
    }

    [Fact]
    public async Task AnEqualRevisionIsRevalidatedWhenItsRejectionWasOnlyASourceNotYetProbed()
    {
        var bench = new RoutineHostTestBench();
        bench.Availability.SetIdleSource(null);
        var routine = Routine(1, IdleTrigger(), NotifyPc());

        var early = await bench.SyncAsync(4, routines: routine);
        Assert.Equal(RoutineReasonCodes.IdleSourceUnavailable, Assert.Single(early.Results!).ReasonCode);

        bench.Availability.SetIdleSource(bench.IdleSource.Id);
        var retry = await bench.SyncAsync(4, routines: routine);

        Assert.Equal(RoutineSyncStatuses.Ok, retry.Status);
        Assert.True(Assert.Single(retry.Results!).Accepted);
        Assert.Equal(Id(1), Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!).Id);
    }

    [Fact]
    public async Task APermanentRejectionIsStillReplayedForAnEqualRevision()
    {
        var bench = new RoutineHostTestBench();
        var routine = Routine(1, IdleTrigger(), Power("WAKEONLAN"));
        await bench.SyncAsync(4, routines: routine);
        var writes = bench.Files.Writes;

        var retry = await bench.SyncAsync(4, routines: routine);

        Assert.Equal(RoutineSyncStatuses.Partial, retry.Status);
        Assert.Equal(writes + 1, bench.Files.Writes); // last-seen only
    }
}

/// <summary>A file that could not be set aside is never overwritten (atomic-save rules, §6.7).</summary>
public sealed class RoutineStoreQuarantineFailureTests
{
    [Fact]
    public async Task SavesAreRefusedUntilTheOriginalCanBeSetAside()
    {
        var bench = new RoutineHostTestBench();
        bench.Files.Files[RoutineHostStore.FileName] = "{ corrupt";
        bench.Files.FailQuarantine = true;
        await bench.InitializeAsync();

        var refused = await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.InternalError, refused.Status);
        Assert.Equal("{ corrupt", bench.Files.Files[RoutineHostStore.FileName]);
        Assert.Single(bench.Service.GetSnapshot().Warnings);

        bench.Files.FailQuarantine = false;
        var accepted = await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.Ok, accepted.Status);
        Assert.Equal("{ corrupt", bench.Files.Files[Assert.Single(bench.Files.Quarantined)]);
    }

    [Fact]
    public async Task AnOriginalThatReadsBackValidIsAdoptedNotOverwritten()
    {
        var source = new RoutineHostTestBench();
        await source.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        var good = source.Files.Files[RoutineHostStore.FileName];

        var bench = new RoutineHostTestBench();
        bench.Files.Files[RoutineHostStore.FileName] = good;
        bench.Files.Untrusted[RoutineHostStore.FileName] = "transient";
        bench.Files.FailQuarantine = true;
        await bench.InitializeAsync();
        Assert.Null(bench.Store.Current.Owner(Owner));

        bench.Files.Untrusted.Clear();
        await bench.SyncAsync(2, owner: OtherOwner, routines: Routine(2, IdleTrigger(), NotifyPc()));

        Assert.NotNull(bench.Store.Current.Owner(Owner));
        Assert.NotNull(bench.Store.Current.Owner(OtherOwner));
    }

    [Fact]
    public async Task HistoryThatCouldNotBeSetAsideIsNotOverwritten()
    {
        var bench = new RoutineHostTestBench();
        bench.Files.Files[RoutineRunStore.FileName] = "{ corrupt";
        bench.Files.FailQuarantine = true;
        await bench.InitializeAsync();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal("{ corrupt", bench.Files.Files[RoutineRunStore.FileName]);
    }
}

/// <summary>T14: a routine state file an ordinary user could have planted is never loaded.</summary>
public sealed class RoutineStoreTrustTests
{
    [Fact]
    public async Task AnUntrustedStoreIsSetAsideAndTheHostStartsEmptyWithAWarning()
    {
        var source = new RoutineHostTestBench();
        await source.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        var bench = new RoutineHostTestBench();
        bench.Files.Files[RoutineHostStore.FileName] = source.Files.Files[RoutineHostStore.FileName];
        bench.Files.Files[RoutineRunStore.FileName] = "{\"fileVersion\":1,\"nextSeq\":1,\"runs\":[]}";
        bench.Files.Untrusted[RoutineHostStore.FileName] = "user owned";
        bench.Files.Untrusted[RoutineRunStore.FileName] = "user writable";

        await bench.InitializeAsync();

        Assert.Empty(bench.Store.Current.Owners!);
        Assert.Equal(2, bench.Files.Quarantined.Count);
        Assert.Equal(2, bench.Service.GetSnapshot().Warnings.Count);
    }

    [WindowsOnlyFact("NTFS ownership and ACLs")]
    public void AFileOwnedByAnOrdinaryUserIsRefused()
    {
        var path = Path.GetTempFileName();
        try
        {
            // A file the current (test) user created: owned by that user, as a planted file would be.
            var security = new FileInfo(path).GetAccessControl();

            Assert.NotNull(RoutineFilePermissions.CheckWindowsSecurity(security));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [WindowsOnlyFact("NTFS ownership and ACLs")]
    public void AnAdminOwnedFileThatUsersCanWriteIsRefused()
    {
        var security = RoutineFilePermissions.BuildWindowsSecurity(ownerIsAdministrators: true);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));

        Assert.NotNull(RoutineFilePermissions.CheckWindowsSecurity(security));
    }

    [WindowsOnlyFact("NTFS ownership and ACLs")]
    public void TheAgentsOwnDescriptorIsTrustedAndReadOnlyUsersAreFine()
    {
        var security = RoutineFilePermissions.BuildWindowsSecurity(ownerIsAdministrators: true);
        Assert.Null(RoutineFilePermissions.CheckWindowsSecurity(security));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.Null(RoutineFilePermissions.CheckWindowsSecurity(security));
    }
}

/// <summary>A revoked owner can never be re-created by a sync that was queued or in flight.</summary>
public sealed class RoutineRevokeRaceTests
{
    [Fact]
    public async Task ASyncArrivingAfterTheUnpairStoresNothing()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        // Revocation order: the credential goes first, then the routines.
        bench.Core.Owners.Paired.Remove(Owner);
        await bench.Service.ForgetOwnerAsync(Owner);
        var late = await bench.SyncAsync(2, routines: Routine(1, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.InternalError, late.Status);
        Assert.Null(bench.Store.Current.Owner(Owner));
    }
}

/// <summary>
/// RemEx-pp4cm C2: a forget that lands while a run is still saving its first record must wait for that run,
/// or the run's final save puts history straight back for an owner that was just forgotten.
/// </summary>
public sealed class RoutineOwnerCancelWindowTests
{
    [Fact]
    public async Task CancellingAnOwnerMidStartWaitsForTheRunToFinish()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bench.Files.HoldWrites = hold;
        var start = bench.StartAsync(1);
        Assert.True(bench.Runner.IsRunning(Owner, Id(1)));

        var cancel = bench.Runner.CancelOwnerAsync(Owner);
        await Task.Delay(50);
        Assert.False(cancel.IsCompleted, "the owner cancel returned while the run was still starting");

        bench.Files.HoldWrites = null;
        hold.SetResult();
        var handle = await start;
        await cancel;

        Assert.False(bench.Runner.IsRunning(Owner, Id(1)));
        var final = await handle.Completion;
        Assert.Equal(RoutineRunOutcomes.Cancelled, final.Outcome);
    }

    [Fact]
    public async Task ARunWhoseStartFailsReleasesItsSlotAndItsWaiters()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        bench.Channel.Reachable.Add(Owner);
        bench.Channel.FailSends = true;

        var handle = await bench.StartAsync(1);

        Assert.Equal(RoutineRunOutcomes.Failed, handle.Initial.Outcome);
        Assert.False(bench.Runner.IsRunning(Owner, Id(1)), "a failed start kept reading as already running");
        var cancel = bench.Runner.CancelOwnerAsync(Owner);
        Assert.True(cancel.IsCompleted);
        await cancel;
    }
}

/// <summary>T17: a run request can never be answered with another owner's record.</summary>
public sealed class RoutineRunRequestIsolationTests
{
    [Fact]
    public async Task ReusingAnotherOwnersRunIdIsRefusedOnEveryResend()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, owner: OtherOwner, routines: Routine(2, IdleTrigger(), NotifyPc()));
        var theirRunId = Guid.NewGuid().ToString();
        await bench.Messages.HandleRunRequestAsync(
            OtherOwner, new RoutineRunRequestPayload { RunId = theirRunId, RoutineId = Id(2) }, _ => Task.CompletedTask);
        await WaitUntilAsync(() => bench.Runs.Find(theirRunId)?.EndedAtUnixMs is not null, "the other owner's run never ended");

        var replies = new List<RemexMessage>();
        for (var i = 0; i < 2; i++)
        {
            await bench.Messages.HandleRunRequestAsync(
                Owner, new RoutineRunRequestPayload { RunId = theirRunId, RoutineId = Id(2) }, m => { replies.Add(m); return Task.CompletedTask; });
        }

        Assert.Empty(replies);
        Assert.Equal(OtherOwner, bench.Runs.Find(theirRunId)!.OwnerClientId);
    }
}
