using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Routines;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// Routines spec §13.2 <c>RoutineNotifyQueueTests</c> (§7.3.5, Q6, R-SYS-26): live versus queued, the 20
/// cap, the one-hour expiry record, ack removal scoped to the owner (T17), countdowns never queued, and the
/// queue surviving an agent restart.
/// </summary>
public sealed class RoutineNotifyQueueTests
{
    private static List<RoutineNotifyPayload> NotifiesTo(RoutineHostTestBench bench, string owner) =>
        bench.Channel.Sent.Where(s => s.ClientId == owner && s.Message.Type == MessageTypes.RoutineNotify)
            .Select(s => s.Message.RoutineNotify!)
            .ToList();

    private static RoutineNotifyPayload Message(RoutineHostTestBench bench, string kind = RoutineNotifyKinds.Step, string? runId = null)
    {
        var now = bench.Time.GetUtcNow().ToUnixTimeMilliseconds();
        return new RoutineNotifyPayload
        {
            NotifyId = Guid.NewGuid().ToString(),
            Kind = kind,
            RoutineId = Id(1),
            RoutineName = "Routine 1",
            RunId = runId ?? Guid.NewGuid().ToString(),
            Title = "Hi",
            Body = "Body",
            QueuedAtUnixMs = now,
            ExpiresAtUnixMs = now + (long)RoutineNotifyQueue.Expiry.TotalMilliseconds,
        };
    }

    [Fact]
    public async Task AConnectedOwnerGetsItLiveAndItStaysUntilAcknowledged()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPhone("hello")));

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineStepStatuses.Succeeded, final.Steps![0].Status);
        Assert.DoesNotContain(RoutineRunAttributes.NotifyQueued, final.Attributes ?? []);
        var sent = Assert.Single(NotifiesTo(bench, Owner));
        Assert.Equal("hello", sent.Title);
        Assert.Single(bench.Queue!.Pending(Owner));

        await bench.Messages.HandleNotifyAckAsync(Owner, new RoutineNotifyAckPayload { NotifyIds = [sent.NotifyId!] });
        Assert.Empty(bench.Queue.Pending(Owner));
    }

    [Fact]
    public async Task AnOfflineOwnersMessageIsQueuedAndPersisted()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPhone("later")));

        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        Assert.Equal(RoutineStepStatuses.Succeeded, final.Steps![0].Status);
        Assert.Contains(RoutineRunAttributes.NotifyQueued, final.Attributes!);
        Assert.Single(bench.Queue!.Pending(Owner));
        Assert.Contains("later", bench.Files.Files[RoutineNotifyQueue.FileName], StringComparison.Ordinal);
        Assert.Empty(NotifiesTo(bench, Owner));
    }

    [Fact]
    public async Task TheQueueIsFlushedAfterTheSyncResultOfTheNextConnection()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPhone("waiting")));
        await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        var replies = new List<RemexMessage>();
        await bench.Sync.HandleSyncAsync(
            Owner,
            Payload(1, false, Routine(1, IdleTrigger(), NotifyPhone("waiting"))),
            message =>
            {
                replies.Add(message);
                return Task.CompletedTask;
            });

        var syncAt = replies.FindIndex(m => m.Type == MessageTypes.RoutineSyncResult);
        var notifyAt = replies.FindIndex(m => m.Type == MessageTypes.RoutineNotify);
        Assert.True(syncAt >= 0 && notifyAt > syncAt, "the held message must follow the sync result");
        Assert.Equal("waiting", replies[notifyAt].RoutineNotify!.Title);

        // Sent, not yet acknowledged: still held.
        Assert.True(Assert.Single(bench.Queue!.Pending(Owner)).Sent);
    }

    [Fact]
    public async Task AnUndeliveredMessageExpiresAfterAnHourAndTheRunRecordIsResent()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPhone("gone")));
        var final = await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;

        bench.Channel.Reachable.Add(Owner);
        bench.Time.Advance(RoutineNotifyQueue.Expiry + TimeSpan.FromSeconds(1));
        await bench.Queue!.SweepAsync();

        Assert.Empty(bench.Queue.Pending(Owner));
        var record = bench.Runs.Find(final.RunId!)!;
        Assert.Equal(RoutineStepStatuses.Expired, record.Steps![0].Status);
        Assert.Equal(RoutineReasonCodes.NotifyExpired, record.Steps[0].ReasonCode);
        Assert.True(record.Seq > final.Seq);
        Assert.Contains(bench.Channel.Reports(Owner), r => !r.Live && r.Runs!.Any(x => x.RunId == final.RunId && x.Seq == record.Seq));

        // Nothing expired is ever delivered afterwards.
        Assert.Empty(NotifiesTo(bench, Owner));
    }

    [Fact]
    public async Task AnExpiryDuringARunningRunIsRecordedWhenTheRunEnds()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Delay(600), NotifyPc("after")));
        var handle = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        await bench.WaitForTimerAsync();

        // A held message of this still-running run passes its hour.
        var held = Message(bench, runId: handle.Initial.RunId) with { ExpiresAtUnixMs = bench.Time.GetUtcNow().ToUnixTimeMilliseconds() - 1 };
        await bench.Queue!.NotifyAsync(Owner, held, 1);
        await bench.Queue.SweepAsync();
        Assert.Empty(bench.Queue.Pending(Owner));
        Assert.Contains("deferredExpiries", bench.Files.Files[RoutineNotifyQueue.FileName], StringComparison.Ordinal);
        Assert.Equal(RoutineRunOutcomes.Running, bench.Runs.Find(handle.Initial.RunId!)!.Outcome);

        await bench.AdvanceUntilAsync(handle.Completion, TimeSpan.FromSeconds(30));

        var record = bench.Runs.Find(handle.Initial.RunId!)!;
        Assert.Equal(RoutineRunOutcomes.Succeeded, record.Outcome);
        Assert.Equal(RoutineStepStatuses.Expired, record.Steps![1].Status);
        Assert.Equal(RoutineReasonCodes.NotifyExpired, record.Steps[1].ReasonCode);
        Assert.DoesNotContain("deferredExpiries", bench.Files.Files[RoutineNotifyQueue.FileName], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiryDeferredAtShutdownIsRecordedOnTheNextStart()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Delay(600), NotifyPc("after")));
        var handle = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        await bench.WaitForTimerAsync();
        var held = Message(bench, runId: handle.Initial.RunId) with { ExpiresAtUnixMs = bench.Time.GetUtcNow().ToUnixTimeMilliseconds() - 1 };
        await bench.Queue!.NotifyAsync(Owner, held, 1);
        await bench.Queue.SweepAsync();

        // Restart: history sweeps the run to interrupted, then the queue applies what waited for it.
        var runs = new RoutineRunStore(bench.Files, bench.Time, NullLogger<RoutineRunStore>.Instance);
        await runs.LoadAndSweepAsync();
        var restarted = new RoutineNotifyQueue(bench.Files, runs, bench.Channel, bench.Time, NullLogger<RoutineNotifyQueue>.Instance);
        await restarted.LoadAsync();

        var record = runs.Find(handle.Initial.RunId!)!;
        Assert.Equal(RoutineRunOutcomes.Interrupted, record.Outcome);
        Assert.Equal(RoutineStepStatuses.Expired, record.Steps![1].Status);
    }

    [Fact]
    public async Task AtMostTwentyPerOwnerTheOldestDropped()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        var first = Message(bench);
        await bench.Queue!.NotifyAsync(Owner, first, 0);
        for (var i = 0; i < RoutineNotifyQueue.MaxPerOwner; i++)
        {
            bench.Time.Advance(TimeSpan.FromSeconds(1));
            await bench.Queue.NotifyAsync(Owner, Message(bench), 0);
        }

        var pending = bench.Queue.Pending(Owner);
        Assert.Equal(RoutineNotifyQueue.MaxPerOwner, pending.Count);
        Assert.DoesNotContain(pending, p => p.Notify!.NotifyId == first.NotifyId);
    }

    [Fact]
    public async Task ACountdownIsNeverQueued()
    {
        var bench = new RoutineHostTestBench(withQueue: true);

        var outcome = await bench.Queue!.NotifyAsync(Owner, Message(bench, RoutineNotifyKinds.Countdown));

        Assert.Equal(RoutinePhoneNotifyOutcome.NotDelivered, outcome);
        Assert.Empty(bench.Queue.Pending(Owner));
    }

    [Fact]
    public async Task TheQueueSurvivesAnAgentRestart()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        var held = Message(bench);
        await bench.Queue!.NotifyAsync(Owner, held, 0);

        var restarted = new RoutineNotifyQueue(bench.Files, bench.Runs, bench.Channel, bench.Time, NullLogger<RoutineNotifyQueue>.Instance);
        await restarted.LoadAsync();

        Assert.Equal(held.NotifyId, Assert.Single(restarted.Pending(Owner)).Notify!.NotifyId);
    }

    [Fact]
    public async Task AnUntrustedQueueFileIsSetAsideNotLoaded()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.Queue!.NotifyAsync(Owner, Message(bench), 0);
        bench.Files.Untrusted[RoutineNotifyQueue.FileName] = "writable by Users";

        var restarted = new RoutineNotifyQueue(bench.Files, bench.Runs, bench.Channel, bench.Time, NullLogger<RoutineNotifyQueue>.Instance);
        await restarted.LoadAsync();

        Assert.Empty(restarted.Pending(Owner));
        Assert.NotNull(restarted.LoadWarning);
    }

    [Fact]
    public async Task AnAckFromAnotherPhoneRemovesNothing()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        var held = Message(bench);
        await bench.Queue!.NotifyAsync(Owner, held, 0);

        await bench.Messages.HandleNotifyAckAsync(OtherOwner, new RoutineNotifyAckPayload { NotifyIds = [held.NotifyId!] });

        Assert.Single(bench.Queue.Pending(Owner));
    }

    [Fact]
    public async Task AFlushSendsOnlyTheSyncingOwnersMessages()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.Queue!.NotifyAsync(Owner, Message(bench), 0);
        await bench.Queue.NotifyAsync(OtherOwner, Message(bench), 0);

        var sent = new List<RemexMessage>();
        await bench.Queue.FlushAsync(OtherOwner, m =>
        {
            sent.Add(m);
            return Task.CompletedTask;
        });

        Assert.Single(sent);
        Assert.False(Assert.Single(bench.Queue.Pending(Owner)).Sent);
    }

    [Fact]
    public async Task RevokingAPhoneDeletesItsHeldMessages()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.InitializeAsync();
        await bench.Queue!.NotifyAsync(Owner, Message(bench), 0);
        await bench.Queue.NotifyAsync(OtherOwner, Message(bench), 0);

        await bench.Service.ForgetOwnerAsync(Owner);

        Assert.Empty(bench.Queue.Pending(Owner));
        Assert.Single(bench.Queue.Pending(OtherOwner));
    }
}

/// <summary>§8.6 phone mirror (R-UX-34, the PC-run half): a PC run's countdown heads-up.</summary>
public sealed class CountdownHeadsUpTests
{
    [Fact]
    public async Task APcRunsCountdownIsMirroredToAConnectedOwner()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));

        var handle = await bench.StartAsync(1);
        await bench.Core.WaitForCountdownAsync();
        await WaitUntilAsync(
            () => bench.Channel.Sent.Any(s => s.Message.RoutineNotify?.Kind == RoutineNotifyKinds.Countdown),
            "no countdown heads-up was sent");

        var heads = bench.Channel.Sent.Select(s => s.Message.RoutineNotify).First(n => n?.Kind == RoutineNotifyKinds.Countdown)!;
        Assert.Equal(handle.Initial.RunId, heads.RunId);
        Assert.Equal(
            heads.QueuedAtUnixMs + (long)RoutineCountdownCoordinator.Length.TotalMilliseconds,
            heads.CountdownEndsAtUnixMs);
        Assert.Empty(bench.Queue!.Pending(Owner));

        bench.Core.ElapseCountdown();
        await handle.Completion;
    }

    [Fact]
    public async Task AnOfflineOwnerGetsNoHeadsUpAndNothingIsHeld()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));

        var handle = await bench.StartAsync(1);
        await bench.Core.WaitForCountdownAsync();
        bench.Core.ElapseCountdown();
        await handle.Completion;

        Assert.DoesNotContain(bench.Channel.Sent, s => s.Message.Type == MessageTypes.RoutineNotify);
        Assert.Empty(bench.Queue!.Pending(Owner));
    }

    [Fact]
    public async Task APresenceConfirmedRunNowHasNoCountdownAndNoHeadsUp()
    {
        var bench = new RoutineHostTestBench(withQueue: true);
        bench.Channel.Reachable.Add(Owner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), Power(RoutinePowerVerbs.Sleep)));

        await (await bench.StartAsync(1, RoutineRunSources.ManualPcRunNow, presence: true)).Completion;

        Assert.DoesNotContain(bench.Channel.Sent, s => s.Message.RoutineNotify?.Kind == RoutineNotifyKinds.Countdown);
    }
}
