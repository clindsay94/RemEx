using System;
using System.Threading.Tasks;

namespace Remex.Core.Services.Command;

public interface ISystemCommandService
{
    void Shutdown(int delaySeconds = 0);
    void ForceShutdown(int delaySeconds = 0);
    void Restart(int delaySeconds = 0);
    void ForceRestart(int delaySeconds = 0);
    void RestartToUefi(int delaySeconds = 0);
    void Sleep();
    void Hibernate();
    void SignOut();
    void Lock();
    void MonitorOff();
}
