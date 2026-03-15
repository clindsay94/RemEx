using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Remex.Core.Services.Command;

public class WindowsSystemCommandService : ISystemCommandService
{
    public void Shutdown()
    {
        ExecuteProcess("shutdown.exe", "/s /t 0");
    }

    public void Restart()
    {
        ExecuteProcess("shutdown.exe", "/r /t 0");
    }

    public void ForceRestart()
    {
        ExecuteProcess("shutdown.exe", "/r /f /t 0");
    }

    public void RestartToUefi()
    {
        ExecuteProcess("shutdown.exe", "/r /fw /t 0");
    }

    public void Lock()
    {
        ExecuteProcess("rundll32.exe", "user32.dll,LockWorkStation");
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
        catch (Win32Exception ex)
        {
            // Catching Win32Exception as requested for Access Denied situations
            throw new Exception($"Failed to execute {fileName} {arguments}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to execute {fileName} {arguments}: {ex.Message}", ex);
        }
    }
}
