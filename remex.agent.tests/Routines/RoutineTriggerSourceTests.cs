using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Routines;
using Remex.Core.Routines;
using static Remex.Agent.Tests.Routines.RoutineHostTestBench;

namespace Remex.Agent.Tests.Routines;

/// <summary>Routines spec §13.2 <c>IdleTriggerSourceTests</c> (§8.5.2).</summary>
public sealed class IdleTriggerSourceTests
{
    private sealed class Rig
    {
        public ManualTimeProvider Time { get; } = new();
        public FakeIdleSource Source { get; } = new();
        public bool Media { get; set; }
        public List<RoutineTriggerFire> Fires { get; } = [];
        public IdleTriggerSource Idle { get; }

        public Rig()
        {
            Idle = new IdleTriggerSource(Time, () => Media, NullLogger.Instance);
            Idle.Fired += Fires.Add;
            Idle.SetSource(Source);
        }

        public async Task PollAtAsync(TimeSpan idle)
        {
            Source.Idle = idle;
            await Idle.PollOnceAsync();
        }
    }

    [Fact]
    public async Task FiresOncePerIdlePeriodAndRearmsOnInput()
    {
        var rig = new Rig();
        await rig.PollAtAsync(TimeSpan.Zero);
        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), IdleMinutes: 10, IgnoreWhileMediaPlaying: true)]);

        await rig.PollAtAsync(TimeSpan.FromMinutes(9));
        Assert.Empty(rig.Fires);
        await rig.PollAtAsync(TimeSpan.FromMinutes(10));
        await rig.PollAtAsync(TimeSpan.FromMinutes(30));
        var fire = Assert.Single(rig.Fires);
        Assert.Equal(RoutineRunSources.PcIdle, fire.Source);
        Assert.Equal(10, fire.Detail.IdleMinutes);

        await rig.PollAtAsync(TimeSpan.FromSeconds(1));
        await rig.PollAtAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(2, rig.Fires.Count);
    }

    [Fact]
    public async Task MediaPlayingHoldsTheFireUntilItStops()
    {
        var rig = new Rig();
        await rig.PollAtAsync(TimeSpan.Zero);
        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), 10, IgnoreWhileMediaPlaying: true), new IdleArming(Owner, Id(2), 10, IgnoreWhileMediaPlaying: false)]);
        rig.Media = true;

        await rig.PollAtAsync(TimeSpan.FromMinutes(15));
        Assert.Equal(Id(2), Assert.Single(rig.Fires).RoutineId);

        rig.Media = false;
        await rig.PollAtAsync(TimeSpan.FromMinutes(16));
        Assert.Equal(2, rig.Fires.Count);
    }

    [Fact]
    public async Task ARoutineArmedWhileThePcIsAlreadyIdleWaitsForInput()
    {
        var rig = new Rig();
        await rig.PollAtAsync(TimeSpan.FromHours(1));
        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), 10, true)]);

        await rig.PollAtAsync(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.Empty(rig.Fires);

        await rig.PollAtAsync(TimeSpan.Zero);
        await rig.PollAtAsync(TimeSpan.FromMinutes(10));
        Assert.Single(rig.Fires);
    }

    [Fact]
    public async Task UnpausingNeverFiresOnTheEdge()
    {
        var rig = new Rig();
        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), 10, true)]);
        await rig.PollAtAsync(TimeSpan.Zero);

        // Paused (disarmed) while the user walks away; un-paused an hour later.
        rig.Idle.SetArmed([]);
        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), 10, true)]);
        await rig.PollAtAsync(TimeSpan.FromHours(1));

        Assert.Empty(rig.Fires);
    }

    [Fact]
    public void PollingRunsOnlyWhileSomethingIsArmedAndASourceExists()
    {
        var rig = new Rig();
        Assert.False(rig.Time.HasPendingTimers);

        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), 10, true)]);
        Assert.True(rig.Time.HasPendingTimers);

        rig.Idle.SetSource(null);
        Assert.False(rig.Time.HasPendingTimers);
    }

    [Fact]
    public async Task ASilentSourceFiresNothing()
    {
        var rig = new Rig();
        rig.Idle.SetArmed([new IdleArming(Owner, Id(1), 1, false)]);
        rig.Source.Idle = null;

        await rig.Idle.PollOnceAsync();

        Assert.Empty(rig.Fires);
    }
}

/// <summary>Routines spec §13.2 <c>SessionTriggerSourceTests</c> (§8.5.3, T9).</summary>
public sealed class SessionTriggerSourceTests
{
    private sealed class Rig
    {
        public ManualTimeProvider Time { get; } = new();
        public RoutineCausality Causality { get; }
        public List<RoutineTriggerFire> Fires { get; } = [];
        public SessionTriggerSource Session { get; }

        public Rig()
        {
            Causality = new RoutineCausality(Time);
            Session = new SessionTriggerSource(Time, Causality, NullLogger.Instance);
            Session.Fired += Fires.Add;
            Session.SetInitialState(false);
            Session.SetArmed([new SessionArming(Owner, Id(1), OnLocked: true), new SessionArming(Owner, Id(2), OnLocked: false)]);
        }
    }

    [Fact]
    public void AnEdgeFiresOnlyAfterHoldingThreeSeconds()
    {
        var rig = new Rig();

        rig.Session.OnEdge(true);
        rig.Time.Advance(SessionTriggerSource.Settle - TimeSpan.FromMilliseconds(1));
        Assert.Empty(rig.Fires);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));

        var fire = Assert.Single(rig.Fires);
        Assert.Equal(Id(1), fire.RoutineId);
        Assert.Equal(RoutineSessionStates.Locked, fire.Detail.SessionState);
    }

    [Fact]
    public void ALockUnlockFlapCollapsesToNothing()
    {
        var rig = new Rig();

        rig.Session.OnEdge(true);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Session.OnEdge(false);
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Fires);
    }

    [Fact]
    public void ALockUnlockLockFlapFiresOnceAfterTheLastEdgeSettles()
    {
        var rig = new Rig();

        rig.Session.OnEdge(true);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Session.OnEdge(false);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Session.OnEdge(true);
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(Id(1), Assert.Single(rig.Fires).RoutineId);
    }

    [Fact]
    public void AtMostTwentySessionRunsPerRoutinePerHour()
    {
        var rig = new Rig();
        for (var i = 0; i < 25; i++)
        {
            rig.Session.OnEdge(true);
            rig.Time.Advance(TimeSpan.FromSeconds(5));
            rig.Session.OnEdge(false);
            rig.Time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(SessionTriggerSource.MaxFiresPerRoutinePerHour, rig.Fires.Count(f => f.RoutineId == Id(1)));

        rig.Time.Advance(TimeSpan.FromHours(1));
        rig.Session.OnEdge(true);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionTriggerSource.MaxFiresPerRoutinePerHour + 1, rig.Fires.Count(f => f.RoutineId == Id(1)));
    }
}

/// <summary>Routines spec §13.2 <c>CausalSuppressionTests</c> (§8.1, T9, Q9).</summary>
public sealed class CausalSuppressionTests
{
    [Fact]
    public void AnEdgeCausedByARoutineStepTriggersNothing()
    {
        var time = new ManualTimeProvider();
        var causality = new RoutineCausality(time);
        var session = new SessionTriggerSource(time, causality, NullLogger.Instance);
        var fires = new List<RoutineTriggerFire>();
        session.Fired += fires.Add;
        session.SetInitialState(false);
        session.SetArmed([new SessionArming(Owner, Id(1), OnLocked: true)]);

        // A routine's LOCK: the lock edge arrives while the step is still running.
        using (causality.BeginStep())
        {
            session.OnEdge(true);
        }

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(fires);

        // A person locking the PC later is a real edge again.
        session.OnEdge(false);
        time.Advance(TimeSpan.FromSeconds(10));
        session.OnEdge(true);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(fires);
    }

    [Fact]
    public void TheWindowIsFiveSecondsAfterTheStep()
    {
        var time = new ManualTimeProvider();
        var causality = new RoutineCausality(time);

        causality.BeginStep().Dispose();
        time.Advance(RoutineCausality.Window);
        Assert.True(causality.IsCausedByRunNow());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(causality.IsCausedByRunNow());
    }

    [Fact]
    public async Task APhoneRunsStepRequestAlsoMarksCausality()
    {
        var bench = new RoutineTestBench();
        var causality = new RoutineCausality(bench.Time);
        var handler = new RoutineStepRequestHandler(
            bench.Executor, bench.Countdown, bench.Time, NullLogger<RoutineStepRequestHandler>.Instance, causality);

        await handler.HandleStepRequestAsync(
            Owner,
            new Remex.Core.Messages.Routines.RoutineStepRequestPayload { RunId = "run-1", Step = RoutineTestBench.PowerStep(RoutinePowerVerbs.Lock) },
            _ => Task.CompletedTask);

        Assert.True(causality.IsCausedByRunNow());
    }
}

/// <summary>Routines spec §13.2 <c>LogindSessionSourceTests</c>: <c>PropertiesChanged</c> bodies (§8.5.3).</summary>
public sealed class LogindSessionSourceTests
{
    private static readonly IReadOnlyList<string> None = [];

    [Fact]
    public void LockedHintInTheChangedDictionaryIsTheNewState()
    {
        var change = LogindParsing.ParseLockedHint(
            LogindParsing.SessionInterface,
            new Dictionary<string, object?> { ["LockedHint"] = true, ["IdleHint"] = false },
            None);

        Assert.True(change.IsRelevant);
        Assert.True(change.Value);
    }

    [Fact]
    public void AnInvalidatedLockedHintMustBeReread()
    {
        var change = LogindParsing.ParseLockedHint(LogindParsing.SessionInterface, new Dictionary<string, object?>(), ["LockedHint"]);

        Assert.True(change.IsRelevant);
        Assert.Null(change.Value);
        Assert.True(change.Invalidated);
    }

    [Theory]
    [InlineData("org.freedesktop.login1.User")]
    [InlineData("org.freedesktop.login1.Seat")]
    [InlineData(null)]
    public void OtherInterfacesAreIgnored(string? iface)
    {
        var change = LogindParsing.ParseLockedHint(iface, new Dictionary<string, object?> { ["LockedHint"] = true }, None);

        Assert.False(change.IsRelevant);
    }

    [Fact]
    public void OtherPropertiesAreNotAnEdge()
    {
        var change = LogindParsing.ParseLockedHint(
            LogindParsing.SessionInterface, new Dictionary<string, object?> { ["IdleHint"] = true, ["Active"] = true }, None);

        Assert.False(change.IsRelevant);
    }

    [Fact]
    public void TheIdleHintBecomesAnIdleDuration()
    {
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var since = (ulong)(now.AddMinutes(-7).ToUnixTimeMilliseconds() * 1000);

        Assert.Equal(TimeSpan.FromMinutes(7), LogindParsing.IdleFromHint(true, since, now));
        Assert.Equal(TimeSpan.Zero, LogindParsing.IdleFromHint(false, since, now));
    }
}

/// <summary>Routines spec §13.2 <c>RoutinePowerVerbProbeTests</c>: the logind mapping (merge gate).</summary>
public sealed class RoutinePowerVerbProbeTests
{
    private static Dictionary<string, string?> Answers(string answer) =>
        RoutinePowerVerbProbe.LogindMethods.ToDictionary(m => m, _ => (string?)answer);

    [Fact]
    public void EveryYesAdvertisesTheVerbsItCovers()
    {
        var verbs = RoutinePowerVerbProbe.MapLinux(Answers("yes"), monitorOffAvailable: true);

        Assert.Equal(RoutinePowerVerbs.All, verbs);
        Assert.DoesNotContain(RoutinePowerVerbs.ReservedWakeOnLan, verbs);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("na")]
    [InlineData("challenge")]
    [InlineData(null)]
    public void AnythingButYesIsNotAdvertised(string? answer)
    {
        var verbs = RoutinePowerVerbProbe.MapLinux(Answers(answer!), monitorOffAvailable: false);

        Assert.Equal([RoutinePowerVerbs.SignOut, RoutinePowerVerbs.Lock], verbs);
    }

    [Fact]
    public void HibernateFollowsCanHibernateAlone()
    {
        var answers = Answers("yes");
        answers["CanHibernate"] = "na";

        var verbs = RoutinePowerVerbProbe.MapLinux(answers, monitorOffAvailable: false);

        Assert.DoesNotContain(RoutinePowerVerbs.Hibernate, verbs);
        Assert.Contains(RoutinePowerVerbs.Sleep, verbs);
        Assert.DoesNotContain(RoutinePowerVerbs.MonitorOff, verbs);
    }

    [Fact]
    public void NoLogindMeansNothingItWouldHaveAnswered()
    {
        Assert.Empty(RoutinePowerVerbProbe.MapLinux(null, monitorOffAvailable: false));
        Assert.Equal([RoutinePowerVerbs.MonitorOff], RoutinePowerVerbProbe.MapLinux(null, monitorOffAvailable: true));
    }
}

/// <summary>Routines spec §13.2 <c>WtsSessionStateSourceTests</c>: the source owns its own window.</summary>
public sealed class WtsSessionStateSourceTests
{
    [WindowsOnlyFact("WTS session notifications")]
    public async Task TheSourceCreatesAndTearsDownItsOwnMessageOnlyWindow()
    {
        using var source = new WtsSessionStateSource(NullLogger<WtsSessionStateSource>.Instance);

        var started = await source.StartAsync(CancellationToken.None);

        Assert.True(started, "WTSRegisterSessionNotification refused the window");
        Assert.True(source.HasWindow);
        Assert.Equal(WtsSessionStateSource.SourceId, source.Id);

        source.Dispose();
        Assert.False(source.HasWindow);
    }
}

/// <summary>The host service arms triggers from the store and fires the runner (§8.4 state machine).</summary>
public sealed class RoutineHostServiceArmingTests
{
    [Fact]
    public async Task AnIdleFireStartsTheRoutineAndPauseDisarmsIt()
    {
        var bench = new RoutineHostTestBench();
        await bench.InitializeAsync();
        await bench.SyncAsync(1, routines: Routine(1, IdleTrigger(minutes: 1), NotifyPc("idle")));

        bench.IdleSource.Idle = TimeSpan.Zero;
        await bench.Service.Idle.PollOnceAsync();
        bench.IdleSource.Idle = TimeSpan.FromMinutes(2);
        await bench.Service.Idle.PollOnceAsync();
        await WaitUntilAsync(() => bench.Core.Ui.Notifications.Any(n => n.Title == "idle"), "the idle routine never ran");

        await bench.Service.SetHostPausedAsync(true);
        Assert.False(bench.Time.HasPendingTimers);
    }

    [Fact]
    public async Task ALockEdgeFromTheSourceRunsASessionRoutine()
    {
        var bench = new RoutineHostTestBench();
        await bench.InitializeAsync();
        await bench.SyncAsync(1, routines: Routine(1, SessionTrigger(locked: true), NotifyPc("locked")));

        bench.SessionSource.Raise(true);
        bench.Time.Advance(SessionTriggerSource.Settle);

        await WaitUntilAsync(() => bench.Core.Ui.Notifications.Any(n => n.Title == "locked"), "the session routine never ran");
        Assert.True(bench.Availability.SessionLocked);
        Assert.True(new SessionLockProbe(bench.Availability).IsLocked() || OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task ThePcSwitchDisarms()
    {
        var bench = new RoutineHostTestBench();
        await bench.InitializeAsync();
        await bench.SyncAsync(1, routines: Routine(1, SessionTrigger(locked: true), NotifyPc("locked")));
        await bench.Service.SetDisabledOnPcAsync(Owner, Id(1), disabled: true);

        bench.SessionSource.Raise(true);
        bench.Time.Advance(SessionTriggerSource.Settle);
        await Task.Delay(50);

        Assert.DoesNotContain(bench.Core.Ui.Notifications, n => n.Title == "locked");
    }
}
