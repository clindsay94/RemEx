using System;
using System.Threading.Tasks;

namespace Remex.Core.Services.Command;

public interface ISystemCommandService
{
    void Shutdown();
    void Restart();
    void ForceRestart();
    void RestartToUefi();
    void Lock();
}
