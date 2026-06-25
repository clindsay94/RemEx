using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services.Network;

public interface INetworkListener
{
    Task StartListeningAsync(CancellationToken cancellationToken);
    void StopListening();
}
