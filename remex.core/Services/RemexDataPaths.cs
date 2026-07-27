namespace Remex.Core.Services;

/// <summary>
/// Resolves where RemEx persists host-side state files (pairing registry, launcher list, dashboard
/// layout, file-transfer roots, …).
///
/// <para>
/// On <b>Windows only</b>, state is stored machine-wide under
/// <c>CommonApplicationData</c> (<c>C:\ProgramData\RemEx</c>) instead of per-user
/// <c>LocalApplicationData</c>, so host state is stable across accounts and kept out of a per-user
/// profile. (Only the security-sensitive files — <c>cert.pfx</c> and <c>paired_clients.json</c> — get
/// an explicit restrictive ACL on top; the rest inherit the ordinary ProgramData permissions.)
/// This originally mattered because the host ran as the <b>LocalSystem</b> Windows
/// Service, whose <c>LocalApplicationData</c> resolves to the <c>systemprofile</c> path — state written
/// while the host ran interactively became invisible once it ran as the service (clients appeared
/// unpaired, launchers/layout/roots empty). That service is gone and the host now always runs in the
/// signed-in user's session, but machine-wide storage is still deliberate: it survives a change of
/// signed-in user and keeps security-sensitive state out of a per-user profile.
/// The TLS certificate already lives in <c>CommonApplicationData</c>; co-locating the rest keeps all
/// host state consistent regardless of the account the host happens to run under.
/// </para>
///
/// <para>
/// On <b>Android, Linux, and macOS the behaviour is unchanged</b> — callers pass the per-user folder
/// they already used and it is returned verbatim. Only Windows diverged in a way that orphaned
/// state, so only Windows is relocated.
/// </para>
/// </summary>
public static class RemexDataPaths
{
    private const string WindowsFolderName = "RemEx";
    private const string LegacyWindowsFolderName = "Remex";

    /// <summary>
    /// The machine-wide RemEx data directory on Windows (<c>C:\ProgramData\RemEx</c>). Matches the
    /// folder the TLS certificate is stored in (see <c>CertificateService.GetCertificatePath</c>).
    /// </summary>
    public static string WindowsMachineWideDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        WindowsFolderName);

    /// <summary>
    /// Returns the directory a service should use for its state, relocating only Windows to the
    /// machine-wide location. <paramref name="legacyPerUserDirectory"/> is the per-user folder the
    /// caller used historically and is returned unchanged on non-Windows platforms. The resulting
    /// directory is created if it does not exist.
    /// </summary>
    public static string ResolveDirectory(string legacyPerUserDirectory)
    {
        var dir = OperatingSystem.IsWindows() ? WindowsMachineWideDirectory : legacyPerUserDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Best-effort one-time migration of a single state file from the legacy per-user Windows
    /// location (<c>LocalApplicationData\Remex</c>) to the machine-wide location. No-op off Windows,
    /// when the target already exists, or when the legacy file is absent/unreadable by the current
    /// account (for example a legacy file left in another user's profile — that case requires a
    /// one-time re-pair / re-configure). Returns true when a file was copied.
    /// </summary>
    public static bool TryMigrateWindowsFile(string fileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var targetDir = WindowsMachineWideDirectory;
            var targetPath = Path.Combine(targetDir, fileName);
            if (File.Exists(targetPath))
            {
                return false;
            }

            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyWindowsFolderName,
                fileName);

            if (string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(legacyPath))
            {
                return false;
            }

            Directory.CreateDirectory(targetDir);
            File.Copy(legacyPath, targetPath, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
