using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Remex.Core.Services.Network;

namespace Remex.Host.Services.Network;

public class ExternalNetworkListenerService : BackgroundService
{
    private readonly INetworkListener _networkListener;

    public ExternalNetworkListenerService(INetworkListener networkListener)
    {
        _networkListener = networkListener;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _networkListener.StartListeningAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _networkListener.StopListening();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _networkListener.StopListening();
        base.Dispose();
    }
}
