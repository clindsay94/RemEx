namespace Remex.Agent.Services.Security;

/// <summary>
/// Cuts a device's live connections when its pairing is revoked (RemEx-6nkht).
/// </summary>
/// <remarks>
/// An interface purely so the revocation and the disconnection can be tested apart. The revoker's
/// tests are about five persistent stores and want a recording double here; the disconnector's tests
/// are about three socket lifetimes and want the real registries. Bolting the three registries onto
/// the revoker's constructor would have forced every store test to stand up a transfer manager.
/// </remarks>
public interface IPairedDeviceDisconnector
{
    /// <summary>Ends the control channel, the screen stream and the file channel for one client.</summary>
    /// <remarks>
    /// ASYNC BECAUSE OF WHERE IT UNWINDS, not because it waits for anything (review). Cancelling a
    /// token runs its registrations inline and aborting a socket completes its pending receive on the
    /// aborting thread, so calling this from the UI thread would tear a whole capture session down on
    /// the dispatcher.
    /// </remarks>
    Task DisconnectAsync(string clientId);
}
