using System;
using System.IO;

namespace Remex.Host.Services.Session;

/// <summary>
/// The opt-in, off-by-default machine setting that allows RemEx to keep the signed-in session
/// usable (unlocked) while a client is connected. Stored as a flag file under
/// <c>ProgramData\RemEx</c> — machine-wide so it survives in the Session-0 service and is shared
/// with the interactive GUI host (which writes it from the settings UI) — matching the pairing
/// store's location. Absent or any content other than "1" means disabled.
/// </summary>
public static class SessionGuardSettings
{
    private static string FlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemEx",
        "keep-session-unlocked.flag");

    /// <summary>True only when the user has explicitly enabled the feature; false on any error.</summary>
    public static bool IsKeepUnlockedEnabled()
    {
        try
        {
            return File.Exists(FlagPath) && File.ReadAllText(FlagPath).Trim() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Persists the opt-in flag from the interactive GUI host's settings UI. Writes "1" to enable or
    /// "0" to disable, creating <c>ProgramData\RemEx</c> if needed. Returns false on any I/O error
    /// (e.g. insufficient rights) so the UI can surface the failure rather than silently lying.
    /// </summary>
    public static bool SetKeepUnlockedEnabled(bool enabled)
    {
        try
        {
            var dir = Path.GetDirectoryName(FlagPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FlagPath, enabled ? "1" : "0");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
