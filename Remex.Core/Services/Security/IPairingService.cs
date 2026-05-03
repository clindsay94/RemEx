using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services.Security;

public interface IPairingService
{
    Task<PairingState> StartPairingAsync(CancellationToken ct);
    string GetActivePin();
    bool IsPairingActive { get; }
    Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct);
    void CancelPairing();
}

public sealed record PairingState(string HostPublicKeyBase64, string Pin, long ExpiresAtUnixMs);
