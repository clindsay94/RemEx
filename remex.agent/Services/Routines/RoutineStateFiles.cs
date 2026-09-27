using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Remex.Agent.Services.Security;
using Remex.Core.Services;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// Where the host's routine state files live and how they are written (routines spec §6.9).
/// </summary>
/// <remarks>
/// A seam so <c>RoutineSyncRevisionTests</c> can make a write fail and prove the old set survives, and so
/// the corrupt-file tests can plant bytes without touching the machine-wide directory.
/// </remarks>
public interface IRoutineStateFiles
{
    /// <summary>The file's text, or null when it does not exist. Throws when it exists and cannot be read.</summary>
    string? Read(string fileName);

    /// <summary>
    /// Writes <paramref name="contents"/> atomically and restricts its permissions. Throws on failure: a
    /// caller must never report a save that did not happen (docs/REGRESSION-GUARDS.md, atomic saves).
    /// </summary>
    Task WriteAsync(string fileName, string contents);

    /// <summary>
    /// Moves an unreadable file aside as <c>&lt;name&gt;.unreadable-&lt;utc&gt;</c> so it is never overwritten by
    /// an empty document (§6.7). Returns the new name, or null when nothing was moved.
    /// </summary>
    string? Quarantine(string fileName, DateTimeOffset now);

    /// <summary>Removes staging files a killed process left behind.</summary>
    void SweepStagingOrphans(string fileName);
}

/// <summary>
/// <see cref="IRoutineStateFiles"/> over the host state directory: <c>C:\ProgramData\RemEx</c> on Windows,
/// <c>~/.local/share/Remex</c> on Linux, or the test override.
/// </summary>
/// <remarks>
/// <para>
/// <b>ATOMIC, THEN LOCKED DOWN (T14, R-SEC-12).</b> Every write goes through
/// <see cref="RemexDataPaths.WriteAllTextAtomicAsync"/> and then <see cref="RoutineFilePermissions.Restrict"/>.
/// <c>remex.agent</c> is elevated on Windows, so a routine is a way to make the elevated process act: a
/// medium-integrity process of the same user must not be able to plant one.
/// </para>
/// </remarks>
public sealed class RoutineStateFiles : IRoutineStateFiles
{
    private readonly string _directory;
    private readonly ILogger _logger;

    public RoutineStateFiles(ILogger<RoutineStateFiles> logger)
        : this(RemexDataPaths.ResolveDirectory(RemexDataPaths.PerUserDirectory), logger)
    {
    }

    /// <summary>Test seam: a specific directory.</summary>
    public RoutineStateFiles(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>The directory the files live in.</summary>
    public string Directory => _directory;

    /// <inheritdoc />
    public string? Read(string fileName)
    {
        var path = PathOf(fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string fileName, string contents)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var path = PathOf(fileName);
        await RemexDataPaths.WriteAllTextAtomicAsync(path, contents);
        RoutineFilePermissions.Restrict(path, _logger);
    }

    /// <inheritdoc />
    public string? Quarantine(string fileName, DateTimeOffset now)
    {
        var path = PathOf(fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var aside = $"{fileName}.unreadable-{now.UtcDateTime:yyyyMMddTHHmmssZ}";
        try
        {
            File.Move(path, PathOf(aside), overwrite: true);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not set the unreadable {File} aside.", fileName);
            return null;
        }
    }

    /// <inheritdoc />
    public void SweepStagingOrphans(string fileName) => RemexDataPaths.SweepStagingOrphans(PathOf(fileName));

    private string PathOf(string fileName) => Path.Combine(_directory, fileName);
}

/// <summary>The permission step for the routine state files (§6.9, T14).</summary>
public static class RoutineFilePermissions
{
    /// <summary>
    /// Windows: LocalSystem + Administrators only, inheritance off. Linux: <c>0600</c>. Best effort, logged:
    /// the file is already written, and the write itself is what the caller reports on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NO ACE FOR THE SIGNED-IN USER, UNLIKE <c>paired_clients.json</c>.</b> That store only has to keep
    /// secrets from OTHER accounts; this one has to keep a same-user, medium-integrity process from writing a
    /// routine the elevated agent will then execute. Under UAC such a process carries Administrators as a
    /// deny-only SID, so an ACL of LocalSystem + Administrators is exactly the elevated agent. The owner is
    /// set to Administrators too: an owner holds implicit WRITE_DAC, and a user-owned file could simply have
    /// its ACL rewritten by that same medium process.
    /// </para>
    /// <para>
    /// <b>A NON-ELEVATED RUN FALLS BACK TO THE PAIRING-STORE ACL.</b> A developer build started without
    /// elevation could otherwise lock itself out of its own file on the first save, and every later read
    /// would find it "unreadable". Production on Windows is always elevated (AGENTS.md).
    /// </para>
    /// </remarks>
    public static void Restrict(string path, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Environment.IsPrivilegedProcess)
                {
                    try
                    {
                        new FileInfo(path).SetAccessControl(BuildWindowsSecurity(ownerIsAdministrators: true));
                    }
                    catch (InvalidOperationException)
                    {
                        // "Not allowed to be the owner": keep the current owner, still drop every other ACE.
                        new FileInfo(path).SetAccessControl(BuildWindowsSecurity(ownerIsAdministrators: false));
                    }
                }
                else
                {
                    PairedClientRegistry.RestrictStorePermissions(path, logger);
                }
            }
            else if (!OperatingSystem.IsAndroid())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not restrict the permissions of a routine state file.");
        }
    }

    /// <summary>
    /// The Windows security descriptor for a routine state file: protected (no inheritance), full control for
    /// LocalSystem and Administrators and nobody else. Exposed for <c>RoutineStoreAclTests</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static FileSecurity BuildWindowsSecurity(bool ownerIsAdministrators)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (ownerIsAdministrators)
        {
            security.SetOwner(administrators);
        }

        security.AddAccessRule(new FileSystemAccessRule(localSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
