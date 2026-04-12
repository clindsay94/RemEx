using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace Remex.Core.Services.Network;

public class MdnsDiscoveryService : IMdnsDiscoveryService
{
    private readonly ILogger<MdnsDiscoveryService> _logger;

    public MdnsDiscoveryService(ILogger<MdnsDiscoveryService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<MdnsHostDiscoveredEventArgs>? HostDiscovered;

    public async Task<IReadOnlyList<string>> DiscoverHostsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var mdns = new MulticastService();
        using var discovery = new ServiceDiscovery(mdns);

        _logger.LogInformation("Starting mDNS discovery for _remex._tcp (timeout: {Timeout})", timeout);

        discovery.ServiceInstanceDiscovered += (_, args) =>
        {
            var host = TryResolveHost(args);
            if (host == null)
            {
                _logger.LogDebug("mDNS discovery skipped {InstanceName} because no SRV record was available.", args.ServiceInstanceName);
                return;
            }

            var announced = false;
            lock (hosts)
            {
                announced = hosts.Add(host.WebSocketUrl);
            }

            if (!announced)
            {
                return;
            }

            _logger.LogInformation("mDNS discovery found host at {Url}", host.WebSocketUrl);
            HostDiscovered?.Invoke(this, host);
        };

        try
        {
            mdns.Start();
            discovery.QueryServiceInstances("_remex._tcp");
            await Task.Delay(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("mDNS discovery cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mDNS discovery failed.");
        }
        finally
        {
            mdns.Stop();
        }

        return hosts.ToList();
    }

    private static MdnsHostDiscoveredEventArgs? TryResolveHost(ServiceInstanceDiscoveryEventArgs args)
    {
        var srv = args.Message.Answers.OfType<SRVRecord>().FirstOrDefault()
            ?? args.Message.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();

        if (srv == null)
        {
            return null;
        }

        var hostName = srv.Target.ToString().TrimEnd('.');
        var addressRecord = args.Message.Answers.OfType<ARecord>().FirstOrDefault(record => record.Name == srv.Target)
            ?? args.Message.AdditionalRecords.OfType<ARecord>().FirstOrDefault(record => record.Name == srv.Target);

        var resolvedHost = addressRecord?.Address.ToString() ?? hostName;
        var webSocketUrl = $"ws://{resolvedHost}:{srv.Port}{RemexConstants.WebSocketPath}";
        return new MdnsHostDiscoveredEventArgs(args.ServiceInstanceName.ToString(), hostName, srv.Port, webSocketUrl);
    }
}