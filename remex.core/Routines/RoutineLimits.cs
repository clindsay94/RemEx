namespace Remex.Core.Routines;

/// <summary>
/// Every numeric limit of the routine schema in one table (routines spec §6.3-§6.5, decision D10).
/// </summary>
/// <remarks>
/// Mirrored field for field in Kotlin (<c>RoutineLimits.kt</c>). The shared fixtures under
/// <c>remex.core.tests/Fixtures/Routines</c> exercise the boundaries on both sides, so a limit changed
/// on one side only fails the other side's fixture test.
/// </remarks>
public static class RoutineLimits
{
    /// <summary>Routines per phone, across every PC.</summary>
    public const int MaxRoutinesPerPhone = 32;

    /// <summary>PC-triggered (<c>pc.*</c>) routines per phone per PC.</summary>
    public const int MaxPcRoutinesPerHost = 16;

    /// <summary>Homes per phone in v1 (D9).</summary>
    public const int MaxHomes = 1;

    /// <summary>Steps per routine: at least one, at most this many.</summary>
    public const int MaxSteps = 12;

    /// <summary>Routine name, user-perceived characters after trim.</summary>
    public const int MaxNameLength = 40;

    /// <summary><c>appearance.icon</c> token length.</summary>
    public const int MaxIconLength = 32;

    /// <summary><c>home.leave</c> debounce.</summary>
    public const int MinLeaveDebounceSeconds = 60;
    public const int MaxLeaveDebounceSeconds = 1800;
    public const int DefaultLeaveDebounceSeconds = 180;

    /// <summary><c>pc.sensor</c> sensor id (= <c>SensorReading.Id</c>) length.</summary>
    public const int MaxSensorIdLength = 128;

    /// <summary><c>pc.sensor</c> display snapshot, and <c>launchApp.appLabel</c>.</summary>
    public const int MaxLabelLength = 64;

    /// <summary><c>pc.sensor</c> sustain.</summary>
    public const int MinSustainSeconds = 5;
    public const int MaxSustainSeconds = 600;
    public const int DefaultSustainSeconds = 60;

    /// <summary><c>pc.idle</c> idle time.</summary>
    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 240;

    /// <summary><c>wake</c> UDP port and its default.</summary>
    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const int DefaultWakePort = 9;
    public const string DefaultBroadcastIp = "255.255.255.255";

    /// <summary><c>waitOnline</c> timeout.</summary>
    public const int MinWaitOnlineSeconds = 30;
    public const int MaxWaitOnlineSeconds = 300;
    public const int DefaultWaitOnlineSeconds = 300;

    /// <summary><c>delay</c> step.</summary>
    public const int MinDelaySeconds = 1;
    public const int MaxDelaySeconds = 600;

    /// <summary><c>power.delaySeconds</c>, only for the delayable verbs.</summary>
    public const int MinPowerDelaySeconds = 0;
    public const int MaxPowerDelaySeconds = 600;

    /// <summary><c>notify</c> text, user-perceived characters.</summary>
    public const int MaxNotifyTitleLength = 40;
    public const int MaxNotifyBodyLength = 120;

    /// <summary>Per-routine step-type caps.</summary>
    public const int MaxWakeSteps = 1;
    public const int MaxWaitOnlineSteps = 2;
    public const int MaxNotifySteps = 3;
    public const int MaxDestructiveSteps = 1;

    /// <summary>
    /// Phone-run static budget: Σ delay + Σ waitOnline + <see cref="PhoneBudgetPerHostStepSeconds"/>
    /// per host-executed step + <see cref="CountdownSeconds"/> when a destructive step exists. Fits a
    /// 10-minute Android job window with margin.
    /// </summary>
    public const int MaxPhoneRunBudgetSeconds = 540;
    public const int PhoneBudgetPerHostStepSeconds = 60;

    /// <summary>
    /// Host-run static budget: Σ delay + <see cref="HostBudgetPerStepSeconds"/> per step +
    /// <see cref="CountdownSeconds"/> when a destructive step exists.
    /// </summary>
    public const int MaxHostRunBudgetSeconds = 1800;
    public const int HostBudgetPerStepSeconds = 30;

    /// <summary>The destructive-step countdown (D1), fixed in v1.</summary>
    public const int CountdownSeconds = 15;

    /// <summary>Serialized <c>routines_sync</c> bound (§6.5).</summary>
    public const int MaxSyncPayloadBytes = 64 * 1024;

    /// <summary><c>routine_notify</c> text echoed to the phone.</summary>
    public const int MaxNotifyWireBodyLength = 160;

    /// <summary><c>routine_step_result.detail</c> diagnostic length.</summary>
    public const int MaxDetailLength = 120;
}
