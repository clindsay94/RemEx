namespace Remex.Core.Routines;

/// <summary>
/// Every routine reason code (routines spec §6.11, catalogue §10.1, RemEx-pp0rt.3).
/// </summary>
/// <remarks>
/// <para>
/// Snake_case wire strings, used for run outcomes, step results, sync rejections, run attributes and
/// UI states alike. Each one maps to a localized string on both platforms (PC <c>Routine_Reason_&lt;code&gt;</c>,
/// Android <c>routine_reason_&lt;code&gt;</c>), so a code that exists here and not in the string
/// tables shows up as a raw id in front of the user.
/// </para>
/// <para>
/// <b>Mirrored in Kotlin by <c>RoutineReasonCodes.kt</c>, and <see cref="All"/> is the parity list.</b>
/// Both sides assert their <c>All</c> equals <c>Fixtures/Routines/reason-codes.json</c>, in order, so
/// adding a code on one side only fails the other side's test. The order is the §10.1 table order.
/// </para>
/// </remarks>
public static class RoutineReasonCodes
{
    public const string Ok = "ok";

    // ── Reaching the PC ──
    public const string PcUnreachable = "pc_unreachable";
    public const string PcUnreachableAway = "pc_unreachable_away";
    public const string WaitTimeout = "wait_timeout";
    public const string WakeNoMac = "wake_no_mac";
    public const string WakeSendFailed = "wake_send_failed";
    public const string PermissionLocalNetwork = "permission_local_network";
    public const string PcNotSelected = "pc_not_selected";
    public const string PcNotPaired = "pc_not_paired";
    public const string PcTooOld = "pc_too_old";
    public const string StepTimeout = "step_timeout";
    public const string TransportLost = "transport_lost";
    public const string AfterPowerOff = "after_power_off";

    // ── On the PC ──
    public const string LaunchNotAllowed = "launch_not_allowed";
    public const string LaunchFailed = "launch_failed";
    public const string PowerUnsupported = "power_unsupported";
    public const string PowerDeniedByOs = "power_denied_by_os";
    public const string PowerFailed = "power_failed";
    public const string MediaUnavailable = "media_unavailable";
    public const string SensorUnavailable = "sensor_unavailable";
    public const string IdleSourceUnavailable = "idle_source_unavailable";
    public const string SessionSourceUnavailable = "session_source_unavailable";
    public const string RoutineNotFound = "routine_not_found";

    // ── Sync and validation ──
    public const string RejectedByPc = "rejected_by_pc";
    public const string SchemaTooNew = "schema_too_new";
    public const string PayloadTooLarge = "payload_too_large";
    public const string StaleRevision = "stale_revision";
    public const string RevisionConflict = "revision_conflict";
    public const string BlockedByPc = "blocked_by_pc";
    public const string DestructiveNotLast = "destructive_not_last";
    public const string TooManyDestructive = "too_many_destructive";
    public const string TooManySteps = "too_many_steps";
    public const string TooManyRoutines = "too_many_routines";
    public const string TooManyHomes = "too_many_homes";
    public const string BudgetExceeded = "budget_exceeded";
    public const string HomeNotSet = "home_not_set";
    public const string StepNotAllowedOnPc = "step_not_allowed_on_pc";
    public const string TriggerNotPc = "trigger_not_pc";
    public const string WrongPc = "wrong_pc";
    public const string UnsupportedTrigger = "unsupported_trigger";
    public const string UnsupportedStep = "unsupported_step";
    public const string DuplicateId = "duplicate_id";
    public const string FieldNotAllowed = "field_not_allowed";
    public const string InvalidField = "invalid_field";

    // ── Skipped and cancelled ──
    public const string PausedOnPhone = "paused_on_phone";
    public const string PausedOnPc = "paused_on_pc";
    public const string DisabledOnPc = "disabled_on_pc";
    public const string SkippedDisabled = "skipped_disabled";
    public const string OwnerAbsent = "owner_absent";
    public const string AlreadyRunning = "already_running";
    public const string Cooldown = "cooldown";
    public const string FlapSuppressed = "flap_suppressed";
    public const string RateLimited = "rate_limited";
    public const string ConflictCountdownActive = "conflict_countdown_active";
    public const string CancelledOnPc = "cancelled_on_pc";
    public const string CancelledOnPhone = "cancelled_on_phone";
    public const string InterruptedPc = "interrupted_pc";
    public const string InterruptedPhone = "interrupted_phone";

    // ── Messages, attributes and states ──
    public const string NotifyQueued = "notify_queued";
    public const string NotifyExpired = "notify_expired";
    public const string NotifyDeniedPhone = "notify_denied_phone";
    public const string BackgroundRestricted = "background_restricted";
    public const string DeferredByOs = "deferred_by_os";
    public const string Simulated = "simulated";
    public const string DryRun = "dry_run";
    public const string CountdownUnseen = "countdown_unseen";
    public const string NfcUnknownTag = "nfc_unknown_tag";
    public const string NfcDeviceLocked = "nfc_device_locked";
    public const string NfcDisabled = "nfc_disabled";
    public const string FingerprintCaptureFailed = "fingerprint_capture_failed";
    public const string HomeFingerprintStale = "home_fingerprint_stale";
    public const string StoreReset = "store_reset";
    public const string InternalError = "internal_error";

    /// <summary>Every code, in §10.1 table order. The Kotlin parity list.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Ok,
        PcUnreachable,
        PcUnreachableAway,
        WaitTimeout,
        WakeNoMac,
        WakeSendFailed,
        PermissionLocalNetwork,
        PcNotSelected,
        PcNotPaired,
        PcTooOld,
        StepTimeout,
        TransportLost,
        AfterPowerOff,
        LaunchNotAllowed,
        LaunchFailed,
        PowerUnsupported,
        PowerDeniedByOs,
        PowerFailed,
        MediaUnavailable,
        SensorUnavailable,
        IdleSourceUnavailable,
        SessionSourceUnavailable,
        RoutineNotFound,
        RejectedByPc,
        SchemaTooNew,
        PayloadTooLarge,
        StaleRevision,
        RevisionConflict,
        BlockedByPc,
        DestructiveNotLast,
        TooManyDestructive,
        TooManySteps,
        TooManyRoutines,
        TooManyHomes,
        BudgetExceeded,
        HomeNotSet,
        StepNotAllowedOnPc,
        TriggerNotPc,
        WrongPc,
        UnsupportedTrigger,
        UnsupportedStep,
        DuplicateId,
        FieldNotAllowed,
        InvalidField,
        PausedOnPhone,
        PausedOnPc,
        DisabledOnPc,
        SkippedDisabled,
        OwnerAbsent,
        AlreadyRunning,
        Cooldown,
        FlapSuppressed,
        RateLimited,
        ConflictCountdownActive,
        CancelledOnPc,
        CancelledOnPhone,
        InterruptedPc,
        InterruptedPhone,
        NotifyQueued,
        NotifyExpired,
        NotifyDeniedPhone,
        BackgroundRestricted,
        DeferredByOs,
        Simulated,
        DryRun,
        CountdownUnseen,
        NfcUnknownTag,
        NfcDeviceLocked,
        NfcDisabled,
        FingerprintCaptureFailed,
        HomeFingerprintStale,
        StoreReset,
        InternalError,
    ];
}
