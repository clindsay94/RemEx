using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Remex.Desktop.Services;

/// <summary>
/// Whether the operating system asks apps to keep animation to a minimum (sweep D8).
/// </summary>
/// <remarks>
/// <para>
/// THE PC USED TO IGNORE THIS AND THE PHONE DID NOT. Android follows the system's "remove
/// animations" setting; the PC's Reduced motion switch started off for everyone and never looked at
/// the OS, so a user who had turned animations off in Windows still got the sliding pages and the
/// moving background until they found the switch. Now the OS decides until the user does.
/// </para>
/// <para>
/// EVERY PATH DEGRADES TO "NO ANSWER" (null), NEVER TO A THROW, the same rule
/// <see cref="SystemSeedSources"/> follows: a missing settings file or an unavailable API is a reason
/// to keep animations on, not a reason to fail startup.
/// </para>
/// <list type="bullet">
/// <item>Windows: <c>SPI_GETCLIENTAREAANIMATION</c>, which is what Settings &gt; Accessibility &gt;
/// Visual effects &gt; Animation effects writes.</item>
/// <item>Linux: KDE's <c>AnimationDurationFactor</c> in <c>kdeglobals</c> (0 means off), GTK's
/// <c>gtk-enable-animations</c> in <c>settings.ini</c>, and GNOME's <c>enable-animations</c> through
/// <c>gsettings</c> (GNOME keeps it in dconf, which has no text file to read).</item>
/// </list>
/// </remarks>
public static class SystemMotionPreference
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    /// <summary>
    /// True when the OS asks for reduced motion, false when it allows animation, null when it
    /// could not be read.
    /// </summary>
    public static bool? TryGetOsPrefersReducedMotion()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var enabled = 0;
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) ? enabled == 0 : null;
            }

            if (OperatingSystem.IsLinux())
                return TryGetLinuxPreference();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"SystemMotionPreference: could not read the OS animation setting — {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// The reduced-motion value to use: the user's stored choice when there is one, otherwise the OS.
    /// </summary>
    /// <param name="storedReduced">The profile's stored switch value.</param>
    /// <param name="storedIsChoice">True once the user has flipped the switch at least once.</param>
    /// <param name="osPrefersReduced">The OS preference, or null when it could not be read.</param>
    /// <remarks>
    /// A stored TRUE counts as a choice even without <paramref name="storedIsChoice"/>: the switch
    /// defaulted to off before this existed, so a profile holding "on" can only be one where the
    /// user turned it on, and following the OS must not quietly undo that.
    /// </remarks>
    public static bool Resolve(bool storedReduced, bool storedIsChoice, bool? osPrefersReduced) =>
        storedIsChoice || storedReduced ? storedReduced : osPrefersReduced ?? false;

    private static bool? TryGetLinuxPreference()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        var kde = ReadIfExists(Path.Combine(config, "kdeglobals"));
        if (kde is not null && ParseKdeGlobals(kde) is { } fromKde)
            return fromKde;

        foreach (var gtk in new[] { "gtk-4.0", "gtk-3.0" })
        {
            var ini = ReadIfExists(Path.Combine(config, gtk, "settings.ini"));
            if (ini is not null && ParseGtkSettingsIni(ini) is { } fromGtk)
                return fromGtk;
        }

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        return desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) ? TryGetGnomePreference() : null;
    }

    private static string? ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>KDE: <c>AnimationDurationFactor=0</c> under <c>[KDE]</c> means animations are off.</summary>
    internal static bool? ParseKdeGlobals(string text)
    {
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (!string.Equals(section, "KDE", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0 || !string.Equals(line[..eq].Trim(), "AnimationDurationFactor", StringComparison.Ordinal)) continue;

            return double.TryParse(line[(eq + 1)..].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var factor)
                ? factor <= 0
                : null;
        }

        return null;
    }

    /// <summary>GTK: <c>gtk-enable-animations=false</c> (or 0) under <c>[Settings]</c> means off.</summary>
    internal static bool? ParseGtkSettingsIni(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0 || !string.Equals(line[..eq].Trim(), "gtk-enable-animations", StringComparison.Ordinal)) continue;

            return line[(eq + 1)..].Trim().ToLowerInvariant() switch
            {
                "false" or "0" => true,
                "true" or "1" => false,
                _ => null,
            };
        }

        return null;
    }

    private static bool? TryGetGnomePreference()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            // Bounded: this runs once at startup and a hung dconf must not hold the window up.
            if (!process.WaitForExit(1000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { /* already exited */ }
                return null;
            }

            return process.StandardOutput.ReadToEnd().Trim() switch
            {
                "false" => true,
                "true" => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
