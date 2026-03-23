using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Core.Services.Network;

public interface IMdnsDiscoveryService
{
    event EventHandler<MdnsHostDiscoveredEventArgs>? HostDiscovered;

    Task<IReadOnlyList<string>> DiscoverHostsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class MdnsHostDiscoveredEventArgs : EventArgs
{
    public MdnsHostDiscoveredEventArgs(string serviceInstanceName, string hostName, int port, string webSocketUrl)
    {
        ServiceInstanceName = serviceInstanceName;
        HostName = hostName;
        Port = port;
        WebSocketUrl = webSocketUrl;
    }

    public string ServiceInstanceName { get; }

    public string HostName { get; }

    public int Port { get; }

    public string WebSocketUrl { get; }
}