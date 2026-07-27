using Remex.Core.Models.IPC;
using Remex.Core.Services;
using Remex.Core.Services.Security;

namespace Remex.Desktop.Services.Security;

/// <summary>
/// Surfaces the active/generated pairing PIN to the desktop UI by delegating to the in-process host's
/// <see cref="IPairingService"/>. Previously queried a separate service process over the
/// <c>RemExLocalIPC</c> pipe (GETPAIRINGPIN / GENERATEPAIRINGPIN); RemEx 2.0 runs the host in-process,
/// so it resolves the live pairing service from <see cref="EmbeddedHostServiceLocator"/>. Mirrors the
/// mapping the deleted <c>LocalIpcServerService</c> performed. (RemEx-aep Phase 3)
/// </summary>
public sealed class IpcPairingPinQueryService : IPairingPinQueryService
{
    public Task<PairingPinInfo?> GetActivePairingPinAsync(CancellationToken cancellationToken = default)
    {
        var pairingService = EmbeddedHostServiceLocator.Require<IPairingService>();
        PairingPinInfo? result = pairingService.TryGetActivePinInfo(out var pin, out var expiresAtUnixMs)
            ? new PairingPinInfo(pin, expiresAtUnixMs)
            : null;
        return Task.FromResult(result);
    }

    public async Task<PairingPinInfo?> GeneratePairingPinAsync(CancellationToken cancellationToken = default)
    {
        var pairingService = EmbeddedHostServiceLocator.Require<IPairingService>();
        var state = await pairingService.GetOrStartPairingAsync(cancellationToken);
        return new PairingPinInfo(state.Pin, state.ExpiresAtUnixMs);
    }
}
