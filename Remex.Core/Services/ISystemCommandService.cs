using System.Threading.Tasks;

namespace Remex.Core.Services;

/// <summary>
/// Interface for system-level execution commands (like launching applications).
/// </summary>
public interface ISystemCommandService
{
    Task LaunchAppAsync(string targetPath);
}
