using System.Diagnostics;

namespace Remex.Core.Services.Command;

public class LinuxSystemCommandService : ISystemCommandService
{
    public Task Shutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown", BuildShutdownArgs("-h", delaySeconds));
        return Task.CompletedTask;
    }

    public Task ForceShutdown(int delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            ExecuteProcess("systemctl", "poweroff -i");
            return Task.CompletedTask;
        }

        ExecuteProcess("shutdown", BuildShutdownArgs("-h", delaySeconds));
        return Task.CompletedTask;
    }

    public Task Restart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown", BuildShutdownArgs("-r", delaySeconds));
        return Task.CompletedTask;
    }

    public Task ForceRestart(int delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            ExecuteProcess("systemctl", "reboot -i");
            return Task.CompletedTask;
        }

        ExecuteProcess("shutdown", BuildShutdownArgs("-r", delaySeconds));
        return Task.CompletedTask;
    }

    public Task RestartToUefi(int delaySeconds = 0)
    {
        if (delaySeconds > 0)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
            {
                // Fire-and-forget: no caller is awaiting, so surface failures to stderr
                // (captured by the host/journal) instead of swallowing them silently.
                try
                {
                    ExecuteProcess("systemctl", "reboot --firmware-setup");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Deferred RestartToUefi failed: {ex.Message}");
                }
            });
            return Task.CompletedTask;
        }

        ExecuteProcess("systemctl", "reboot --firmware-setup");
        return Task.CompletedTask;
    }

    public Task Sleep()
    {
        ExecuteProcess("systemctl", "suspend");
        return Task.CompletedTask;
    }

    public Task Hibernate()
    {
        ExecuteProcess("systemctl", "hibernate");
        return Task.CompletedTask;
    }

    public Task SignOut()
    {
        ExecuteProcess("loginctl", "terminate-session self");
        return Task.CompletedTask;
    }

    public Task Lock()
    {
        ExecuteProcess("loginctl", "lock-session");
        return Task.CompletedTask;
    }

    public Task MonitorOff()
    {
        ExecuteProcess("sh", "-c \"xset dpms force off\"");
        return Task.CompletedTask;
    }

    private static string BuildShutdownArgs(string modeArg, int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            return $"{modeArg} now";
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(delaySeconds / 60d));
        return $"{modeArg} +{minutes}";
    }

    private void ExecuteProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to execute {fileName} {arguments}: {ex.Message}", ex);
        }
    }
}
