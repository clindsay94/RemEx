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
    PairedDeviceActivityStore activity) : IPairedDeviceSource
{
    public IReadOnlyList<PairedDeviceRow> PairedDevices()
    {
        var ids = registry.PairedClientIds();
        var rows = new List<PairedDeviceRow>(ids.Count);

        foreach (var id in ids)
        {
            var seen = activity.Resolve(id);
            rows.Add(new PairedDeviceRow(
                ClientId: id,
                DeviceName: names.Resolve(id),
                FirstPairedUtc: seen?.FirstPairedUtc,
                LastSeenUtc: seen?.LastSeenUtc));
        }

        return rows;
    }
}
