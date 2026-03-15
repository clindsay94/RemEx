using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Host.Services;

public class AppLauncherService : IAppLauncherService
{
    private readonly ILogger<AppLauncherService> _logger;

    public AppLauncherService(ILogger<AppLauncherService> logger)
    {
        _logger = logger;
    }

    public Task LaunchAppAsync(string targetPath)
    {
        try
        {
            _logger.LogInformation("Launching app via UseShellExecute: {targetPath}", targetPath);

            var psi = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true // Mandatory to allow OS to resolve shortcuts like .lnk / .desktop
            };

            Process.Start(psi);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch app: {targetPath}", targetPath);
            throw;
        }
    }
}
