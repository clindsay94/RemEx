using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Remex.Core;

namespace Remex.Client.Services.Network;

public class MdnsDiscoveryService
{
    private readonly ILogger<MdnsDiscoveryService> _logger;

    public MdnsDiscoveryService(ILogger<MdnsDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Discovers Remex hosts on the local network using mDNS.
    /// </summary>
    /// <param name="timeout">How long to listen for advertisements.</param>
    /// <returns>A list of WebSocket URLs.</returns>
    public async Task<List<string>> DiscoverHostsAsync(TimeSpan timeout)
    {
        var hosts = new HashSet<string>();
        using var mdns = new MulticastService();
        using var sd = new ServiceDiscovery(mdns);

        _logger.LogInformation("Starting mDNS discovery for _remex._tcp (timeout: {Timeout})", timeout);

        sd.ServiceInstanceDiscovered += (s, e) =>
        {
            var serviceName = e.ServiceInstanceName;
            _logger.LogDebug("MdnsDiscovery: Discovered service instance {InstanceName}", serviceName);

            // The SRV record contains the target hostname and port.
            // Often it's in the same message (Additional records).
            var srv = e.Message.Answers.OfType<SRVRecord>().FirstOrDefault() 
                     ?? e.Message.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();

            if (srv != null)
            {
                var target = srv.Target.ToString();
                
                // Look for ARecord to resolve IP directly
                var aRecord = e.Message.Answers.OfType<ARecord>().FirstOrDefault(a => a.Name == srv.Target)
                            ?? e.Message.AdditionalRecords.OfType<ARecord>().FirstOrDefault(a => a.Name == srv.Target);
                
                if (aRecord != null)
                {
                    target = aRecord.Address.ToString();
                }

                var port = srv.Port;
                var url = $"ws://{target}:{port}{RemexConstants.WebSocketPath}";
                
                lock (hosts)
                {
                    if (hosts.Add(url))
                    {
                        _logger.LogInformation("MdnsDiscovery: Found host at {Url}", url);
                    }
                }
            }
            else
            {
                // If SRV is missing, we could try to infer from instance name, 
                // but instance name isn't necessarily the hostname.
                // However, many implementations put the hostname in the instance name.
                _logger.LogDebug("MdnsDiscovery: SRV record missing for {InstanceName}, skipping.", serviceName);
            }
        };

        try
        {
            mdns.Start();
            sd.QueryServiceInstances("_remex._tcp");
            
            await Task.Delay(timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MdnsDiscovery: Error during discovery");
        }
        finally
        {
            mdns.Stop();
        }

        return hosts.ToList();
    }
}
