using Remex.Core.Models;
using Remex.Core.Services.Theme;

namespace Remex.Agent.Services.Theme;

/// <summary>
/// The host's live implementation of <see cref="IPhoneThemeSnapshotStore"/> (RemEx-sudp8). One
/// instance per running agent, registered as a singleton so <c>PingPongHandler</c> (writer) and the
/// desktop's <c>CustomizationViewModel</c> (reader, via <c>App.EmbeddedHostServices</c>) share it.
/// </summary>
/// <remarks>
/// Lock-guarded rather than a <c>ConcurrentDictionary</c>-style store like
/// <c>PairedClientNameStore</c>: there is exactly one field to protect, not a keyed collection, and
/// no disk persistence to serialize around it.
/// </remarks>
public sealed class PhoneThemeSnapshotStore : IPhoneThemeSnapshotStore
{
    private readonly object _gate = new();
    private PhoneThemeSnapshot? _latest;

    public PhoneThemeSnapshot? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public event Action<PhoneThemeSnapshot?>? Changed;

    public void Set(PhoneThemeSnapshot snapshot)
    {
        lock (_gate)
        {
            _latest = snapshot;
        }

        Changed?.Invoke(snapshot);
    }
}
