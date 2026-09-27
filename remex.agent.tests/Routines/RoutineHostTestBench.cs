using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.Routines;
using Remex.Core.Messages;
using Remex.Core.Messages.Routines;
using Remex.Core.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>An in-memory <see cref="IRoutineStateFiles"/> that can be told to fail a write.</summary>
internal sealed class FakeStateFiles : IRoutineStateFiles
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public List<string> Quarantined { get; } = [];
    public bool FailWrites { get; set; }
    public int Writes { get; private set; }

    public string? Read(string fileName) => Files.TryGetValue(fileName, out var text) ? text : null;

    public Task WriteAsync(string fileName, string contents)
    {
        if (FailWrites)
        {
            throw new IOException("disk full");
        }

        Writes++;
        Files[fileName] = contents;
        return Task.CompletedTask;
    }

    public string? Quarantine(string fileName, DateTimeOffset now)
    {
        if (!Files.Remove(fileName, out var text))
        {
            return null;
        }

        var aside = fileName + ".unreadable-test";
        Files[aside] = text;
        Quarantined.Add(aside);
        return aside;
    }

    public void SweepStagingOrphans(string fileName)
    {
    }
}

/// <summary>Records every host-initiated send; <see cref="Reachable"/> decides who is connected.</summary>
internal sealed class FakePhoneChannel : IRoutinePhoneChannel
{
    public HashSet<string> Reachable { get; } = new(StringComparer.Ordinal);
    public List<(string ClientId, RemexMessage Message)> Sent { get; } = [];

    public bool CanReach(string clientId) => Reachable.Contains(clientId);

    public Task<bool> TrySendAsync(string clientId, RemexMessage message)
    {
        if (!CanReach(clientId))
        {
            return Task.FromResult(false);
        }

        lock (Sent)
        {
            Sent.Add((clientId, message));
        }

        return Task.FromResult(true);
    }

    public List<RoutineRunReportPayload> Reports(string clientId)
    {
        lock (Sent)
        {
            return Sent.Where(s => s.ClientId == clientId && s.Message.RoutineRunReport is not null)
                .Select(s => s.Message.RoutineRunReport!).ToList();
        }
    }

    public List<RoutineSyncResultPayload> SyncResults(string clientId)
    {
        lock (Sent)
        {
            return Sent.Where(s => s.ClientId == clientId && s.Message.RoutineSyncResult is not null)
                .Select(s => s.Message.RoutineSyncResult!).ToList();
        }
    }
}

internal sealed class FakeIdleSource : IIdleSource
{
    public TimeSpan? Idle { get; set; } = TimeSpan.Zero;

    public string Id => "test.idle";

    public Task<TimeSpan?> GetIdleAsync(CancellationToken ct) => Task.FromResult(Idle);
}

internal sealed class FakeSessionSource : ISessionStateSource
{
    public bool? IsLocked { get; set; } = false;

    public string Id => "test.session";

    public event Action<bool>? Changed;

    public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);

    public void Raise(bool locked)
    {
        IsLocked = locked;
        Changed?.Invoke(locked);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakePlatformSources(IIdleSource? idle, ISessionStateSource? session) : IRoutinePlatformSources
{
    public Task<IIdleSource?> ResolveIdleAsync(CancellationToken ct) => Task.FromResult(idle);

    public Task<ISessionStateSource?> ResolveSessionAsync(CancellationToken ct) => Task.FromResult(session);
}

/// <summary>The S4a host wired to fakes: stores, sync, runner, host service.</summary>
internal sealed class RoutineHostTestBench
{
    public const string HostId = "9f2c4be07a1d33e5";
    public const string Owner = RoutineTestBench.Owner;
    public const string OtherOwner = RoutineTestBench.OtherOwner;

    public RoutineTestBench Core { get; }
    public ManualTimeProvider Time => Core.Time;
    public FakeStateFiles Files { get; } = new();
    public FakePhoneChannel Channel { get; } = new();
    public RoutineTriggerAvailability Availability { get; } = new();
    public RoutineCausality Causality { get; }
    public RoutineHostStore Store { get; }
    public RoutineRunStore Runs { get; }
    public RoutineHostValidator Validator { get; }
    public RoutineHostRunner Runner { get; }
    public RoutineSyncHandler Sync { get; }
    public RoutineHostMessageHandler Messages { get; }
    public FakeIdleSource IdleSource { get; } = new();
    public FakeSessionSource SessionSource { get; } = new();
    public RoutineHostService Service { get; }
    public bool MediaPlaying { get; set; }

    public RoutineHostTestBench()
    {
        Core = new RoutineTestBench();
        Causality = new RoutineCausality(Time);
        Availability.SetIdleSource(IdleSource.Id);
        Availability.SetSessionSource(SessionSource.Id);
        Store = new RoutineHostStore(Files, Time, NullLogger<RoutineHostStore>.Instance);
        Runs = new RoutineRunStore(Files, Time, NullLogger<RoutineRunStore>.Instance);
        Validator = new RoutineHostValidator(Core.Launchers, FakeCapabilities.For(Core.AdvertisedVerbs), Availability, () => HostId);
        Runner = new RoutineHostRunner(
            Store, Runs, Core.Executor, Core.Countdown, Core.Owners, Channel, new LiveOnlyRoutinePhoneNotifier(Channel),
            Core.Ui, Causality, () => HostId, Time, NullLogger<RoutineHostRunner>.Instance);
        Sync = new RoutineSyncHandler(
            Store, Runs, Validator, Runner, Core.Countdown, Availability, Channel, Time, NullLogger<RoutineSyncHandler>.Instance);
        Messages = new RoutineHostMessageHandler(Sync, Runner, Runs, Time, NullLogger<RoutineHostMessageHandler>.Instance);

        var media = new Mock<Remex.Agent.Services.Media.IMediaSessionMonitor>();
        media.Setup(m => m.Current).Returns(() => MediaPlaying
            ? new Remex.Core.Models.MediaPlaybackState { Status = Remex.Core.Models.MediaPlaybackStatus.Playing }
            : null);
        Service = new RoutineHostService(
            Store, Runs, Runner, Sync, Availability, new FakePlatformSources(IdleSource, SessionSource), Core.Countdown,
            Core.Owners, Remex.Desktop.Services.Routines.RoutineDryRunMode.Off, Core.Ui, Causality, media.Object, Time,
            NullLoggerFactory.Instance);
    }

    public Task InitializeAsync() => Service.InitializeAsync(CancellationToken.None);

    /// <summary>Syncs <paramref name="routines"/> for <paramref name="owner"/> at <paramref name="revision"/>.</summary>
    public Task<RoutineSyncResultPayload> SyncAsync(long revision, bool paused = false, string owner = Owner, params Routine[] routines) =>
        Sync.ProcessAsync(owner, Payload(revision, paused, routines));

    public static RoutinesSyncPayload Payload(long revision, bool paused, params Routine[] routines) => new()
    {
        SchemaVersion = RoutineSchema.CurrentVersion,
        Revision = revision,
        Paused = paused,
        Routines = [.. routines],
    };

    public static string Id(int n) => $"3f0c2a4e-7b1d-4c55-9a60-{n:x12}";

    public static Routine Routine(int n, RoutineTrigger trigger, params RoutineStep[] steps) => new()
    {
        Id = Id(n),
        Name = $"Routine {n}",
        HostIdentity = HostId,
        Enabled = true,
        Revision = 1,
        Trigger = trigger,
        Steps = [.. steps],
        CreatedAtUnixMs = 1_790_000_000_000,
        UpdatedAtUnixMs = 1_790_000_000_000,
    };

    public static RoutineTrigger IdleTrigger(int minutes = 10, bool ignoreMedia = true) =>
        new() { Type = RoutineTriggerTypes.PcIdle, IdleMinutes = minutes, IgnoreWhileMediaPlaying = ignoreMedia };

    public static RoutineTrigger SessionTrigger(bool locked) =>
        new() { Type = RoutineTriggerTypes.PcSession, SessionState = locked ? RoutineSessionStates.Locked : RoutineSessionStates.Unlocked };

    public static RoutineStep Delay(int seconds) => new() { Type = RoutineStepTypes.Delay, Seconds = seconds };

    public static RoutineStep Power(string verb) => RoutineTestBench.PowerStep(verb);

    public static RoutineStep NotifyPc(string title = "Hi") => new() { Type = RoutineStepTypes.Notify, Target = RoutineNotifyTargets.Pc, Title = title, Body = "Body" };

    public static RoutineStep NotifyPhone(string title = "Hi") => new() { Type = RoutineStepTypes.Notify, Target = RoutineNotifyTargets.Phone, Title = title, Body = "Body" };

    public static RoutineStep Media() => new() { Type = RoutineStepTypes.Media, MediaAction = RoutineMediaActions.PlayPause };

    /// <summary>Waits until something is waiting on the manual clock (a delay or a countdown).</summary>
    public async Task WaitForTimerAsync()
    {
        for (var i = 0; i < 400 && !Time.HasPendingTimers; i++)
        {
            await Task.Delay(5);
        }

        Assert.True(Time.HasPendingTimers, "nothing is waiting on the clock");
    }

    /// <summary>Moves the clock in steps until <paramref name="done"/> completes.</summary>
    public async Task AdvanceUntilAsync(Task done, TimeSpan step)
    {
        for (var i = 0; i < 400 && !done.IsCompleted; i++)
        {
            Time.Advance(step);
            await Task.Delay(5);
        }

        Assert.True(done.IsCompleted, "the runs never finished");
        await done;
    }

    /// <summary>Waits for a condition that a background run makes true.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), what);
    }

    public Task<RoutineRunHandle> StartAsync(int n, string source = RoutineRunSources.PcIdle, string owner = Owner, bool presence = false, bool testRun = false) =>
        Runner.StartAsync(new RoutineRunStart(owner, Id(n), source, testRun, presence));
}
