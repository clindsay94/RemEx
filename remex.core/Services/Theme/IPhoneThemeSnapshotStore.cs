using Remex.Core.Models;

namespace Remex.Core.Services.Theme;

/// <summary>
/// The host's record of the most recent <c>theme_sync</c> (RemEx-sudp8). Lives in
/// <c>Remex.Core</c> so the desktop UI can depend on the abstraction without referencing
/// <c>Remex.Agent</c>, the same reason <c>IFileTrustService</c> does.
/// </summary>
/// <remarks>
/// SURVIVES RESTARTS AND DISCONNECTS. The host's implementation persists the latest snapshot and
/// reloads it at startup (RemEx-qean1), and nothing clears it on disconnect: a phone that synced once
/// keeps being "the phone" until it syncs again, so "Match my phone" stays offered across a host
/// restart and a reconnect. The phone also resends its theme after every successful connect.
/// </remarks>
public interface IPhoneThemeSnapshotStore
{
    /// <summary>The most recently accepted snapshot, or null if no phone has synced one yet.</summary>
    PhoneThemeSnapshot? Latest { get; }

    /// <summary>Raised every time <see cref="Set"/> accepts a new snapshot.</summary>
    event Action<PhoneThemeSnapshot?>? Changed;

    /// <summary>Records a validated snapshot as the latest and raises <see cref="Changed"/>.</summary>
    void Set(PhoneThemeSnapshot snapshot);
}
