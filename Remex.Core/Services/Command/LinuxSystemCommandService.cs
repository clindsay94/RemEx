using System;
using System.Diagnostics;

namespace Remex.Core.Services.Command;

public class LinuxSystemCommandService : ISystemCommandService
{
    public void Shutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown", BuildShutdownArgs("-h", delaySeconds));
    }

    public void ForceShutdown(int delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            ExecuteProcess("systemctl", "poweroff -i");
            return;
        }

        ExecuteProcess("shutdown", BuildShutdownArgs("-h", delaySeconds));
    }

    public void Restart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown", BuildShutdownArgs("-r", delaySeconds));
    }

    public void ForceRestart(int delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            ExecuteProcess("systemctl", "reboot -i");
            return;
        }

        ExecuteProcess("shutdown", BuildShutdownArgs("-r", delaySeconds));
    }

    public void RestartToUefi(int delaySeconds = 0)
    {
        if (delaySeconds > 0)
        {
            System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
            {
                try { ExecuteProcess("systemctl", "reboot --firmware-setup"); } catch { }
            });
            return;
        }

        ExecuteProcess("systemctl", "reboot --firmware-setup");
    }

    public void Sleep()
    {
        ExecuteProcess("systemctl", "suspend");
    }

    public void Hibernate()
    {
        ExecuteProcess("systemctl", "hibernate");
    }

    public void SignOut()
    {
        ExecuteProcess("loginctl", "terminate-session self");
    }

    public void Lock()
    {
        ExecuteProcess("loginctl", "lock-session");
    }

    public void MonitorOff()
    {
        ExecuteProcess("sh", "-c \"xset dpms force off\"");
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
