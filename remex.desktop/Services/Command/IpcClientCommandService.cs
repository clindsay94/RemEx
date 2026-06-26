using System.Threading.Tasks;
using Remex.Core.Services.Command;

namespace Remex.Desktop.Services.Command;

/// <summary>
/// Forwards system commands from the desktop UI to the in-process host's real
/// <see cref="ISystemCommandService"/>. Historically this opened the <c>RemExLocalIPC</c> named pipe
/// to a separate service process; RemEx 2.0 runs the host in-process, so it now resolves the live
/// service from <see cref="EmbeddedHostServiceLocator"/> and calls it directly. The name is retained
/// to keep the DI registration in <c>CommandModeContext</c> stable. (RemEx-aep Phase 3)
/// </summary>
public class IpcClientCommandService : ISystemCommandService
{
    private static ISystemCommandService Inner => EmbeddedHostServiceLocator.Require<ISystemCommandService>();

    public Task Shutdown(int delaySeconds = 0) => Inner.Shutdown(delaySeconds);
    public Task ForceShutdown(int delaySeconds = 0) => Inner.ForceShutdown(delaySeconds);
    public Task Restart(int delaySeconds = 0) => Inner.Restart(delaySeconds);
    public Task ForceRestart(int delaySeconds = 0) => Inner.ForceRestart(delaySeconds);
    public Task RestartToUefi(int delaySeconds = 0) => Inner.RestartToUefi(delaySeconds);
    public Task Sleep() => Inner.Sleep();
    public Task Hibernate() => Inner.Hibernate();
    public Task SignOut() => Inner.SignOut();
    public Task Lock() => Inner.Lock();
    public Task MonitorOff() => Inner.MonitorOff();
}
