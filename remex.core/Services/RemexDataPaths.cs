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

    /// <summary>
    /// The per-user RemEx folder, under <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// on every platform. It is both the source of the one-time Windows migration in
    /// <see cref="TryMigrateWindowsFile"/> — the "legacy" location, for the host state that moved to
    /// ProgramData — and the current, correct home of the client-side stores resolved by
    /// <see cref="PerUserDirectory"/>. One folder, two roles; hence one constant.
    /// </summary>
    private const string PerUserFolderName = "Remex";

    /// <summary>
    /// <see cref="AppContext"/> key holding an absolute directory that replaces every host-state
    /// directory this class resolves, on every platform. Unset in production; the test assemblies set
    /// it to a per-run temp directory from a module initializer (<c>build/TestHostStateRedirect.cs</c>),
    /// before any test code runs.
    ///
    /// <para>
    /// WHY THIS EXISTS (RemEx-4u29). Without it the suite writes into the machine-wide store the host
    /// itself uses: a test's fixture identity became a real entry in
    /// <c>C:\ProgramData\RemEx\paired_clients.json</c>, and a fixture grant became a real
    /// <c>fullBrowseGranted</c> record in <c>file_transfer_trust.json</c>. Both files are security
    /// state — a pairing entry is a credential record and a full-browse grant is standing
    /// authorisation to read the PC's filesystem — so a test fixture must not be able to create
    /// either, and after a test run you could no longer tell a real pairing from a fixture by
    /// inspection. Seven fixture identities were found on the developer's machine when this was
    /// filed; six remained when it was fixed, because a later test run deleted one — the same suite,
    /// still editing the same live credential store, which is the point.
    /// </para>
    ///
    /// <para>
    /// AN <see cref="AppContext"/> KEY RATHER THAN AN ENVIRONMENT VARIABLE, deliberately. These paths
    /// decide where pairing credentials and browse grants are read from, so the redirect should be as
    /// hard to set from outside as the binary is to replace. An environment variable is inherited by
    /// anything the host launches and can be set by anything that can start it. This key needs either
    /// code already running in the process, or write access to <c>Remex.Agent.runtimeconfig.json</c>
    /// beside the executable — the .NET host seeds AppContext from its <c>configProperties</c>, so
    /// the guarantee is NOT "in-process only", and review corrected a first draft that said it was.
    /// On a Program Files install that write access is admin-equivalent, which is the same access
    /// needed to swap the binary outright, so the choice stands on the comparison rather than on an
    /// absolute.
    /// </para>
    /// </summary>
    public const string HostStateDirectoryOverrideKey = "Remex.HostStateDirectoryOverride";

    /// <summary>
    /// The directory named by <see cref="HostStateDirectoryOverrideKey"/>, or <c>null</c> when unset
    /// — which is always, in production. Blank or non-string values read as unset so a half-applied
    /// override cannot silently redirect state to the process working directory.
    /// </summary>
    public static string? HostStateDirectoryOverride
        => AppContext.GetData(HostStateDirectoryOverrideKey) is string dir
            && !string.IsNullOrWhiteSpace(dir)
                ? dir
                : null;

    /// <summary>
    /// The machine-wide RemEx data directory on Windows (<c>C:\ProgramData\RemEx</c>). Matches the
    /// folder the TLS certificate is stored in (see <c>CertificateService.GetCertificatePath</c>).
    /// </summary>
    public static string WindowsMachineWideDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        WindowsFolderName);

    /// <summary>
    /// The per-user RemEx directory — <c>%LOCALAPPDATA%\Remex</c> on Windows,
    /// <c>~/.local/share/Remex</c> elsewhere — or the directory named by
    /// <see cref="HostStateDirectoryOverrideKey"/> when it is set.
    ///
    /// <para>
    /// FOR THE STORES THAT ARE GENUINELY PER-USER (RemEx-ln0k), which is why this is not
    /// <see cref="ResolveDirectory"/>. That method relocates Windows to
    /// <see cref="WindowsMachineWideDirectory"/> because the state it resolves belongs to the host
    /// and has to survive a change of signed-in user. These do not: <c>pinned_hosts.json</c> is the
    /// CLIENT's record of which host certificates this user has trusted, and
    /// <c>dashboard_layout.json</c> / <c>recent_activity.json</c> are that user's own layout and
    /// history. Putting them in ProgramData would share one account's pinned-certificate trust with
    /// every other account on the PC, and would silently orphan every existing layout. So the
    /// production path is deliberately left exactly where it was; the only thing this adds is that
    /// the override wins.
    /// </para>
    ///
    /// <para>
    /// It does NOT create the directory, unlike <see cref="ResolveDirectory"/>. Callers here differ
    /// on when the folder should appear — <c>PinnedCertStore</c> creates it lazily at first write,
    /// and <c>PathSettings</c> resolves its defaults in a property initializer, where creating three
    /// directories as a side effect of binding configuration would be a surprise. Resolving a path
    /// and making a folder are separate decisions and this only makes the first one.
    /// </para>
    /// </summary>
    public static string PerUserDirectory => HostStateDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        PerUserFolderName);

    /// <summary>
    /// Returns the directory a service should use for its state, relocating only Windows to the
    /// machine-wide location. <paramref name="legacyPerUserDirectory"/> is the per-user folder the
    /// caller used historically and is returned unchanged on non-Windows platforms. The resulting
    /// directory is created if it does not exist.
    ///
    /// <para>
    /// <see cref="HostStateDirectoryOverrideKey"/> wins over both, on every platform — the redirect
    /// has to cover Linux and macOS too, where these stores live under the per-user
    /// <c>LocalApplicationData\Remex</c> rather than in ProgramData.
    /// </para>
    /// </summary>
    public static string ResolveDirectory(string legacyPerUserDirectory)
    {
        var dir = HostStateDirectoryOverride
            ?? (OperatingSystem.IsWindows() ? WindowsMachineWideDirectory : legacyPerUserDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Best-effort one-time migration of a single state file from the legacy per-user Windows
    /// location (<c>LocalApplicationData\Remex</c>) to the machine-wide location. No-op off Windows,
    /// when the target already exists, or when the legacy file is absent/unreadable by the current
    /// account (for example a legacy file left in another user's profile — that case requires a
    /// one-time re-pair / re-configure). Returns true when a file was copied.
    ///
    /// <para>
    /// Also a no-op while <see cref="HostStateDirectoryOverrideKey"/> is set. The migration reads the
    /// real per-user profile, so leaving it enabled under the override would copy the developer's
    /// live state into the test directory — the redirect would stop tests writing real state while
    /// still letting them read it, and a test asserting "no clients are paired" would start failing
    /// on whichever machine had a pairing.
    /// </para>
    /// </summary>
    public static bool TryMigrateWindowsFile(string fileName)
    {
        if (!OperatingSystem.IsWindows() || HostStateDirectoryOverride is not null)
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
                PerUserFolderName,
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

    /// <summary>
    /// Writes <paramref name="contents"/> over <paramref name="path"/> by way of a uniquely-named
    /// sibling temporary file, so a reader never observes a half-written store.
    ///
    /// <para>
    /// THE UNIQUE NAME IS THE POINT (RemEx-kow1). Every host-state store here used to stage through
    /// the fixed path <c>&lt;store&gt;.tmp</c>, which is shared by every writer of that store rather
    /// than owned by one: two of them staging at once means one truncates the file the other is
    /// mid-write, and whichever moves second publishes the loser's bytes under the winner's name.
    /// The stores this matters for are <c>paired_clients.json</c> and
    /// <c>file_transfer_trust.json</c> — a corrupted pairing registry unpairs every device and a
    /// corrupted trust store loses or mis-states standing filesystem grants. Concurrent writers are
    /// not hypothetical: a test run and the installed agent share the machine-wide directory, and
    /// within one test assembly every host shares one redirected directory. A GUID per write gives
    /// each writer its own staging file; the move stays the single atomic step it always was.
    /// <see cref="Remex.Core.Services.RemexDataPaths"/> is where it lives because all four callers
    /// resolve their directory through this class already.
    /// </para>
    ///
    /// <para>
    /// WHAT THIS DOES NOT DO, because the first draft claimed it and a test disproved it: it does not
    /// make concurrent writers all succeed. Two renames onto one target still collide, and on Windows
    /// the loser gets <c>UnauthorizedAccessException</c> out of <c>File.Move</c> — measured, not
    /// assumed. That is no worse than the fixed-path code it replaces (there the losing writer failed
    /// at the write instead) and three of the four callers already catch it. The property gained is
    /// integrity, not availability: whatever is on disk is always exactly one writer's complete
    /// contents.
    /// </para>
    ///
    /// <para>
    /// Callers keep their own exception handling: this throws whatever the write or the move throws,
    /// having first removed the staging file so a failure cannot litter the store directory with
    /// per-attempt garbage that nothing would ever collect. The NAMING (leading dot, GUID, sibling
    /// directory) matches <c>CertificateService.WriteProtectedFile</c>, the one writer of this state
    /// that already did it correctly — but only the naming. That method also applies its restrictive
    /// ACL at creation, before any bytes exist, and this does not: the staging file here inherits the
    /// store directory's permissions and hardening still happens after the rename, exactly as it did
    /// when the staging path was fixed. The window is unchanged in kind and size; it is simply not
    /// what this method set out to close.
    /// </para>
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        // Sibling of the target so File.Move is a same-volume rename rather than a copy.
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteStagingFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Removes a staging file left by a failed <see cref="WriteAllTextAtomic"/>. Best-effort by
    /// design: the caller is already propagating the real failure, and masking it with a cleanup
    /// exception would report the wrong problem.
    /// </summary>
    private static void TryDeleteStagingFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
