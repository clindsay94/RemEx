using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.Routines;
using Remex.Core.Models;
using Remex.Core.Routines;
using Remex.Core.Services;
using Remex.Core.Services.Command;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Agent.Tests.Routines;

/// <summary>
/// A hand-driven <see cref="TimeProvider"/>: nothing moves until <see cref="Advance"/>. Hand-rolled
/// rather than pulling in Microsoft.Extensions.TimeProvider.Testing, because timers are all it needs.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward and fires every timer that came due, in order.</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is { } due && due <= target && (next is null || due < next.DueAt))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, next.DueAt!.Value);
                next.DueAt = next.Period is { } period && period > 0 ? _ticks + period : null;
            }

            next.Fire();
        }
    }

    /// <summary>True while any timer is armed, i.e. something is waiting on this clock.</summary>
    public bool HasPendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Exists(t => t.DueAt is not null);
            }
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueAt { get; set; }

        public long? Period { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }

                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Records every <see cref="IRoutineUi"/> call; can be told to fail or to cancel.</summary>
internal sealed class FakeRoutineUi : IRoutineUi
{
    public List<RoutineCountdownPrompt> Shown { get; } = [];
    public List<string> Closed { get; } = [];
    public List<(NotificationImportance Importance, string Title, string Message)> Notifications { get; } = [];
    public bool WindowOpens { get; set; } = true;
    public bool Throw { get; set; }
    public Action? LastCancel { get; private set; }

    public Task<bool> ShowCountdownAsync(RoutineCountdownPrompt prompt, Action onCancel)
    {
        if (Throw)
        {
            throw new InvalidOperationException("no desktop");
        }

        Shown.Add(prompt);
        LastCancel = onCancel;
        return Task.FromResult(WindowOpens);
    }

    public void CloseCountdown(string runId) => Closed.Add(runId);

    public void Notify(NotificationImportance importance, string title, string message) =>
        Notifications.Add((importance, title, message));
}

internal sealed class FakeLockProbe : ISessionLockProbe
{
    public bool Locked { get; set; }

    public bool IsLocked() => Locked;
}

internal sealed class FakePowerExecutor : IRoutinePowerExecutor
{
    public List<(string Verb, int? Delay)> Issued { get; } = [];
    public Func<string, SharedCommandVerbs.Outcome?>? Respond { get; set; }
    public Exception? Throw { get; set; }

    public Task<SharedCommandVerbs.Outcome?> ExecuteAsync(string verb, int? delaySeconds)
    {
        Issued.Add((verb, delaySeconds));
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult<SharedCommandVerbs.Outcome?>(
            Respond?.Invoke(verb) ?? new SharedCommandVerbs.Outcome(true, verb + " executed."));
    }
}

internal sealed class FakeMediaKeys : IRoutineMediaKeys
{
    public List<int> Sent { get; } = [];
    public bool Available { get; set; } = true;

    public bool TrySend(int virtualKey)
    {
        if (!Available)
        {
            return false;
        }

        Sent.Add(virtualKey);
        return true;
    }
}

internal sealed class FakeOwners : IRoutineOwnerDirectory
{
    public HashSet<string> Paired { get; } = new(StringComparer.Ordinal) { RoutineTestBench.Owner, RoutineTestBench.OtherOwner };

    public bool IsPaired(string clientId) => Paired.Contains(clientId);

    public string? DisplayName(string clientId) => clientId == RoutineTestBench.Owner ? "Pixel" : null;
}

internal sealed class FakeLauncherStorage : ILauncherStorageService
{
    public List<AppEntry> Entries { get; } = [];

    public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>(Entries));

    public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
}

internal sealed class FakeAppLauncher : IAppLauncherService
{
    public List<string> Launched { get; } = [];
    public Exception? Throw { get; set; }

    public Task LaunchAppAsync(string targetPath)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        Launched.Add(targetPath);
        return Task.CompletedTask;
    }
}

internal static class FakeCapabilities
{
    public static IHostCapabilitiesProvider For(List<string> verbs)
    {
        var mock = new Mock<IHostCapabilitiesProvider>();
        mock.Setup(m => m.GetCurrent())
            .Returns(() => new HostCapabilities { SupportsRoutines = true, RoutinePowerVerbs = verbs });
        return mock.Object;
    }
}

/// <summary>The executor wired to fakes, with the pieces exposed for assertions.</summary>
internal sealed class RoutineTestBench
{
    public const string Owner = "owner-phone-0001";
    public const string OtherOwner = "other-phone-0002";

    public ManualTimeProvider Time { get; } = new();
    public FakeRoutineUi Ui { get; } = new();
    public FakeLockProbe Lock { get; } = new();
    public FakePowerExecutor Power { get; } = new();
    public FakeMediaKeys Media { get; } = new();
    public FakeOwners Owners { get; } = new();
    public FakeLauncherStorage Launchers { get; } = new();
    public FakeAppLauncher Launcher { get; } = new();
    public List<string> AdvertisedVerbs { get; } = [.. RoutinePowerVerbs.All];
    public RoutineCountdownCoordinator Countdown { get; }
    public RoutineStepExecutor Executor { get; }

    public RoutineTestBench(RoutineDryRunMode? dryRun = null)
    {
        Countdown = new RoutineCountdownCoordinator(Ui, Lock, Time, NullLogger<RoutineCountdownCoordinator>.Instance);
        Executor = new RoutineStepExecutor(
            Power, Launchers, Launcher, Media, Ui, Owners, FakeCapabilities.For(AdvertisedVerbs), Countdown,
            dryRun ?? RoutineDryRunMode.Off, Time, NullLogger<RoutineStepExecutor>.Instance);
    }

    public RoutineStepRequestHandler CreateHandler() =>
        new(Executor, Countdown, Time, NullLogger<RoutineStepRequestHandler>.Instance);

    public static RoutineStep PowerStep(string verb) => new() { Type = RoutineStepTypes.Power, Verb = verb };

    public static RoutineStepExecution Execution(
        RoutineStep step, bool testRun = false, bool presenceConfirmed = false, string owner = Owner, string runId = "run-1") =>
        new(owner, runId, "routine-1", "Evening", 0, step, RoutineRunSources.ManualApp, testRun, presenceConfirmed);

    /// <summary>Waits until the countdown has actually started (the UI was asked to show it).</summary>
    public async Task WaitForCountdownAsync(int expectedShown = 1)
    {
        for (var i = 0; i < 200 && (Ui.Shown.Count < expectedShown || !Time.HasPendingTimers); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(Ui.Shown.Count >= expectedShown, "the countdown never started");
        Assert.True(Time.HasPendingTimers, "the countdown is not waiting on the clock");
    }

    /// <summary>Runs the countdown to its end.</summary>
    public void ElapseCountdown() => Time.Advance(RoutineCountdownCoordinator.Length);
}
