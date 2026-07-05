using System.Threading.Tasks;

namespace Remex.Core.Services;

/// <summary>
/// Interface for application launching execution commands.
/// </summary>
public interface IAppLauncherService
{
    Task LaunchAppAsync(string targetPath);
}
