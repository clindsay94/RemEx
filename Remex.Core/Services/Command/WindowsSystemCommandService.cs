using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Remex.Core.Services.Command;

public class WindowsSystemCommandService : ISystemCommandService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    public void Shutdown()
    {
        ExecuteProcess("shutdown.exe", "/s /t 0");
    }

    public void ForceShutdown()
    {
        ExecuteProcess("shutdown.exe", "/s /f /t 0");
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

    public void Sleep()
    {
        ExecuteProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
    }

    public void Hibernate()
    {
        ExecuteProcess("shutdown.exe", "/h");
    }

    public void SignOut()
    {
        ExecuteProcess("shutdown.exe", "/l");
    }

    public void Lock()
    {
        if (!LockWorkStation())
        {
            var error = Marshal.GetLastWin32Error();
            throw new Exception($"LockWorkStation failed with error code {error}.");
        }
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
