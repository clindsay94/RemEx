using System;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services.Security;

public interface IPairingService
{
    Task<PairingState> StartPairingAsync(CancellationToken ct);
    Task<string> DeriveSessionKeyAsync(string clientPublicKeyBase64, CancellationToken ct);
    string GetActivePin();
    bool IsPairingActive { get; }
    Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct);
    void CancelPairing();

    /// <summary>
    /// Raised when a new pairing PIN becomes active. The desktop UI subscribes to this
    /// to show the PIN to the user so they can enter it on their phone.
    /// Args: (pin, expiresAtUnixMs).
    /// </summary>
    event Action<string, long>? PinDisplayed;

    /// <summary>
    /// Raised when an active PIN session ends (consumed, expired, or cancelled).
    /// </summary>
    event Action? PinCleared;
}

public sealed record PairingState(string HostPublicKeyBase64, string Pin, long ExpiresAtUnixMs);
