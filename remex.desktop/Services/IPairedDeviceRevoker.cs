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
/// IT ENDS LIVE CONNECTIONS TOO, NOT ONLY FUTURE ONES (RemEx-6nkht). <c>IsClientPaired</c> is
/// consulted when a connection is ESTABLISHED and nowhere afterwards, so clearing the credential
/// alone left a phone that was already mirroring the desktop mirroring it. The control channel, the
/// screen stream and the file channel are all cut for that device, each by the class that owns its
/// lifetime. Best effort by design: the credential is already gone, so a socket that survives lives
/// only until its next authentication.
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
    /// present on disk — meaning the device comes back on the next restart. That case throws a
    /// <see cref="PairedDeviceRevocationException"/> with <c>PairingMayReturn</c> set, and the caller
    /// must say so in different words from an ordinary failure: it is the only one the user can act
    /// on, and the action is to unpair the device again after restarting (RemEx-pynli).
    /// </para>
    /// </remarks>
    Task RevokeAsync(string clientId, CancellationToken ct);
}
