using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Remex.Core.Services.Command;

public class WindowsSystemCommandService : ISystemCommandService
{
    private const int HwndBroadcast = 0xffff;
    private const int WmSyscommand = 0x0112;
    private const int ScMonitorpower = 0xF170;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public Task Shutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", $"/s /t {NormalizeDelay(delaySeconds)}");
        return Task.CompletedTask;
    }

    public Task ForceShutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", $"/s /f /t {NormalizeDelay(delaySeconds)}");
        return Task.CompletedTask;
    }

    public Task Restart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", $"/r /t {NormalizeDelay(delaySeconds)}");
        return Task.CompletedTask;
    }

    public Task ForceRestart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", $"/r /f /t {NormalizeDelay(delaySeconds)}");
        return Task.CompletedTask;
    }

    public Task RestartToUefi(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", $"/r /fw /t {NormalizeDelay(delaySeconds)}");
        return Task.CompletedTask;
    }

    public Task Sleep()
    {
        ExecuteProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
        return Task.CompletedTask;
    }

    public Task Hibernate()
    {
        ExecuteProcess("shutdown.exe", "/h");
        return Task.CompletedTask;
    }

    public Task SignOut()
    {
        ExecuteProcess("shutdown.exe", "/l");
        return Task.CompletedTask;
    }

    public Task Lock()
    {
        if (!LockWorkStation())
        {
            var error = Marshal.GetLastWin32Error();
            throw new Exception($"LockWorkStation failed with error code {error}.");
        }
        return Task.CompletedTask;
    }

    public Task MonitorOff()
    {
        _ = SendMessage((IntPtr)HwndBroadcast, WmSyscommand, (IntPtr)ScMonitorpower, (IntPtr)2);
        return Task.CompletedTask;
    }

    private static int NormalizeDelay(int delaySeconds)
    {
        return Math.Clamp(delaySeconds, 0, 315360000);
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
