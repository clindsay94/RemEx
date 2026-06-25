using Makaretu.Dns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Remex.Core;

namespace Remex.Agent.Services.Network;

public class MdnsAdvertisingService : BackgroundService
{
    private readonly ILogger<MdnsAdvertisingService> _logger;
    private readonly IConfiguration _configuration;
    private ServiceDiscovery? _serviceDiscovery;

    // Guards the advertised profile + re-advertise logic so the NetworkAddressChanged
    // event handler (raised on an arbitrary thread pool thread) cannot race the initial
    // advertise or a concurrent re-advertise.
    private readonly object _advertiseLock = new();
    private ServiceProfile? _currentProfile;
    private string _instanceName = string.Empty;
    private int _port;

    public MdnsAdvertisingService(ILogger<MdnsAdvertisingService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The port is usually passed into HostBootstrapper. If it's not in configuration,
        // we default to RemexConstants.DefaultPort.
        _port = _configuration.GetValue<int>("Host:Port", RemexConstants.DefaultPort);
        _instanceName = _port == RemexConstants.DefaultPort ? Environment.MachineName : $"{Environment.MachineName} ({_port})";

        _logger.LogInformation("Starting mDNS advertising for {InstanceName} (_remex._tcp) on port {Port}", _instanceName, _port);

        _serviceDiscovery = new ServiceDiscovery();

        // Re-advertise whenever an interface address changes (DHCP renew, NIC switch,
        // VPN up/down) so the host stays discoverable on the segment the client is on.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        try
        {
            ReadvertiseFromCurrentAddresses();

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
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _serviceDiscovery?.Dispose();
            _serviceDiscovery = null;
            _currentProfile = null;
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogInformation("Network address change detected; re-advertising RemEx mDNS.");
            ReadvertiseFromCurrentAddresses();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-advertise mDNS after a network address change.");
        }
    }

    private void ReadvertiseFromCurrentAddresses()
    {
        var addresses = GetAdvertisableAddresses().ToArray();

        lock (_advertiseLock)
        {
            var serviceDiscovery = _serviceDiscovery;
            if (serviceDiscovery is null)
            {
                return;
            }

            // Send a goodbye for the previous profile before announcing the rebuilt set.
            if (_currentProfile is not null)
            {
                try { serviceDiscovery.Unadvertise(_currentProfile); } catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignoring failure to unadvertise the previous mDNS profile.");
                }
            }

            // Service Name: _remex._tcp
            // Host Name: Environment.MachineName + ".local"
            var profile = addresses.Length > 0
                ? new ServiceProfile(_instanceName, "_remex._tcp", (ushort)_port, addresses)
                : new ServiceProfile(_instanceName, "_remex._tcp", (ushort)_port);

            if (addresses.Length > 0)
            {
                _logger.LogInformation(
                    "Advertising RemEx mDNS on {Addresses}",
                    string.Join(", ", addresses.Select(address => address.ToString())));
            }
            else
            {
                _logger.LogWarning("No preferred address was found for mDNS advertising; falling back to the library default address selection.");
            }

            serviceDiscovery.Advertise(profile);
            _currentProfile = profile;
        }
    }

    public override void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _serviceDiscovery?.Dispose();
        _serviceDiscovery = null;
        _currentProfile = null;
        base.Dispose();
    }

    // Virtual / container / VPN interface name and description fragments to exclude.
    // Covers both Windows (vEthernet, Hyper-V, WSL, "Virtual") and Linux/CachyOS
    // (docker0, virbr0, tailscale0, wg*, veth*) so a multi-NIC host advertises only the
    // real physical/Wi-Fi segments the client can actually reach.
    private static readonly string[] VirtualInterfaceFragments =
    {
        "WSL",
        "Hyper-V",
        "vEthernet",
        "Virtual",
        "docker",
        "virbr",
        "tailscale",
        "wg",
        "veth",
    };

    private static IEnumerable<IPAddress> GetAdvertisableAddresses()
    {
        // Advertise every preferred unicast address across all real interfaces rather than
        // a single "primary" address. Multi-NIC and Linux hosts may face the client on a
        // segment other than the default route, so all reachable addresses are announced.
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsPreferredInterface)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(info => info.Address)
            .Where(IsPreferredAddress)
            .Distinct();
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

        var name = networkInterface.Name;
        var description = $"{name} {networkInterface.Description}";

        foreach (var fragment in VirtualInterfaceFragments)
        {
            // Match the short fragments (wg, veth, virbr) against the interface name to
            // avoid false positives in long human-readable descriptions, and match the
            // descriptive fragments against the combined name+description.
            if (name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                || description.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPreferredAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Exclude IPv4 link-local (169.254.0.0/16).
            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Include routable/ULA IPv6 but skip link-local (fe80::/10) which requires a
            // scope id to be usable by the client, and skip site-local (deprecated).
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal;
        }

        return false;
    }
}
