using System;
using System.Threading.Tasks;

namespace Remex.Core.Services.Command;

public interface ISystemCommandService
{
    void Shutdown();
    void ForceShutdown();
    void Restart();
    void ForceRestart();
    void RestartToUefi();
    void Sleep();
    void Hibernate();
    void SignOut();
    void Lock();
}
