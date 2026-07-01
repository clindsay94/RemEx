using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Remex.Desktop.Services;

namespace Remex.Agent.Services;

/// <summary>
/// Windows and Linux implementation of the launch-at-login registration service.
///
/// Windows: RemEx 2.0 has no Windows Service. Auto-start is an elevated Task Scheduler
/// "logon task" (LogonType=InteractiveToken, RunLevel=HighestAvailable) that starts the
/// single interactive user-session app at sign-in, elevated, with no UAC prompt. This
/// service is the in-app source of truth for the Settings "Launch at login" toggle; the
/// installer (autostart-remex.ps1 / RemEx.iss) registers an equivalent task at setup time.
///
/// Linux: per-user XDG autostart .desktop file (unchanged).
/// </summary>
public class StartupRegistrationService : IStartupRegistrationService
{
    private const string ValueName = "RemEx";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Keep "RemEx" stable — autostart-remex.ps1 and the verification steps query this exact
    // task name. The toggle and the installer must manage the same task.
    private const string WindowsTaskName = "RemEx";

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// Deletes any legacy per-user HKCU Run "RemEx" launch-at-login entry on Windows. Auto-start
    /// is now an elevated Task Scheduler logon task; a lingering Run key would start a competing
    /// medium-integrity instance that wins the single-instance guard and reintroduces the UIPI input
    /// block. Safe to call repeatedly; must run in the interactive user's session (HKCU is the
    /// signed-in user's hive). No-op off Windows. (RemEx-hmk)
    /// </summary>
    public static void RemoveLegacyWindowsRunKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort cleanup; a failure here is non-fatal.
        }
    }

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsTaskExists();
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
                var desktopFile = Path.Combine(autostartDir, "remex-client.desktop");
                if (!File.Exists(desktopFile)) return false;
                var lines = File.ReadAllLines(desktopFile);
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("X-GNOME-Autostart-enabled=", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line.Split('=')[1].Trim();
                        return value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
                return true; // default enabled if file exists
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    public void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            // The Run key is dead on Windows; always clear any legacy value so it can never start a
            // competing medium-integrity host, then enable/disable the elevated logon task. (RemEx-hmk)
            RemoveLegacyWindowsRunKey();
            if (enabled)
            {
                RegisterWindowsLogonTask();
            }
            else
            {
                RemoveWindowsLogonTask();
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
                var desktopFile = Path.Combine(autostartDir, "remex-client.desktop");

                if (!enabled)
                {
                    if (File.Exists(desktopFile))
                    {
                        File.Delete(desktopFile);
                    }
                    return;
                }

                Directory.CreateDirectory(autostartDir);

                // Mirror the installed menu launcher when present so the autostart entry stays in sync
                // with it. client-install.sh installs to the per-user applications dir; fall back to the
                // system-wide one, then to a generated entry. Keep the "remex-client.desktop" basename:
                // the whole Linux install (dir, symlink, launcher) uses that identifier and the installer
                // manages the SAME autostart file — like the stable "RemEx" task name on Windows.
                var userDesktopFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "applications", "remex-client.desktop");
                var systemDesktopFile = "/usr/share/applications/remex-client.desktop";
                var sourceDesktopFile = File.Exists(userDesktopFile) ? userDesktopFile
                    : File.Exists(systemDesktopFile) ? systemDesktopFile
                    : null;
                var exePath = Environment.ProcessPath ?? "remex-client";

                string content;
                if (sourceDesktopFile is not null)
                {
                    content = File.ReadAllText(sourceDesktopFile);
                    // Update Exec line to include --minimized
                    var lines = content.Split('\n');
                    var hasAutostartFlag = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                        {
                            // Exec values may themselves contain '=' (env vars, flags) — strip only the key.
                            var execCmd = lines[i]["Exec=".Length..].Trim();
                            if (!execCmd.Contains("--minimized"))
                            {
                                lines[i] = $"Exec={execCmd} --minimized";
                            }
                        }
                        else if (lines[i].StartsWith("X-GNOME-Autostart-enabled=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = "X-GNOME-Autostart-enabled=true";
                            hasAutostartFlag = true;
                        }
                    }
                    content = string.Join("\n", lines);
                    if (!hasAutostartFlag)
                    {
                        content = content.TrimEnd('\n') + "\nX-GNOME-Autostart-enabled=true\n";
                    }
                }
                else
                {
                    // Branding matches the installed launcher (installer/linux/remex-client.desktop):
                    // Name=RemEx, Icon=remex. --minimized starts it to the tray, ready for the phone.
                    content = $"""
[Desktop Entry]
Type=Application
Name=RemEx
Comment=Remote desktop and system monitoring
Exec="{exePath}" --minimized
Icon=remex
Categories=Network;RemoteAccess;
Terminal=false
StartupWMClass=Remex.Agent
X-GNOME-Autostart-enabled=true
""";
                }

                File.WriteAllText(desktopFile, content);
            }
            catch
            {
                // Ignored
            }
        }
    }

    // ---- Windows scheduled-task helpers ----------------------------------------------------

    private static bool WindowsTaskExists()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // schtasks /Query exits 0 when the task exists, 1 when it does not.
            return RunSchtasks($"/Query /TN \"{WindowsTaskName}\"") == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterWindowsLogonTask()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Debug.WriteLine("[Remex] RegisterWindowsLogonTask: could not resolve the running executable path.");
            return;
        }

        var account = WindowsIdentity.GetCurrent().Name; // DOMAIN\User or COMPUTER\User
        var xml = BuildLogonTaskXml(exePath, account);

        // schtasks is encoding-sensitive: write the definition as UTF-16 (with BOM).
        var xmlPath = Path.Combine(Path.GetTempPath(), $"remex-logon-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var exit = RunSchtasks($"/Create /TN \"{WindowsTaskName}\" /XML \"{xmlPath}\" /F");
            if (exit != 0)
            {
                Debug.WriteLine($"[Remex] schtasks /Create for '{WindowsTaskName}' returned exit code {exit}.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Remex] Failed to register the RemEx logon task: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    private static void RemoveWindowsLogonTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // /F suppresses the confirmation prompt; a missing task is a non-fatal non-zero exit.
            RunSchtasks($"/Delete /TN \"{WindowsTaskName}\" /F");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Remex] Failed to remove the RemEx logon task: {ex.Message}");
        }
    }

    /// <summary>
    /// Task Scheduler XML for an elevated logon task. LogonType=InteractiveToken +
    /// RunLevel=HighestAvailable is what makes RemEx start elevated at sign-in with no UAC prompt;
    /// this mirrors the autostart-remex.ps1 New-ScheduledTaskPrincipal definition (kept in sync).
    /// </summary>
    private static string BuildLogonTaskXml(string exePath, string account)
    {
        var safeExe = SecurityElement.Escape(exePath);
        var safeAccount = SecurityElement.Escape(account);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts RemEx at sign-in, elevated, so your PC is reachable from your phone.</Description>
    <URI>\{WindowsTaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{safeAccount}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{safeAccount}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{safeExe}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static int RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return -1;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
