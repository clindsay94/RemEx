using System.Collections.Generic;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services;

public interface IProcessMonitorService
{
    Task<List<ProcessInfo>> GetProcessesAsync();
    ProcessKillResult KillProcess(int processId);
}
