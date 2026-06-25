using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models.IPC;

namespace Remex.Desktop.Services.Security;

public interface IPairingPinQueryService
{
    Task<PairingPinInfo?> GetActivePairingPinAsync(CancellationToken cancellationToken = default);
    Task<PairingPinInfo?> GeneratePairingPinAsync(CancellationToken cancellationToken = default);
}
