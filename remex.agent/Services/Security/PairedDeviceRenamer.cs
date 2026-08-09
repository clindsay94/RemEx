using Remex.Desktop.Services;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Applies a display rename to the paired-device name store (RemEx-4gbp2).
/// </summary>
/// <remarks>
/// <para>
/// **IT HOLDS THE OVERRIDE STORE AND NOTHING ELSE, AND THAT IS THE SAFETY PROPERTY.** RemEx-nrsv's
/// requirement is that renaming never reaches <see cref="PairedClientRegistry"/> — the sole
/// authentication path in production, where a break silently bricks every device pairing. Rather
/// than depend on the registry and remember not to touch it, this type cannot see it. A future edit
/// that wanted to would have to add a constructor parameter, which is a change a reviewer looks at.
/// </para>
/// <para>
/// IT WRITES THE USER'S OVERRIDE, NOT THE DEVICE'S REPORTED NAME. A first version wrote into
/// <see cref="PairedClientNameStore"/> and review caught it: that store holds what the device says
/// it is, so one slot per device meant a re-pair silently discarded the user's chosen name, and
/// clearing a rename deleted the reported name too — leaving a raw client id with no way back. The
/// rules (trim, cap at 48, blank CLEARS) belong to <see cref="PairedDeviceDisplayName"/> and are
/// applied by the store, so this class now does no thinking at all, which is the right amount.
/// </para>
/// </remarks>
public sealed class PairedDeviceRenamer(PairedDeviceNameOverrideStore overrides) : IPairedDeviceNameWriter
{
    public void Rename(string clientId, string? typedName) => overrides.Set(clientId, typedName);
}
