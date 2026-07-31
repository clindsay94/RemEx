namespace Remex.Agent.Services.Session;

/// <summary>
/// The opt-in, off-by-default machine setting that allows RemEx to keep the signed-in session
/// usable (unlocked) while a client is connected. Stored as a flag file under
/// <c>ProgramData\RemEx</c>, matching the pairing store's location: machine-wide so it is stable
/// across logins and protected by the same elevated-only ACL as <c>cert.pfx</c> and
/// <c>paired_clients.json</c>, which is where security-sensitive state belongs. Absent or any
/// content other than "1" means disabled.
///
/// The previous comment here justified the location by saying the setting "survives in the
/// Session-0 service" and is "shared with the interactive GUI host". Both describe a design that
/// no longer exists: there is no service and no separate GUI host — remex.agent is a single
/// elevated process in the signed-in user's session that is both host and UI (RemEx-aep). The
/// storage choice is still correct; only the reason was stale (RemEx-hn5k).
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
