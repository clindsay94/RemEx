using Makaretu.Dns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            var addresses = GetAdvertisableAddresses().ToArray();
             
            // Service Name: _remex._tcp
            // Host Name: Environment.MachineName + ".local"
            var profile = addresses.Length > 0
                ? new ServiceProfile(instanceName, "_remex._tcp", (ushort)port, addresses)
                : new ServiceProfile(instanceName, "_remex._tcp", (ushort)port);

            if (addresses.Length > 0)
            {
                _logger.LogInformation(
                    "Advertising RemEx mDNS on {Addresses}",
                    string.Join(", ", addresses.Select(address => address.ToString())));
            }
            else
            {
                _logger.LogWarning("No preferred IPv4 address was found for mDNS advertising; falling back to the library default address selection.");
            }
             
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

    private static IEnumerable<IPAddress> GetAdvertisableAddresses()
    {
        var primary = TryGetPrimaryIpv4Address();
        if (primary is not null)
        {
            yield return primary;
            yield break;
        }

        foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(IsPreferredInterface)
                     .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                     .Select(info => info.Address)
                     .Where(IsPreferredIpv4Address)
                     .Distinct())
        {
            yield return address;
        }
    }

    private static IPAddress? TryGetPrimaryIpv4Address()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPreferredInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        var description = $"{networkInterface.Name} {networkInterface.Description}";
        return description.Contains("WSL", StringComparison.OrdinalIgnoreCase) is false
            && description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) is false
            && description.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) is false
            && description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) is false;
    }

    private static bool IsPreferredIpv4Address(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }
}
