using System.Text.Json.Serialization;

namespace Remex.Core.Routines;

// The routine models (routines spec §6.2-§6.6, RemEx-pp0rt.3). The wire and the phone store share one
// shape, and the Kotlin mirror (routines/model/Routine.kt) reads and writes the same JSON.
//
// NO MEMBER IS `required`, AND EVERY FIELD WHOSE PRESENCE MATTERS IS NULLABLE (spec T18). A required
// member missing from the JSON makes System.Text.Json throw; inside a RemexMessage that throw becomes a
// null envelope and PingPongHandler drops the WHOLE session (the PhoneThemeSnapshot lesson). Validity
// is the validator's job (RoutineValidator), never the deserializer's. Trigger and step fields are
// nullable for a second reason: "field present on a type that does not take it" is a validation rule
// (field_not_allowed), and a non-nullable default would make absent and present-with-default the same.
//
// Trigger and step are FLAT records discriminated by `type`, not polymorphic JSON: source-generated
// polymorphism throws on an unknown discriminator, and an unknown type must deserialize and then be
// rejected (unsupported_trigger / unsupported_step).

/// <summary>A document of routines: the phone store body and the shape inside the export file.</summary>
public sealed record RoutineSet
{
    /// <summary><see cref="RoutineSchema.CurrentVersion"/> when written by this build. 0 = absent.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>List order is the user's order.</summary>
    [JsonConverter(typeof(Remex.Core.Messages.Routines.LenientRoutineListConverter))]
    public List<Routine>? Routines { get; init; }
}

/// <summary>One trigger followed by 1-12 ordered steps (§6.2).</summary>
public sealed record Routine
{
    /// <summary>Lower-case UUID v4, unique within the phone's set, never reused.</summary>
    public string? Id { get; init; }

    /// <summary>1-40 user-perceived characters after trim; no control characters or line breaks.</summary>
    public string? Name { get; init; }

    /// <summary>16 lower-case hex characters: <c>HostIdentity.KeyFor(spkiPin)</c> of the PC it controls.</summary>
    public string? HostIdentity { get; init; }

    /// <summary>
    /// Phone-side toggle. Gates automatic triggers and shortcut, widget and NFC runs; in-app Run,
    /// Test and PC Run now still work.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>≥ 1, +1 on every save of this routine. Independent of the per-PC sync revision.</summary>
    public long Revision { get; init; }

    /// <summary>Display-only token set owned by the UX; the host treats it as opaque.</summary>
    public RoutineAppearance? Appearance { get; init; }

    public RoutineTrigger? Trigger { get; init; }

    public List<RoutineStep>? Steps { get; init; }

    /// <summary>Display only, never for ordering.</summary>
    public long CreatedAtUnixMs { get; init; }

    /// <summary>Display only; ≥ <see cref="CreatedAtUnixMs"/>.</summary>
    public long UpdatedAtUnixMs { get; init; }

    /// <summary>
    /// Set by <c>LenientRoutineListConverter</c> when this routine's JSON could not be read (a field
    /// of the wrong JSON type). Never on the wire. The placeholder carries the <see cref="Id"/> when
    /// one was readable so the rejection can name the routine; the validator answers
    /// <c>invalid_field</c>.
    /// </summary>
    [JsonIgnore]
    public bool IsMalformed { get; init; }

    /// <summary>
    /// The original JSON of a malformed routine, exactly as it arrived. <c>LenientRoutineListConverter</c>
    /// writes this back unchanged instead of the lossy placeholder, so a store round trip never
    /// overwrites the user's routine with a copy that has lost its fields (spec §6.7, "kept
    /// verbatim"). A malformed routine WITHOUT it cannot be written: the converter throws rather than
    /// persist the placeholder silently.
    /// </summary>
    [JsonIgnore]
    public System.Text.Json.JsonElement? RawJson { get; init; }
}

/// <summary>Display hints (§6.2). Both fields optional.</summary>
public sealed record RoutineAppearance
{
    /// <summary>≤ 32 characters of <c>[a-z0-9_]</c>.</summary>
    public string? Icon { get; init; }

    /// <summary><c>#RRGGBB</c>.</summary>
    public string? Color { get; init; }
}

/// <summary>
/// The trigger: one flat record, the fields that apply depend on <see cref="Type"/> (§6.3).
/// </summary>
public sealed record RoutineTrigger
{
    /// <summary>One of <see cref="RoutineTriggerTypes"/>; anything else is <c>unsupported_trigger</c>.</summary>
    public string? Type { get; init; }

    /// <summary><c>home.arrive</c>, <c>home.leave</c>.</summary>
    public string? HomeId { get; init; }

    /// <summary><c>home.leave</c>: 60-1800, default 180.</summary>
    public int? LeaveDebounceSeconds { get; init; }

    /// <summary><c>pc.sensor</c>: <c>SensorReading.Id</c>, 1-128 characters.</summary>
    public string? SensorId { get; init; }

    /// <summary><c>pc.sensor</c>: display snapshot, ≤ 64.</summary>
    public string? SensorLabel { get; init; }

    /// <summary><c>pc.sensor</c>: <c>above</c> or <c>below</c>.</summary>
    public string? Direction { get; init; }

    /// <summary><c>pc.sensor</c>: finite.</summary>
    public double? Threshold { get; init; }

    /// <summary><c>pc.sensor</c>: 5-600, default 60.</summary>
    public int? SustainSeconds { get; init; }

    /// <summary><c>pc.idle</c>: 1-240.</summary>
    public int? IdleMinutes { get; init; }

    /// <summary><c>pc.idle</c>: default true.</summary>
    public bool? IgnoreWhileMediaPlaying { get; init; }

    /// <summary><c>pc.session</c>: <c>locked</c> or <c>unlocked</c>.</summary>
    public string? SessionState { get; init; }

    /// <summary>See <see cref="Routine.IsMalformed"/>. Never on the wire.</summary>
    [JsonIgnore]
    public bool IsMalformed { get; init; }
}

/// <summary>
/// One step: one flat record, the fields that apply depend on <see cref="Type"/> (§6.4).
/// </summary>
public sealed record RoutineStep
{
    /// <summary>One of <see cref="RoutineStepTypes"/>; anything else is <c>unsupported_step</c>.</summary>
    public string? Type { get; init; }

    /// <summary><c>wake</c>: upper-case colon form, 6 bytes.</summary>
    public string? Mac { get; init; }

    /// <summary><c>wake</c>: IPv4 dotted quad, default <c>255.255.255.255</c>.</summary>
    public string? BroadcastIp { get; init; }

    /// <summary><c>wake</c>: 1-65535, default 9.</summary>
    public int? Port { get; init; }

    /// <summary><c>waitOnline</c>: 30-300, default 300.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary><c>delay</c>: 1-600.</summary>
    public int? Seconds { get; init; }

    /// <summary><c>power</c>: one of <see cref="RoutinePowerVerbs.All"/>.</summary>
    public string? Verb { get; init; }

    /// <summary><c>power</c>: 0-600, only for <see cref="RoutinePowerVerbs.Delayable"/>.</summary>
    public int? DelaySeconds { get; init; }

    /// <summary><c>launchApp</c>: <c>AppEntry.Id</c> GUID; the host resolves it at run time.</summary>
    public string? AppId { get; init; }

    /// <summary><c>launchApp</c>: display snapshot, ≤ 64.</summary>
    public string? AppLabel { get; init; }

    /// <summary><c>media</c>: one of <see cref="RoutineMediaActions"/>.</summary>
    public string? MediaAction { get; init; }

    /// <summary><c>notify</c>: <c>phone</c> or <c>pc</c>.</summary>
    public string? Target { get; init; }

    /// <summary><c>notify</c>: 1-40.</summary>
    public string? Title { get; init; }

    /// <summary><c>notify</c>: 0-120; plain text, control characters stripped at presentation.</summary>
    public string? Body { get; init; }

    /// <summary>See <see cref="Routine.IsMalformed"/>. Never on the wire.</summary>
    [JsonIgnore]
    public bool IsMalformed { get; init; }

    /// <summary>
    /// True when this step is executed by the host: <c>power</c>, <c>launchApp</c>, <c>media</c>, and
    /// <c>notify</c> with <c>target = pc</c>. In a phone-run routine these travel as
    /// <c>routine_step_request</c>.
    /// </summary>
    [JsonIgnore]
    public bool IsHostExecuted =>
        Type is RoutineStepTypes.Power or RoutineStepTypes.LaunchApp or RoutineStepTypes.Media
        || (Type == RoutineStepTypes.Notify && Target == RoutineNotifyTargets.Pc);

    /// <summary>True for a <c>power</c> step whose verb is in the D1 destructive set.</summary>
    [JsonIgnore]
    public bool IsDestructive => Type == RoutineStepTypes.Power && RoutinePowerVerbs.IsDestructive(Verb);
}
