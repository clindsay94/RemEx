namespace Remex.Desktop.Services;

/// <summary>
/// Ends a device's pairing.
/// </summary>
/// <remarks>
/// <para>
/// **ITS OWN INTERFACE, NOT A METHOD ON THE LIST OR ON THE RENAMER (RemEx-5lb90).** This is the only
/// operation in the paired-device surface that touches <c>PairedClientRegistry</c>, which
/// <c>docs/REGRESSION-GUARDS.md</c> names as the ONLY authentication path in production. Putting a
/// revocation one method away from a display edit is how somebody eventually calls the wrong one;
/// keeping it separate means a caller has to ask for revocation by name.
/// </para>
/// <para>
/// IT IS NOT UNDOABLE AND THE UI MUST SAY SO. The phone has to pair again with a new PIN — there is
/// no "re-add" that restores the old pairing, because the credential is gone. A confirmation that
/// reads like "remove from list" is a trap.
/// </para>
/// <para>
/// IT DOES NOT YET END A LIVE SESSION (RemEx-6nkht). <c>IsClientPaired</c> is consulted when a
/// connection is established, so a device already streaming keeps streaming until it disconnects on
/// its own. The confirmation's promise is about the next connection; closing the current one means
/// aborting a socket mid-stream, which is its own change with its own verification.
/// </para>
/// <para>
/// Declared on the desktop side for the dependency direction the sibling interfaces already use:
/// <c>remex.agent</c> ProjectReferences <c>remex.desktop</c>, so the UI cannot name the host's types.
/// </para>
/// </remarks>
public interface IPairedDeviceRevoker
{
    /// <summary>
    /// Revokes the pairing and forgets everything recorded about the device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EVERYTHING, not just the credential. A name or a first-paired date left behind resurfaces
    /// against a device that pairs again later, showing it wearing the identity of a pairing the user
    /// deliberately ended — and a file-transfer trust grant left behind comes back as PRIVILEGE, not
    /// cosmetics: the re-paired device inherits full-device browse with no consent prompt.
    /// </para>
    /// <para>
    /// THROWS WHEN A TEARDOWN FAILED, and the caller must say so. A revocation that half-happened and
    /// reported nothing is the failure the user cannot see: the app looks like it worked, and what
    /// survived is whatever the failing store had not yet written. The credential store removes from
    /// memory before it persists, so a failure there can leave the pairing gone for this run and still
    /// present on disk — meaning the device can come back on the next restart. That specific outcome
    /// is NOT held by a test (the failure injection reaches an absent store file, not a stale one) and
    /// the error text does not yet say it; RemEx-pynli covers making the message that useful.
    /// </para>
    /// </remarks>
    Task RevokeAsync(string clientId, CancellationToken ct);
}
