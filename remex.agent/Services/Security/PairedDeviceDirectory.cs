using Remex.Desktop.Services;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Composes the paired-device list the desktop shows, from the three stores that each own one part
/// of it (RemEx-nrsv).
/// </summary>
/// <remarks>
/// <para>
/// THREE STORES, ONE LIST, AND THE SPLIT IS DELIBERATE. <see cref="PairedClientRegistry"/> owns WHO
/// is paired and is the only authentication path in production; <see cref="PairedClientNameStore"/>
/// owns what each device is called; <see cref="PairedDeviceActivityStore"/> owns when it paired and
/// when it was last seen. Only the first is security-relevant, and keeping the other two out of it
/// is what lets a name or a date be edited, lost or corrupted without any risk to pairing.
/// </para>
/// <para>
/// THE REGISTRY IS THE SPINE. The list is driven by <see cref="PairedClientRegistry.PairedClientIds"/>
/// and joined against the other two, never the reverse — so a leftover name or activity row for a
/// device that is no longer paired cannot put it back on screen beside an unpair button. That is the
/// direction that matters: showing a device that is not paired invites revoking a pairing that does
/// not exist, while a paired device with no name still lists under its id.
/// </para>
/// </remarks>
public sealed class PairedDeviceDirectory(
    PairedClientRegistry registry,
    PairedClientNameStore names,
    PairedDeviceActivityStore activity,
    Remex.Agent.Services.ClientSessionRegistry sessions,
    PairedDeviceNameOverrideStore overrides) : IPairedDeviceSource
{
    public IReadOnlyList<PairedDeviceRow> PairedDevices()
    {
        var ids = registry.PairedClientIds();
        var chosen = overrides.Snapshot();
        var rows = new List<PairedDeviceRow>(ids.Count);

        foreach (var id in ids)
        {
            var seen = activity.Resolve(id);
            rows.Add(new PairedDeviceRow(
                ClientId: id,
                // BOTH FACTS, KEPT APART. DeviceName is what the device reported; NameOverride is
                // what the user chose. Collapsing them loses a re-pair's refreshed name or the
                // user's choice, depending which wins (review).
                DeviceName: names.Resolve(id),
                NameOverride: chosen.TryGetValue(id, out var custom) ? custom : null,
                FirstPairedUtc: seen?.FirstPairedUtc,
                LastSeenUtc: seen?.LastSeenUtc,
                // PER-DEVICE, from the session registry's own id lookup. Deliberately not derived
                // from PhonePresence, which answers "is ANY phone attached" — using it here would
                // light every row in the list whenever a single device connected.
                IsOnline: sessions.IsConnected(id)));
        }

        return rows;
    }
}
