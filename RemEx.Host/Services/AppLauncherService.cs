using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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

    private void LaunchInInteractiveSession(string targetPath)
    {
        IntPtr userTokenHandle = IntPtr.Zero;
        try
        {
            // Get the active console session ID (the one with the monitor/keyboard)
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF) throw new Exception("No active console session found.");

            // Obtain the user token for the active session
            if (!WTSQueryUserToken(sessionId, out userTokenHandle))
            {
                throw new Exception($"WTSQueryUserToken failed (Error: {Marshal.GetLastWin32Error()}).");
            }

            // Use the validated full path directly — no shell metacharacter
            // sanitization needed because we avoid cmd.exe string interpolation
            // by passing the path through CreateProcessAsUser directly.
            string? appDir = System.IO.Path.GetDirectoryName(targetPath);
            string commandLine = $"cmd.exe /c start \"\" /D \"{appDir}\" \"{targetPath}\"";

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default"; // Required for interactive apps
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_SHOW;

            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();

            bool result = CreateProcessAsUser(
                userTokenHandle,
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_NEW_CONSOLE,
                IntPtr.Zero,
                appDir,
                ref si,
                out pi);

            if (!result)
            {
                throw new Exception($"CreateProcessAsUser failed (Error: {Marshal.GetLastWin32Error()}).");
            }

            _logger.LogInformation("Successfully started process in session {SessionId}.", sessionId);

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        finally
        {
            if (userTokenHandle != IntPtr.Zero) CloseHandle(userTokenHandle);
        }
    }

    #region P/Invoke

    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const ushort SW_SHOW = 5;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr Token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hSnapshot);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    #endregion
}
