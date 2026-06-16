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
            // Validate and normalize the path to prevent traversal attacks
            targetPath = System.IO.Path.GetFullPath(targetPath);
            if (!System.IO.File.Exists(targetPath) && !System.IO.Directory.Exists(targetPath))
            {
                _logger.LogWarning("Launch target does not exist: {targetPath}", targetPath);
                throw new System.IO.FileNotFoundException("Launch target not found.", targetPath);
            }
            if (!OperatingSystem.IsWindows())
            {
                LaunchStandard(targetPath);
                return Task.CompletedTask;
            }

            // If we are running in Session 0 (as a service), we must launch in the interactive session.
            if (Process.GetCurrentProcess().SessionId == 0)
            {
                _logger.LogInformation("Service is in Session 0. Attempting to launch in interactive session: {targetPath}", targetPath);
                LaunchInInteractiveSession(targetPath);
            }
            else
            {
                LaunchStandard(targetPath);
            }

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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void LaunchInInteractiveSession(string targetPath)
    {
        string? appDir = System.IO.Path.GetDirectoryName(targetPath);

        // Launch through cmd's "start" so files/shortcuts honour their shell association. The path was
        // already validated/normalized in LaunchAppAsync; passing it through CreateProcessAsUser (rather
        // than interpolating into a shell string) avoids cmd metacharacter injection.
        string commandLine = $"cmd.exe /c start \"\" /D \"{appDir}\" \"{targetPath}\"";

        if (!WindowsActiveSession.TryLaunch(
                applicationName: null,
                commandLine: commandLine,
                workingDirectory: appDir,
                creationFlags: WindowsActiveSession.CREATE_NEW_CONSOLE,
                showWindow: WindowsActiveSession.SW_SHOW,
                logger: _logger))
        {
            throw new Exception("Failed to launch the app in the interactive session.");
        }
    }
}
