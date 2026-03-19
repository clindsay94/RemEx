using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Remex.Core;

namespace Remex.Host.Services.Network;

public class MdnsAdvertisingService : BackgroundService
{
    private readonly ILogger<MdnsAdvertisingService> _logger;
    private readonly IConfiguration _configuration;
    private ServiceDiscovery? _serviceDiscovery;

    public MdnsAdvertisingService(ILogger<MdnsAdvertisingService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The port is usually passed into HostBootstrapper. If it's not in configuration, 
            // we default to RemexConstants.DefaultPort.
            int port = _configuration.GetValue<int>("Host:Port", RemexConstants.DefaultPort);
            string instanceName = Environment.MachineName;
            
            _logger.LogInformation("Starting mDNS advertising for {InstanceName} (_remex._tcp) on port {Port}", instanceName, port);

            _serviceDiscovery = new ServiceDiscovery();
            
            // Service Name: _remex._tcp
            // Host Name: Environment.MachineName + ".local"
            var profile = new ServiceProfile(instanceName, "_remex._tcp", (ushort)port);
            
            _serviceDiscovery.Advertise(profile);

            // Keep the service alive until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("mDNS advertising service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mDNS advertising service encountered an error.");
        }
        finally
        {
            _serviceDiscovery?.Dispose();
            _serviceDiscovery = null;
        }
    }

    public override void Dispose()
    {
        _serviceDiscovery?.Dispose();
        _serviceDiscovery = null;
        base.Dispose();
    }
}
