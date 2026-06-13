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

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
                return key?.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
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
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;

                if (enabled)
                {
                    var exePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exePath)) return;
                    key.SetValue(ValueName, $"\"{exePath}\" --minimized");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Ignored
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
