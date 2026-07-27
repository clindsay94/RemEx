namespace Remex.Core.Services.Network;

public interface IWakeOnLanService
{
    Task WakeAsync(string macAddress, string broadcastIp = "255.255.255.255", int port = 9);
}
