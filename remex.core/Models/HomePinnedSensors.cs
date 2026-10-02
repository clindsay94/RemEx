namespace Remex.Core.Models;

/// <summary>
/// The PC Home's pinned-sensor list, carried host → phone by <c>home_pins_sync</c> (RemEx-wqo7a.5).
/// </summary>
/// <remarks>
/// <para>
/// **ONLY THE NAME LIST TRAVELS, NEVER CARDS OR POSITIONS.** A host-side mirror of the phone's layout
/// was removed after it lost user data (REGRESSION-GUARDS, RemEx-sydzo), so this record carries
/// exactly what both Homes need to agree on and nothing that either side lays out. The PC is the
/// single owner: its <c>DashboardProfile.PinnedSensorIds</c> is the list, and the phone only ever asks
/// for one change at a time with <see cref="HomePinChange"/>.
/// </para>
/// <para>
/// **NAMES, NOT IDS.** The phone's <c>TelemetrySensor.id</c> is a slug of its own making, so the shared
/// key is <c>SensorReading.Name</c>, compared OrdinalIgnoreCase on both sides.
/// </para>
/// <para>
/// NOTHING HERE IS <c>required</c>, and every member has a default, for the reason written on
/// <see cref="PhoneThemeSnapshot"/>: a required member missing from the JSON throws, the envelope
/// becomes null, and the receive loop drops the whole session. NOTE that the source-generated reader
/// sets an ABSENT init-only list to null despite the initializer, so a reader normalizes through
/// <c>HomePinsValidation.NormalizeNames</c> rather than trusting these to be non-null.
/// </para>
/// </remarks>
public sealed record HomePinnedSensors
{
    /// <summary>The pinned sensors, in the PC Home's order.</summary>
    public List<string> SensorNames { get; init; } = [];

    /// <summary>
    /// The sensors that CAN be pinned: those with a placed sensor card on the PC canvas. The phone greys
    /// out the pin toggle for anything outside this list, because the PC cannot make it durable.
    /// </summary>
    public List<string> PinnableSensorNames { get; init; } = [];

    /// <summary>
    /// Monotonic per host process. The phone discards a sync older than the last one it accepted on
    /// the same connection, and forgets the counter on reconnect (a restarted host starts again).
    /// </summary>
    public long Revision { get; init; }

    /// <summary>When the host last changed the list. For display only; never for ordering.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>
/// One phone-requested pin change, carried phone → host by <c>home_pins_change</c> (RemEx-wqo7a.5).
/// </summary>
/// <remarks>
/// A DELTA, NEVER A WHOLE LIST. The host applies changes in arrival order, so two people toggling
/// different sensors both win, and the same sensor toggled twice ends where the later arrival left
/// it — with no clock on either side to disagree about.
/// </remarks>
public sealed record HomePinChange
{
    /// <summary>The sensor's display name (<c>SensorReading.Name</c>).</summary>
    public string SensorName { get; init; } = string.Empty;

    /// <summary>True to pin it to the Home, false to unpin it.</summary>
    public bool Pinned { get; init; }
}
