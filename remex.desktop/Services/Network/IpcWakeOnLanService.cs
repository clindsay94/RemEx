using Remex.Core.Services.Network;

namespace Remex.Desktop.Services.Network;

/// <summary>
/// Forwards Wake-on-LAN requests from the desktop UI to the in-process host's real
/// <see cref="IWakeOnLanService"/>. Previously sent a command over the <c>RemExLocalIPC</c> pipe to a
/// separate service process; RemEx 2.0 runs the host in-process, so it resolves the live service from
/// <see cref="EmbeddedHostServiceLocator"/> and calls it directly. (RemEx-aep Phase 3)
/// </summary>
public class IpcWakeOnLanService : IWakeOnLanService
{
    public Task WakeAsync(string macAddress, string broadcastIp = "255.255.255.255", int port = 9)
        => EmbeddedHostServiceLocator.Require<IWakeOnLanService>().WakeAsync(macAddress, broadcastIp, port);
}
