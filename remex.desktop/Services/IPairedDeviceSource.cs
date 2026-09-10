using System;
using System.Collections.Generic;

namespace Remex.Desktop.Services;

/// <summary>
/// One paired device, as the desktop UI needs to show it.
/// </summary>
/// <param name="ClientId">The opaque pairing id. Never blank; it is the fallback display name.</param>
/// <param name="DeviceName">What the device calls itself, or null if it never said.</param>
/// <param name="NameOverride">What the USER chose to call it, or null when they have not.</param>
/// <param name="FirstPairedUtc">When it first paired, or null when that is not known.</param>
/// <param name="LastSeenUtc">When it was last connected, or null when that is not known.</param>
/// <param name="IsOnline">Whether this specific device has a live connection right now.</param>
/// <remarks>
/// NO SECRET, AND NO ROOM FOR ONE. The registry this is composed from stores a reconnect secret per
/// client and is the only authentication path in production; this record deliberately has nowhere to
/// put one, so a later "just add the field" cannot quietly carry a credential into a view model, a
/// log line or a diagnostics export.
/// <para>
/// <c>IsOnline</c> IS PER-DEVICE, not "is any phone attached". <see cref="PhonePresence"/> answers
/// the latter for the shell's single dot; a list of devices needs to know WHICH one is connected, and
/// answering it from the whole-app presence would light every row whenever any phone was on.
/// </para>
/// <para>
/// THE TIMESTAMPS ARE NULLABLE AND THAT IS LOAD-BEARING. They come from a file a user can edit, and
/// devices paired before that file existed have no row at all. A UI renders null as "unknown"; it
/// must never refuse to list a device because a date is missing.
/// </para>
/// </remarks>
public readonly record struct PairedDeviceRow(
    string ClientId,
    string? DeviceName,
    string? NameOverride,
    DateTimeOffset? FirstPairedUtc,
    DateTimeOffset? LastSeenUtc,
    bool IsOnline);

/// <summary>
/// The devices the embedded host has paired, as the desktop UI can see them.
/// </summary>
/// <remarks>
/// <para>
/// DECLARED ON THE DESKTOP SIDE FOR A DEPENDENCY DIRECTION, exactly as
/// <see cref="IClientSessionSource"/> is: <c>remex.agent</c> ProjectReferences <c>remex.desktop</c>,
/// so a reference the other way would be a cycle and the UI cannot name the host's types.
/// </para>
/// <para>
/// READ-ONLY, AND IT STAYS THAT WAY. Renaming a device is display-only and belongs to the name
/// store; unpairing goes through the registry's own revoke path. Putting either on this interface
/// would put a mutation of the sole authentication path behind something whose name says it is a
/// list (RemEx-nrsv's stated safety property).
/// </para>
/// <para>
/// Resolve it from <c>App.EmbeddedHostServices</c> on every read rather than caching one in a
/// constructor — the host publishes its container after it starts, so a view model built first would
/// otherwise cache null for the session (the mistake found in review of RemEx-n8xk).
/// </para>
/// </remarks>
public interface IPairedDeviceSource
{
    /// <summary>Every paired device, ordered stably. Never null; empty when none.</summary>
    IReadOnlyList<PairedDeviceRow> PairedDevices();
}
