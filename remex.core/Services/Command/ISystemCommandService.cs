using System;
using System.Threading.Tasks;

namespace Remex.Core.Services.Command;

/// <summary>
/// System power/session commands. Task-returning so transports that proxy the command
/// over IPC (see IpcClientCommandService) can await the round trip instead of blocking.
/// Local implementations complete synchronously and return Task.CompletedTask.
/// </summary>
public interface ISystemCommandService
{
    Task Shutdown(int delaySeconds = 0);
    Task ForceShutdown(int delaySeconds = 0);
    Task Restart(int delaySeconds = 0);
    Task ForceRestart(int delaySeconds = 0);
    Task RestartToUefi(int delaySeconds = 0);
    Task Sleep();
    Task Hibernate();
    Task SignOut();
    Task Lock();
    Task MonitorOff();
}
