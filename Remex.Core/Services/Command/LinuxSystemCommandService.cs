using System;
using System.Diagnostics;

namespace Remex.Core.Services.Command;

public class LinuxSystemCommandService : ISystemCommandService
{
    public void Shutdown()
    {
        ExecuteProcess("shutdown", "-h now");
    }

    public void Restart()
    {
        ExecuteProcess("shutdown", "-r now");
    }

    public void ForceRestart()
    {
        // Linux standard restart is generally forceful enough
        ExecuteProcess("shutdown", "-r now");
    }

    public void RestartToUefi()
    {
        throw new NotSupportedException("RestartToUefi is not supported on Linux.");
    }

    public void Lock()
    {
        throw new NotSupportedException("Lock is not supported on Linux generically via CLI.");
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
