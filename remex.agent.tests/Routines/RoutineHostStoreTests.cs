using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Routines;
using Remex.Agent.Services.Security;
using Remex.Core.Routines;
using Remex.Desktop.Services;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>Routines spec §13.2 <c>MultiOwnerIsolationTests</c> (§7.4.4, T17).</summary>
public sealed class MultiOwnerIsolationTests
{
    [Fact]
    public async Task OwnersAreKeptApartInStateRunsAndReports()
    {
        var bench = new RoutineHostTestBench();
        bench.Channel.Reachable.Add(Owner);
        bench.Channel.Reachable.Add(OtherOwner);
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await bench.SyncAsync(1, owner: OtherOwner, routines: Routine(2, IdleTrigger(), NotifyPc()));

        // B's empty sync never touches A's set.
        await bench.SyncAsync(2, owner: OtherOwner);
        Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!);

        // B cannot run A's routine by naming it.
        Assert.Equal(RoutineReasonCodes.RoutineNotFound, (await bench.StartAsync(1, RoutineRunSources.ManualApp, OtherOwner)).Initial.ReasonCode);

        await (await bench.StartAsync(1, RoutineRunSources.ManualApp)).Completion;
        Assert.Contains(bench.Channel.Reports(Owner), r => r.Runs!.Any(run => run.RoutineId == Id(1)));
        // B only ever hears about its own refused request, never about A's run.
        Assert.DoesNotContain(bench.Channel.Reports(OtherOwner), r => r.Runs!.Any(run => run.RoutineId == Id(1) && run.Outcome != RoutineRunOutcomes.Skipped));
        Assert.DoesNotContain(bench.Runs.Page(OtherOwner, 0).Page, r => r.RoutineId == Id(1) && r.Outcome != RoutineRunOutcomes.Skipped);
        Assert.DoesNotContain(bench.Runs.Page(Owner, 0).Page, r => r.OwnerClientId != Owner);
    }
}

/// <summary>Routines spec §13.2 <c>OwnerAbsentSuspensionTests</c> (§7.4.4, T8, Q11).</summary>
public sealed class OwnerAbsentSuspensionTests
{
    [Fact]
    public async Task ThirtyDaysWithoutASyncSuspendsAndTheNextSyncLiftsIt()
    {
        var bench = new RoutineHostTestBench();
        var routine = Routine(1, IdleTrigger(), NotifyPc());
        await bench.SyncAsync(1, routines: routine);

        bench.Time.Advance(RoutineHostStore.OwnerAbsentAfter);

        Assert.Equal(RoutineReasonCodes.OwnerAbsent, (await bench.StartAsync(1)).Initial.ReasonCode);
        Assert.True(Assert.Single(bench.Service.GetSnapshot().Owners).Suspended);

        var result = await bench.SyncAsync(1, routines: routine);

        Assert.Null(result.OwnerSuspended);
        Assert.True((await bench.StartAsync(1)).Started);
    }
}

/// <summary>Routines spec §13.2 <c>BlockedByPcTests</c> (T5).</summary>
public sealed class BlockedByPcTests
{
    [Fact]
    public async Task ABlockedPhoneCannotSyncOrRunButKeepsItsRoutinesForUnblocking()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));
        await bench.Service.SetBlockedAsync(Owner, blocked: true);

        var result = await bench.SyncAsync(2, routines: Routine(2, IdleTrigger(), NotifyPc()));

        Assert.Equal(RoutineSyncStatuses.BlockedByPc, result.Status);
        Assert.Equal(Id(1), Assert.Single(bench.Store.Current.Owner(Owner)!.Routines!).Id);
        Assert.Equal(RoutineReasonCodes.BlockedByPc, (await bench.StartAsync(1)).Initial.ReasonCode);

        await bench.Service.SetBlockedAsync(Owner, blocked: false);
        Assert.True((await bench.StartAsync(1)).Started);
    }
}

/// <summary>Routines spec §13.2 <c>RevokeDeletesRoutinesTests</c> (T7).</summary>
public sealed class RevokeDeletesRoutinesTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("remex-routine-revoke-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ForgettingAnOwnerDeletesItsRoutinesRunsAndCancelsItsActiveRun()
    {
        var bench = new RoutineHostTestBench();
        await bench.SyncAsync(1, routines: [Routine(1, IdleTrigger(), Delay(60), NotifyPc())]);
        await bench.SyncAsync(1, owner: OtherOwner, routines: Routine(2, IdleTrigger(), NotifyPc()));
        await (await bench.StartAsync(2, RoutineRunSources.ManualApp, OtherOwner)).Completion;
        var running = await bench.StartAsync(1, RoutineRunSources.ManualApp);
        await bench.WaitForTimerAsync();

        await bench.Service.ForgetOwnerAsync(Owner);
        var final = await running.Completion;

        Assert.Null(bench.Store.Current.Owner(Owner));
        Assert.NotNull(bench.Store.Current.Owner(OtherOwner));
        Assert.Equal(RoutineRunOutcomes.Cancelled, final.Outcome);
        Assert.Equal(RoutineReasonCodes.PcNotPaired, final.ReasonCode);
        Assert.Empty(bench.Runs.Query(Owner, null));
        Assert.NotEmpty(bench.Runs.Query(OtherOwner, null));
    }

    [Fact]
    public async Task TheRevokerCallsTheRoutinesTeardownAndReportsItsFailure()
    {
        var routines = new RecordingLifecycle();
        var revoker = NewRevoker(routines);

        await revoker.RevokeAsync("phone-1", CancellationToken.None);
        Assert.Equal(["phone-1"], routines.Forgotten);

        routines.Throw = true;
        var failure = await Assert.ThrowsAsync<PairedDeviceRevocationException>(() => revoker.RevokeAsync("phone-2", CancellationToken.None));
        Assert.Contains(failure.Failures, e => e is IOException);
        Assert.False(failure.PairingMayReturn);
    }

    private PairedDeviceRevoker NewRevoker(IRoutineOwnerLifecycle routines)
    {
        var registry = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, Path.Combine(_root.FullName, "paired.json"));
        registry.RegisterClient("phone-1", [1, 2, 3, 4]);
        registry.RegisterClient("phone-2", [5, 6, 7, 8]);
        var trust = new FileTrustService(
            NullLogger<FileTrustService>.Instance, registry, new ClientSessionRegistry(),
            Path.Combine(_root.FullName, "trust.json"), TimeSpan.FromSeconds(1));
        return new PairedDeviceRevoker(
            registry,
            new PairedClientNameStore(NullLogger<PairedClientNameStore>.Instance, Path.Combine(_root.FullName, "names.json")),
            new PairedDeviceNameOverrideStore(NullLogger<PairedDeviceNameOverrideStore>.Instance, Path.Combine(_root.FullName, "overrides.json")),
            new PairedDeviceActivityStore(NullLogger<PairedDeviceActivityStore>.Instance, Path.Combine(_root.FullName, "activity.json")),
            trust,
            new NoDisconnect(),
            NullLogger<PairedDeviceRevoker>.Instance,
            routines);
    }

    private sealed class RecordingLifecycle : IRoutineOwnerLifecycle
    {
        public List<string> Forgotten { get; } = [];
        public bool Throw { get; set; }

        public Task ForgetOwnerAsync(string clientId)
        {
            if (Throw)
            {
                throw new IOException("routines.json is locked");
            }

            Forgotten.Add(clientId);
            return Task.CompletedTask;
        }
    }

    private sealed class NoDisconnect : IPairedDeviceDisconnector
    {
        public Task DisconnectAsync(string clientId) => Task.CompletedTask;
    }
}

/// <summary>Routines spec §13.2 <c>RoutineStoreCorruptFileTests</c> (T14, §6.7).</summary>
public sealed class RoutineStoreCorruptFileTests
{
    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"fileVersion\":2,\"owners\":{}}")]
    [InlineData("{\"fileVersion\":1,\"owners\":{\"phone\":{\"revision\":1,\"routines\":[{\"id\":\"nope\"}]}}}")]
    public async Task AnUnreadableOrInvalidFileIsSetAsideAndTheHostStartsEmptyWithAWarning(string contents)
    {
        var bench = new RoutineHostTestBench();
        bench.Files.Files[RoutineHostStore.FileName] = contents;

        await bench.InitializeAsync();

        Assert.Empty(bench.Store.Current.Owners!);
        Assert.Equal(contents, bench.Files.Files[Assert.Single(bench.Files.Quarantined)]);
        Assert.False(bench.Files.Files.ContainsKey(RoutineHostStore.FileName));
        Assert.Single(bench.Service.GetSnapshot().Warnings);
        Assert.Contains(bench.Core.Ui.Notifications, n => n.Importance == NotificationImportance.Problem);
    }

    [Fact]
    public async Task AValidFileRoundTrips()
    {
        var first = new RoutineHostTestBench();
        await first.SyncAsync(1, routines: Routine(1, IdleTrigger(), NotifyPc()));

        var second = new RoutineHostTestBench();
        second.Files.Files[RoutineHostStore.FileName] = first.Files.Files[RoutineHostStore.FileName];
        await second.InitializeAsync();

        Assert.Empty(second.Files.Quarantined);
        Assert.Equal(Id(1), Assert.Single(second.Store.Current.Owner(Owner)!.Routines!).Id);
        Assert.Empty(second.Service.GetSnapshot().Warnings);
    }
}

/// <summary>Routines spec §13.2 <c>RoutineStoreAclTests</c> (T14, R-SEC-12).</summary>
public sealed class RoutineStoreAclTests
{
    [WindowsOnlyFact("NTFS ACLs")]
    public void TheDescriptorIsProtectedAndGrantsOnlyLocalSystemAndAdministrators()
    {
        var security = RoutineFilePermissions.BuildWindowsSecurity(ownerIsAdministrators: true);

        Assert.True(security.AreAccessRulesProtected);
        var granted = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        Assert.Equal(
            new HashSet<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
            },
            granted);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), security.GetOwner(typeof(SecurityIdentifier)));
    }
}

/// <summary>Routines spec §13.2 <c>RoutineStoreLinuxPermissionTests</c> (§6.9).</summary>
public sealed class RoutineStoreLinuxPermissionTests
{
    [LinuxOnlyFact("POSIX file modes")]
    public async Task TheFileIsOwnerReadWriteOnly()
    {
        var directory = Directory.CreateTempSubdirectory("remex-routine-perm-");
        try
        {
            var files = new RoutineStateFiles(directory.FullName, NullLogger.Instance);
            await files.WriteAsync(RoutineHostStore.FileName, "{}");

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory.FullName, RoutineHostStore.FileName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

/// <summary>Routines spec §13.2 <c>RoutineRunStoreTests</c> (§8.8).</summary>
public sealed class RoutineRunStoreTests
{
    private static RoutineRun Run(string id, long triggeredMs, string routine = "r1", string owner = Owner, string outcome = RoutineRunOutcomes.Succeeded) => new()
    {
        RunId = id,
        OwnerClientId = owner,
        RoutineId = routine,
        TriggeredAtUnixMs = triggeredMs,
        Outcome = outcome,
    };

    [Fact]
    public async Task EveryUpsertStampsANewSeq()
    {
        var bench = new RoutineHostTestBench();
        var now = bench.Time.GetUtcNow().ToUnixTimeMilliseconds();

        var a = await bench.Runs.UpsertAsync(Run("a", now));
        var b = await bench.Runs.UpsertAsync(Run("b", now));
        var a2 = await bench.Runs.UpsertAsync(Run("a", now) with { Outcome = RoutineRunOutcomes.Failed });

        Assert.True(a.Seq < b.Seq && b.Seq < a2.Seq);
        Assert.Equal(2, bench.Runs.Query(null, null).Count);
    }

    [Fact]
    public void RetentionKeepsTwentyPerRoutineOrThirtyDaysNeverNinetyAndCapsAtFiveHundred()
    {
        var now = 400L * 24 * 3_600_000;
        long DaysAgo(double days) => now - (long)(days * 24 * 3_600_000);

        var runs = Enumerable.Range(0, 25).Select(i => Run($"old{i}", DaysAgo(40 + i))).ToList();
        runs.Add(Run("young", DaysAgo(1), routine: "r2"));
        runs.Add(Run("ancient", DaysAgo(91), routine: "r3"));
        runs.Add(Run("stuck", DaysAgo(200), routine: "r4", outcome: RoutineRunOutcomes.Running));

        var kept = RoutineRunStore.ApplyRetention(runs, now).Select(r => r.RunId).ToHashSet();

        // r1: the 20 newest of 25 are kept (all older than 30 days); only those under 90 days survive.
        Assert.Contains("old0", kept);
        Assert.DoesNotContain("old24", kept);
        Assert.Contains("young", kept);
        Assert.DoesNotContain("ancient", kept);
        Assert.Contains("stuck", kept);

        var many = Enumerable.Range(0, 600).Select(i => Run($"m{i}", DaysAgo(1) + i, routine: $"r{i}")).ToList();
        var capped = RoutineRunStore.ApplyRetention(many, now);
        Assert.Equal(RoutineRunStore.Cap, capped.Count);
        Assert.DoesNotContain(capped, r => r.RunId == "m0");
    }

    [Fact]
    public async Task TheStartupSweepMarksLeftoverRunsInterrupted()
    {
        var bench = new RoutineHostTestBench();
        var now = bench.Time.GetUtcNow().ToUnixTimeMilliseconds();
        await bench.Runs.UpsertAsync(Run("cut", now, outcome: RoutineRunOutcomes.Running) with
        {
            Steps = [new RoutineRunStep { Index = 0, Status = RoutineStepStatuses.Running }, new RoutineRunStep { Index = 1, Status = RoutineStepStatuses.Pending }],
        });

        var restarted = new RoutineRunStore(bench.Files, bench.Time, NullLogger<RoutineRunStore>.Instance);
        await restarted.LoadAndSweepAsync();
        var swept = restarted.Find("cut")!;

        Assert.Equal(RoutineRunOutcomes.Interrupted, swept.Outcome);
        Assert.Equal(RoutineReasonCodes.InterruptedPc, swept.ReasonCode);
        Assert.All(swept.Steps!, s => Assert.Equal(RoutineStepStatuses.Skipped, s.Status));
    }

    [Fact]
    public async Task ReportsPageBySeqFiftyAtATime()
    {
        var bench = new RoutineHostTestBench();
        var now = bench.Time.GetUtcNow().ToUnixTimeMilliseconds();
        for (var i = 0; i < 60; i++)
        {
            await bench.Runs.UpsertAsync(Run($"p{i}", now, routine: $"r{i}"));
        }

        await bench.Runs.UpsertAsync(Run("other", now, owner: OtherOwner));

        var (first, more) = bench.Runs.Page(Owner, 0);
        Assert.Equal(RoutineRunStore.MaxPerReport, first.Count);
        Assert.True(more);
        var (second, more2) = bench.Runs.Page(Owner, first[^1].Seq!.Value);
        Assert.Equal(10, second.Count);
        Assert.False(more2);
        Assert.DoesNotContain(first.Concat(second), r => r.OwnerClientId == OtherOwner);
    }
}
