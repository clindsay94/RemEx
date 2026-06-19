using System;
using System.IO;
using Microsoft.Win32;
using Remex.Client.Services;

namespace Remex.Client.Desktop.Services;

/// <summary>
/// Windows and Linux implementation of the launch-at-login registration service.
/// </summary>
public class StartupRegistrationService : IStartupRegistrationService
{
    private const string ValueName = "RemEx";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // On Windows the Session-0 service owns elevated autostart: it spawns the interactive GUI host at
    // HIGH integrity (see InteractiveDesktopHostLauncher). A per-user HKCU Run entry would start a
    // competing MEDIUM-integrity instance that wins the single-instance guard and reintroduces the
    // UIPI input block, so launch-at-login is NOT user-managed on Windows. Linux still uses the
    // per-user XDG autostart .desktop file. (RemEx-hmk)
    public bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Deletes any legacy per-user HKCU Run "RemEx" launch-at-login entry on Windows. The Session-0
    /// service now owns elevated autostart; a lingering Run key would start a competing
    /// medium-integrity GUI host that wins the single-instance guard and reintroduces the UIPI input
    /// block. Safe to call repeatedly; must run in the interactive user's session (HKCU is the
    /// signed-in user's hive), not the LocalSystem service. No-op off Windows. (RemEx-hmk)
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
            // Launch-at-login is service-managed on Windows (see IsSupported); the HKCU Run key is
            // legacy and proactively removed, so it is never the source of truth here.
            return false;
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
            // Never create the HKCU Run key on Windows — the Session-0 service owns elevated
            // autostart. Always clear any legacy entry so an old "launch at login" value cannot start
            // a competing medium-integrity host. (RemEx-hmk)
            RemoveLegacyWindowsRunKey();
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

                // Try to find the system desktop file to copy, or generate one
                var systemDesktopFile = "/usr/share/applications/remex-client.desktop";
                var exePath = Environment.ProcessPath ?? "remex-client";
                
                string content;
                if (File.Exists(systemDesktopFile))
                {
                    content = File.ReadAllText(systemDesktopFile);
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
                    content = $"""
[Desktop Entry]
Type=Application
Name=RemEx Client
Comment=Remote PC Management Client
Exec="{exePath}" --minimized
Icon=remex-client
Categories=Utility;
Terminal=false
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
}
