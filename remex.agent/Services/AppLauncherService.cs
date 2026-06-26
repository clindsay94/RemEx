using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services;

namespace Remex.Agent.Services;

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
            // Validate and normalize the path to prevent traversal attacks
            targetPath = System.IO.Path.GetFullPath(targetPath);
            if (!System.IO.File.Exists(targetPath) && !System.IO.Directory.Exists(targetPath))
            {
                _logger.LogWarning("Launch target does not exist: {targetPath}", targetPath);
                throw new System.IO.FileNotFoundException("Launch target not found.", targetPath);
            }
            // RemEx runs inside the signed-in user's interactive session, so a normal ShellExecute
            // launches the app onto the user's desktop on every platform. The old Session-0
            // CreateProcessAsUser bridge (via WindowsActiveSession) is gone. (RemEx-aep Phase 4)
            LaunchStandard(targetPath);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch app: {targetPath}", targetPath);
            throw;
        }
    }

    private void LaunchStandard(string targetPath)
    {
        _logger.LogInformation("Launching app normally: {targetPath}", targetPath);
        var appDir = System.IO.Path.GetDirectoryName(targetPath);
        var psi = new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true,
            WorkingDirectory = appDir ?? string.Empty
        };
        Process.Start(psi)?.Dispose();
    }
}
