namespace Remex.Desktop.Services;

/// <summary>
/// Renames a paired device, for display only.
/// </summary>
/// <remarks>
/// <para>
/// **SEPARATE FROM <see cref="IPairedDeviceSource"/> ON PURPOSE (RemEx-4gbp2).** That one is a list,
/// and its own doc says a mutation of the pairing state must never sit behind something whose name
/// says otherwise. Widening it would have been one line and exactly the wrong line.
/// </para>
/// <para>
/// **DISPLAY ONLY, AND STRUCTURALLY SO.** A rename must never reach <c>PairedClientRegistry</c>,
/// which <c>docs/REGRESSION-GUARDS.md</c> names as the only authentication path in production. The
/// implementation holds the NAME STORE and nothing else, so there is no registry here to reach — the
/// safety property is enforced by what the type can see, not by remembering not to.
/// </para>
/// <para>
/// Declared on the desktop side for the dependency direction that <see cref="IClientSessionSource"/>
/// and <see cref="IPairedDeviceSource"/> already use: <c>remex.agent</c> ProjectReferences
/// <c>remex.desktop</c>, so the UI cannot name the host's types.
/// </para>
/// <para>
/// UNPAIRING IS DELIBERATELY NOT HERE. It is RemEx-5lb90, it mutates the registry, and putting it on
/// the same interface as a rename would put a revocation one method away from an edit.
/// </para>
/// </remarks>
public interface IPairedDeviceNameWriter
{
    /// <summary>
    /// Sets what a device is called, or clears the override when <paramref name="typedName"/> is
    /// blank.
    /// </summary>
    /// <remarks>
    /// BLANK CLEARS RATHER THAN STORES, per <see cref="PairedDeviceDisplayName.Normalize"/>: an empty
    /// stored name leaves the device rendering nameless with the field the user would fix it in being
    /// the one they just emptied. Clearing restores the id fallback, which is recoverable.
    /// </remarks>
    void Rename(string clientId, string? typedName);
}
