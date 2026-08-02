using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Remex.Core.Validation;

namespace Remex.Agent.Services.FileTransfer;

public sealed class FileTransferService : IFileTransferService
{
    private const long MaxUploadBytes = 5_000_000_000L;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<FileTransferService> _logger;
    private readonly string _configPath;
    private readonly ThumbnailService _thumbnailService;

    private sealed record ConfiguredRoot
    {
        public required string RootId { get; init; }
        public required string DisplayName { get; init; }
        public required string AbsolutePath { get; init; }
        public bool IsWritable { get; init; }
        public bool CanRename { get; init; }
        public bool CanMove { get; init; }
        public bool CanDelete { get; init; }
        public bool CanRemoveRoot { get; init; }
    }

    public FileTransferService(ILogger<FileTransferService> logger)
    {
        _logger = logger;
        // Host-only store. Relocated to machine-wide ProgramData on Windows (unchanged elsewhere)
        // so configured shared roots survive a change of signed-in user — originally, the host
        // running as the LocalSystem service.
        var legacyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
        var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
        RemexDataPaths.TryMigrateWindowsFile("file_transfer_roots.json");
        _configPath = Path.Combine(baseFolder, "file_transfer_roots.json");
        _thumbnailService = new ThumbnailService(logger);
    }

    /// <summary>
    /// Test-only seam (visible to <c>Remex.Agent.Tests</c> via <c>InternalsVisibleTo</c>). Points the
    /// roots config at an explicit path so unit tests can drive copy/move/mkdir/search/metadata against a
    /// temp directory without touching the machine-wide ProgramData store. Not used by DI — the default
    /// container only binds public constructors, so this overload is invisible to host bootstrapping.
    /// </summary>
    internal FileTransferService(ILogger<FileTransferService> logger, string configFilePath)
    {
        _logger = logger;
        _configPath = configFilePath;
        _thumbnailService = new ThumbnailService(logger);
    }

    /// <summary>
    /// Test-only seam: writes the given roots directly to the config file, bypassing default-root creation
    /// (which would otherwise create folders in the real user profile). Each tuple maps to a configured
    /// shared root with the supplied permission flags.
    /// </summary>
    internal void SeedRootsForTests(
        params (string rootId, string displayName, string absolutePath,
                bool isWritable, bool canRename, bool canMove, bool canDelete, bool canRemoveRoot)[] roots)
    {
        var configured = roots
            .Select(r => new ConfiguredRoot
            {
                RootId = r.rootId,
                DisplayName = r.displayName,
                AbsolutePath = Path.GetFullPath(r.absolutePath),
                IsWritable = r.isWritable,
                CanRename = r.canRename,
                CanMove = r.canMove,
                CanDelete = r.canDelete,
                CanRemoveRoot = r.canRemoveRoot,
            })
            .ToList();
        SaveConfiguredRoots(configured);
    }

    public Task<IReadOnlyList<FileSharedRoot>> ListRootsAsync(CancellationToken ct)
        => Task.FromResult(MapToSharedRoots(LoadConfiguredRoots()));

    public Task<IReadOnlyList<FileEntry>> BrowseAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var dir = new DirectoryInfo(ResolvePath(rootId, relativePath));
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Directory not found in shared root '{rootId}': {relativePath}");
        return Task.FromResult(EnumerateDirectory(dir));
    }

    // Full-device browse of a mounted volume (RemEx-hb1t). The caller (FileTransferHandler) has already
    // verified this client holds a full-browse consent grant and that volumeAbsolutePath is a real
    // enumerated volume; here we enforce path safety only — ResolveWithinRoot collapses '..' and bounds
    // the resolved path within the volume root (plus the restricted-system-path denylist), so the client
    // cannot escape the volume. Listing is read-only; full browse never exposes write/delete.
    public Task<IReadOnlyList<FileEntry>> BrowseVolumeAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct)
    {
        var resolved = FilePathValidation.ResolveWithinRoot(volumeAbsolutePath, relativePath, volumeAbsolutePath);
        var dir = new DirectoryInfo(resolved);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Folder not found: '{relativePath}'.");
        return Task.FromResult(EnumerateDirectory(dir));
    }

    // Write-op parity for full-device browsing (RemEx-hb1t.3): the SAME physical folder must behave the
    // same whether it was reached via its pinned rootId or via a volume browse. SECURITY: the relative
    // path is resolved within the volume FIRST (collapses '..', restricted-path denylist) — raw client
    // input is never prefix-compared. Deepest containing root wins so the most specific permission set
    // applies. Comparison is ordinal-insensitive on Windows, ordinal on Linux (cross-platform parity).
    public Task<(string RootId, string RelativePath)?> TryMapVolumePathToConfiguredRootAsync(
        string volumeAbsolutePath, string relativePath, CancellationToken ct)
    {
        var resolved = FilePathValidation.ResolveWithinRoot(volumeAbsolutePath, relativePath, volumeAbsolutePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var root in LoadConfiguredRoots()
                     .OrderByDescending(r => Path.GetFullPath(r.AbsolutePath).Length))
        {
            var rootPath = Path.GetFullPath(root.AbsolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (resolved.Equals(rootPath, comparison))
                return Task.FromResult<(string, string)?>((root.RootId, string.Empty));

            var rootWithSep = rootPath + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(rootWithSep, comparison))
            {
                var rebased = resolved[rootWithSep.Length..].Replace('\\', '/');
                return Task.FromResult<(string, string)?>((root.RootId, rebased));
            }
        }

        return Task.FromResult<(string, string)?>(null);
    }

    // Shared, resilient directory listing. Entries whose metadata can't be read (locked/protected system
    // files, common when browsing a full volume) are skipped rather than failing the whole listing; an
    // unreadable *directory* still surfaces as an error to the caller.
    private static IReadOnlyList<FileEntry> EnumerateDirectory(DirectoryInfo dir)
    {
        var entries = new List<FileEntry>();
        foreach (var fsi in dir.EnumerateFileSystemInfos())
        {
            try
            {
                entries.Add(new FileEntry
                {
                    Name = fsi.Name,
                    IsDirectory = fsi is DirectoryInfo,
                    SizeBytes = fsi is FileInfo fi ? fi.Length : 0,
                    ModifiedUnixMs = new DateTimeOffset(fsi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Skip an entry whose metadata is unreadable; listing the rest beats failing the browse.
            }
        }
        entries.Sort(static (a, b) =>
        {
            var byDir = b.IsDirectory.CompareTo(a.IsDirectory);
            return byDir != 0 ? byDir : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return entries;
    }

    public Task<Stream> OpenForReadAsync(string rootId, string relativePath, CancellationToken ct)
        => OpenForReadCore(ResolvePath(rootId, relativePath), rootId, relativePath);

    // Full-device read of a mounted volume (RemEx-39jw). Same consent/genuine-volume contract as
    // BrowseVolumeAsync: the caller (FileTransferHandler) has already verified full-browse consent and that
    // volumeAbsolutePath is a real enumerated volume. Read-only — there is deliberately no OpenVolumeForWriteAsync.
    public Task<Stream> OpenVolumeForReadAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct)
    {
        var resolved = FilePathValidation.ResolveWithinRoot(volumeAbsolutePath, relativePath, volumeAbsolutePath);
        return OpenForReadCore(resolved, volumeAbsolutePath, relativePath);
    }

    private static Task<Stream> OpenForReadCore(string resolved, string rootDisplay, string relativePath)
    {
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found in shared root '{rootDisplay}': {relativePath}");
        return Task.FromResult<Stream>(new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true));
    }

    public Task<Stream> OpenForWriteAsync(string rootId, string relativePath, long expectedBytes, CancellationToken ct)
        => Task.FromResult<Stream>(new FileStream(
            ResolveForWrite(rootId, relativePath, expectedBytes),
            FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true));

    /// <summary>
    /// Applies every write-side check and returns the absolute destination path, creating the parent
    /// directory if needed.
    /// </summary>
    /// <remarks>
    /// Extracted so that opening a stream and promoting a staged file cannot drift apart. These four
    /// checks — size cap, root writability, root-escape safety via <c>ResolvePath</c>, and parent
    /// creation — ARE the write authorization for this service, so a second caller that needed a path
    /// rather than a stream had to reuse them rather than reimplement them (RemEx-fq6f).
    /// </remarks>
    private string ResolveForWrite(string rootId, string relativePath, long expectedBytes)
    {
        if (expectedBytes > MaxUploadBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), $"File too large ({expectedBytes} bytes). Max is {MaxUploadBytes}.");

        var root = GetConfiguredRoot(rootId);
        if (!root.IsWritable)
            throw new UnauthorizedAccessException($"Shared root '{root.DisplayName}' is read-only.");

        var resolved = ResolvePath(rootId, relativePath);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return resolved;
    }

    public async Task PromoteStagedFileAsync(
        string rootId, string relativePath, long expectedBytes, string stagingPath, CancellationToken ct)
    {
        var destination = ResolveForWrite(rootId, relativePath, expectedBytes);

        // Same volume: a rename. This is the whole point of the bead — the previous code re-read the
        // staged file and streamed it to the destination, so a 5 GB push cost 10 GB of writes plus a
        // 5 GB read to move bytes that were already on the right disk.
        //
        // On this branch it is also SAFER, not just faster, for two separate reasons. First, the copy
        // opened the destination with FileMode.Create, which truncates before the first byte arrives,
        // so a failure midway left the user's pre-existing file destroyed and replaced by a fragment;
        // a true rename replaces atomically. Second, ResolveWithinRoot is purely lexical and does not
        // resolve reparse points, so FileMode.Create would happily write THROUGH a symlink or junction
        // planted in the shared root to a target outside it — MoveFileEx and rename() replace the link
        // itself. (Atomicity is claimed only here: see the fallback below.)
        if (AreSameVolume(stagingPath, destination))
        {
            File.Move(stagingPath, destination, overwrite: true);
            RestoreInheritedAcl(destination);
            return;
        }

        // Different volume: bytes genuinely have to cross, so stream them. File.Move would fall back to
        // an internal copy here anyway (MOVEFILE_COPY_ALLOWED on Windows, an EXDEV fallback on Unix),
        // but that copy is synchronous, uncancellable and NOT atomic — this keeps the await points and
        // honours ct on a transfer that can legitimately run for minutes. A file created here inherits
        // the destination's ACL normally, so no fixup is needed on this path.
        await using var src = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.None, 65536, useAsync: true);
        await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await src.CopyToAsync(dst, 65536, ct);
        await dst.FlushAsync(ct);
    }

    public Task DeleteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.CanDelete)
            throw new UnauthorizedAccessException($"Deletions are not permitted in '{root.DisplayName}'.");

        var resolved = ResolvePath(rootId, relativePath);

        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
        else if (File.Exists(resolved))
            File.Delete(resolved);
        else
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");

        return Task.CompletedTask;
    }

    public Task RenameAsync(string rootId, string relativePath, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("New name is invalid.");

        var root = GetConfiguredRoot(rootId);
        if (!root.CanRename)
            throw new UnauthorizedAccessException($"Renames are not permitted in '{root.DisplayName}'.");

        var resolved = ResolvePath(rootId, relativePath);
        var parentDir = Path.GetDirectoryName(resolved)
            ?? throw new InvalidOperationException("Cannot rename a root path.");
        var destination = Path.Combine(parentDir, newName);

        if (Directory.Exists(resolved))
            Directory.Move(resolved, destination);
        else if (File.Exists(resolved))
            File.Move(resolved, destination, overwrite: false);
        else
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");

        return Task.CompletedTask;
    }

    public Task<string> ComputeSha256Async(string rootId, string relativePath, CancellationToken ct)
        => ComputeSha256Core(ResolvePath(rootId, relativePath), rootId, relativePath, ct);

    /// <summary>Volume-mode counterpart of <see cref="ComputeSha256Async"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    public Task<string> ComputeVolumeSha256Async(string volumeAbsolutePath, string relativePath, CancellationToken ct)
    {
        var resolved = FilePathValidation.ResolveWithinRoot(volumeAbsolutePath, relativePath, volumeAbsolutePath);
        return ComputeSha256Core(resolved, volumeAbsolutePath, relativePath, ct);
    }

    private static async Task<string> ComputeSha256Core(string resolved, string rootDisplay, string relativePath, CancellationToken ct)
    {
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found in shared root '{rootDisplay}': {relativePath}");

        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToBase64String(hash);
    }

    public Task<IReadOnlyList<FileSharedRoot>> AddRootFromPathAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct)
    {
        // Resolve the SOURCE (parent) root up front: a root derived from an existing shared root MUST
        // inherit that parent's permission flags rather than being granted full read/write/delete. The
        // old code hardcoded IsWritable/CanRename/CanMove/CanDelete = true, so a paired client could
        // browse a read-only default root (Documents/Desktop/Pictures/Downloads), pick a subfolder, and
        // re-pin it as a fully writable/deletable root — silently defeating the read-only designation and
        // gaining overwrite/delete over the user's files there (VULN-4, RemEx-s032.4). Inheriting the
        // parent flags keeps a read-only subtree read-only; capabilities may narrow along the derivation
        // chain but never widen.
        var parent = GetConfiguredRoot(sourceRootId);

        var absolutePath = ResolvePath(sourceRootId, sourceRelativePath);
        if (!Directory.Exists(absolutePath))
            throw new DirectoryNotFoundException($"Directory does not exist: {absolutePath}");

        var roots = LoadConfiguredRoots().ToList();
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (roots.Any(r => r.AbsolutePath.Equals(absolutePath, pathComparison)))
            throw new InvalidOperationException("This folder is already a shared root.");

        var displayName = Path.GetFileName(absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rootId = $"custom_{Guid.NewGuid():N}";

        roots.Add(new ConfiguredRoot
        {
            RootId = rootId,
            DisplayName = displayName,
            AbsolutePath = absolutePath,
            // Inherit the parent root's capability flags — never widen them. A writable parent yields a
            // writable derived root; a read-only parent yields a read-only derived root.
            IsWritable = parent.IsWritable,
            CanRename = parent.CanRename,
            CanMove = parent.CanMove,
            CanDelete = parent.CanDelete,
            // CanRemoveRoot is a UI/management flag (the user may un-share what they chose to share), not a
            // filesystem-write capability, so a derived root is always individually removable. This grants
            // no access to file contents and is safe to keep true even for a read-only subtree.
            CanRemoveRoot = true,
        });

        SaveConfiguredRoots(roots);
        return Task.FromResult<IReadOnlyList<FileSharedRoot>>(MapToSharedRoots(roots));
    }

    public Task<IReadOnlyList<FileSharedRoot>> RemoveRootAsync(string rootId, CancellationToken ct)
    {
        var roots = LoadConfiguredRoots().ToList();
        var target = roots.FirstOrDefault(r => r.RootId == rootId)
            ?? throw new InvalidOperationException($"Shared root '{rootId}' not found.");

        if (!target.CanRemoveRoot)
            throw new UnauthorizedAccessException($"The root '{target.DisplayName}' cannot be removed.");

        roots.RemoveAll(r => r.RootId == rootId);
        SaveConfiguredRoots(roots);
        return Task.FromResult<IReadOnlyList<FileSharedRoot>>(MapToSharedRoots(roots));
    }

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP3: file-manager ops ──
    // Copy / move / mkdir / search / metadata / thumbnail. All paths are resolved through the centralized
    // FilePathValidation helper (plan §2) so the root-escape + Linux system denylist rules are identical to
    // browse/transfer. Permission flags: copy/mkdir require IsWritable; move additionally requires the
    // (previously unwired) CanMove flag.

    /// <summary>
    /// Copies within a root, honouring a client's answer to a filename collision (RemEx-6vd8).
    /// </summary>
    /// <returns>The name actually used, when "keep both" renamed it; otherwise null.</returns>
    public Task<string?> CopyAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct, string? conflictResolution = null)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.IsWritable)
            throw new UnauthorizedAccessException($"Copies are not permitted in '{root.DisplayName}' (read-only).");

        var source = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
        var destination = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, destinationRelativePath, root.DisplayName);

        if (PathsEqual(source, destination))
            throw new IOException("Source and destination are the same.");

        EnsureParentDirectory(destination);

        // RESOLVED AFTER the destination has been confined to the root, never before - doing it on
        // the raw request would hand path composition back to the untrusted side.
        //
        // The resolver RE-CHECKS containment rather than inheriting it, and review proved that is not
        // redundant: ResolveWithinRoot maps "/" and "." to the root ITSELF, whose parent is outside
        // the share, so a sibling rename there escaped entirely.
        var plan = ConflictResolver.Resolve(
            conflictResolution,
            destination,
            root.AbsolutePath,
            overwrite,
            ListDirectoryNames,
            ConflictResolver.HostFileSystemIsCaseSensitive);

        destination = plan.DestinationPath;
        overwrite = plan.Overwrite;

        if (File.Exists(source))
        {
            if (Directory.Exists(destination))
                throw Occupied(plan, FileConflictException.DifferentKindExists);
            if (File.Exists(destination) && !overwrite)
                throw Occupied(plan, FileConflictException.FileExists);
            RunRenamedCreate(plan, () => File.Copy(source, destination, overwrite));
        }
        else if (Directory.Exists(source))
        {
            if (IsDestinationInsideSource(source, destination))
                throw new IOException("Cannot copy a folder into itself.");
            if (Directory.Exists(destination) && !overwrite)
                throw Occupied(plan, FileConflictException.DirectoryExists);

            // A FILE STANDING WHERE A FOLDER IS GOING — the last collision path that carried no
            // code. Review found it falling through to CopyDirectoryRecursive, which surfaces a raw
            // OS IOException the client cannot branch on, so the sheet never opened for it.
            if (File.Exists(destination))
                throw Occupied(plan, FileConflictException.DifferentKindExists);

            RunRenamedCreate(plan, () => CopyDirectoryRecursive(source, destination, overwrite, ct));
        }
        else
        {
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");
        }

        return Task.FromResult(plan.ResolvedName);
    }

    /// <summary>
    /// Moves within a root, honouring a client's answer to a filename collision (RemEx-6vd8).
    /// </summary>
    /// <returns>The name actually used, when "keep both" renamed it; otherwise null.</returns>
    public Task<string?> MoveAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct, string? conflictResolution = null)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.CanMove)
            throw new UnauthorizedAccessException($"Moves are not permitted in '{root.DisplayName}'.");

        var source = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
        var destination = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, destinationRelativePath, root.DisplayName);

        if (PathsEqual(source, destination))
            throw new IOException("Source and destination are the same.");

        EnsureParentDirectory(destination);

        // RESOLVED AFTER the destination has been confined to the root, never before - doing it on
        // the raw request would hand path composition back to the untrusted side.
        //
        // The resolver RE-CHECKS containment rather than inheriting it, and review proved that is not
        // redundant: ResolveWithinRoot maps "/" and "." to the root ITSELF, whose parent is outside
        // the share, so a sibling rename there escaped entirely.
        var plan = ConflictResolver.Resolve(
            conflictResolution,
            destination,
            root.AbsolutePath,
            overwrite,
            ListDirectoryNames,
            ConflictResolver.HostFileSystemIsCaseSensitive);

        destination = plan.DestinationPath;
        overwrite = plan.Overwrite;

        if (File.Exists(source))
        {
            if (Directory.Exists(destination))
                throw Occupied(plan, FileConflictException.DifferentKindExists);
            if (File.Exists(destination))
            {
                if (!overwrite)
                    throw Occupied(plan, FileConflictException.FileExists);
                File.Delete(destination);
            }

            // PROBED LIKE COPY, and move is the branch that needed it more. Copy gained this in
            // RemEx-cirk while move kept composing a name it never checked, so the identical request
            // produced a coded, answerable refusal one way and a raw OS error the other - "the
            // filename, directory name, or volume label syntax is incorrect" - which the client
            // cannot branch on, so no sheet opened at all.
            //
            // ORDER IS THE SAFETY PROPERTY HERE. RunRenamedCreate throws BEFORE it runs the
            // operation, so a name that turns out to be unusable or already taken leaves the source
            // exactly where it was. A move that failed after deleting its source would be the one
            // unrecoverable outcome in this file THAT NOBODY ASKED FOR - DeleteAsync and an
            // overwriting move are equally irreversible, but the user requested those.
            RunRenamedCreate(plan, () =>
            {
                if (AreSameVolume(source, destination))
                {
                    File.Move(source, destination);
                }
                else
                {
                    // Cross-volume move: File.Move can throw across devices, so realize it as
                    // copy+delete. The source delete stays INSIDE the probed operation, so it is
                    // reached only once the copy has actually succeeded.
                    //
                    // PASSING overwrite RATHER THAN A LITERAL true, which review caught as the last
                    // path where keep-both could still destroy something. Under keep-both overwrite
                    // is always false, so if the invented name gets claimed between the probe
                    // releasing it and this line, the copy REFUSES - which is what the same-drive
                    // path and CopyAsync already do. A hardcoded true overwrote it instead. On the
                    // plain-overwrite path the value is true anyway and the destination was already
                    // removed above, so nothing else changes.
                    File.Copy(source, destination, overwrite);
                    File.Delete(source);
                }
            });
        }
        else if (Directory.Exists(source))
        {
            if (IsDestinationInsideSource(source, destination))
                throw new IOException("Cannot move a folder into itself.");
            if (Directory.Exists(destination))
            {
                if (!overwrite)
                    throw Occupied(plan, FileConflictException.DirectoryExists);
                Directory.Delete(destination, recursive: true);
            }
            else if (File.Exists(destination))
            {
                // A FILE STANDING WHERE A FOLDER IS GOING, REFUSED UNCONDITIONALLY — the overwrite
                // flag does not reach this. Review caught two things here: it reported the plain
                // collision code, and it honoured overwrite by DELETING the user's file to make room
                // for a directory.
                //
                // Copy already refused this outright; move did not, so the same request destroyed a
                // file on one path and was rejected on the other. The bead's own principle is that
                // the HOST decides rather than trusting the client to withhold a button, and this is
                // an unrecoverable delete of a different kind of thing than the user was moving.
                // Making move agree with copy is the reading that cannot lose data.
                throw Occupied(plan, FileConflictException.DifferentKindExists);
            }

            RunRenamedCreate(plan, () =>
            {
                if (AreSameVolume(source, destination))
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    // Cross-volume directory move: Directory.Move fails across devices, so copy then
                    // delete. As above, the source is removed only after the copy has succeeded,
                    // and overwrite is passed through rather than forced for the reason given above.
                    CopyDirectoryRecursive(source, destination, overwrite, ct);
                    Directory.Delete(source, recursive: true);
                }
            });
        }
        else
        {
            throw new FileNotFoundException($"'{relativePath}' not found in root '{root.DisplayName}'.");
        }

        return Task.FromResult(plan.ResolvedName);
    }

    /// <summary>
    /// Reports an occupied destination with the code that fits WHO CHOSE THE NAME.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THE CODE IS A BUTTON, NOT A SENTENCE (RemEx-nhw2).** When the user named the destination,
    /// "that already exists" is a question they can answer, and the client offers Replace for it.
    /// When "keep both" made the host INVENT the name, the same code is a trap: Replace re-answers
    /// the ORIGINAL request — overwrite the file the user first named — while the sheet is showing
    /// the invented sibling. Somebody who chose keep-both precisely to preserve that file would
    /// destroy it by answering a question about a different one.
    /// </para>
    /// <para>
    /// So a name WE picked reports <c>resolved_name_taken</c>, which carries keep-both and skip and
    /// never replace. Asking again re-lists the directory and takes the next free name.
    /// </para>
    /// <para>
    /// ONE MOUNT WHERE THAT RETRY DOES NOT CONVERGE, recorded rather than claimed away: on a
    /// case-INSENSITIVE volume under a Linux host — SMB, exFAT, ntfs-3g — <c>NextAvailableName</c>
    /// compares Ordinal, so it can pick "b (2).txt" while "B (2).txt" is sitting there, and the
    /// pre-check below then rejects it every time. Skip still ends it, so this is a livelock the
    /// user can leave rather than data loss, and it predates this change (RemEx-2knx).
    /// </para>
    /// <para>
    /// The kind distinction is dropped for an invented name on purpose. "A folder is where your file
    /// was going" is useful about a destination the user chose; about a sibling they never saw it is
    /// noise, and the answer — pick another name — is the same either way.
    /// </para>
    /// </remarks>
    internal static FileConflictException Occupied(
        ConflictResolutionPlan plan, Func<string, FileConflictException> ifTheUserChoseTheName) =>
        plan.ResolvedName is null
            ? ifTheUserChoseTheName(Path.GetFileName(plan.DestinationPath))
            : FileConflictException.ResolvedNameTaken(plan.ResolvedName);

    /// <summary>
    /// Runs a create whose destination "keep both" renamed, probing the chosen name first and
    /// translating only the PROBE's refusal into its own code.
    /// </summary>
    /// <remarks>
    /// **THE GAP THIS CLOSES (RemEx-cirk), REPRODUCED BEFORE IT WAS FIXED.** NextAvailableName
    /// guarantees the chosen name is ABSENT from the destination; it never guarantees it is
    /// CREATABLE. Measured: a 255-character name creates fine, the same name with " (2)" appended is
    /// 259 and throws — and Windows long-path support does not save it, because the limit breached
    /// is the COMPONENT limit, not the path limit. The user then gets the OS's opaque "filename,
    /// directory name, or volume label syntax is incorrect" AFTER choosing Keep both, which is worse
    /// than getting it before.
    ///
    /// ONLY PROBES THE RENAMED CASE, and probes rather than wraps. When the destination is the one
    /// the caller asked for, an IOException means whatever it has always meant — this code asserts
    /// "the name WE chose is unusable", which can only be true when we chose one. And the operation
    /// itself runs UNWRAPPED, because choosing a name never established that a later failure is
    /// ABOUT that name; only touching the name alone does.
    /// </remarks>
    internal static void RunRenamedCreate(ConflictResolutionPlan plan, Action create)
    {
        if (plan.ResolvedName is null)
        {
            create();
            return;
        }

        // PROBE THE NAME, THEN RUN THE OPERATION UNWRAPPED. The guard this replaces wrapped the
        // WHOLE operation - including a recursive copy over an entire tree - so a disk-full partway
        // through, an ACL denial on a nested child, or a deep CHILD path breaching MAX_PATH all
        // emerged as "the name we chose is unusable". A confident wrong diagnosis, and one the client
        // renders as an offer to rename, which fixes none of those.
        //
        // The precondition (we chose a name) never established the ATTRIBUTION (this failure is
        // about that name). Only a probe does: creating and removing the exact path tests exactly
        // the thing that might be wrong, and asks the OS rather than guessing a length limit - which
        // is the same reason FileConflictNaming refuses to clamp, since ext4 counts 255 BYTES and
        // exFAT and CIFS differ again.
        //
        // DELETE-ON-CLOSE RATHER THAN A finally, and review is the reason. A finally runs on the
        // THROW path too, where CreateNew failed BECAUSE THE PATH ALREADY EXISTS - so the cleanup
        // deleted a file the probe never created, on its way to reporting a name it had just
        // destroyed. Letting the kernel own the lifetime makes "remove only what we made" structural
        // rather than a condition someone can drop later. ON WINDOWS it also closes the window where
        // the process dying between create and delete strands a zero-byte file where the copy is
        // about to land, because the kernel owns the deletion. ON LINUX IT DOES NOT: .NET performs
        // the unlink itself at handle disposal, so a hard kill still strands the file - measured
        // under WSL, not assumed. The consequence is bounded (see below), and no cleanup that could
        // run after a kill exists to fix it anyway.
        //
        // A filesystem that silently ignores delete-on-close - an SMB or FUSE-backed shared root is
        // not hypothetical here - leaves that stray file behind, and the cost is a FAILED COPY rather
        // than mere litter: the create() below runs File.Copy with overwrite false, or
        // CreateDirectory over a file, and hits the leftover. Accepted rather than papered over: a
        // follow-up "delete it if it is still there" check would reopen the exact window this design
        // closed, since another writer can claim the name in between and we delete theirs.
        FileStream? probe = null;
        try
        {
            probe = new FileStream(plan.DestinationPath, FileMode.CreateNew, FileAccess.Write,
                                   FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // ASK THE FILESYSTEM, NOT THE EXCEPTION TYPE, and the platforms are why. Something
            // OCCUPYING the path is refused as UnauthorizedAccessException on Windows when it is a
            // directory, and as EEXIST - which .NET surfaces as IOException - on Linux, because
            // O_EXCL fails before the kind is ever considered (measured; an earlier version of this
            // comment claimed EISDIR, which is what you get WITHOUT O_EXCL, a call this never
            // makes). No type test can span that. Asking what is actually there can.
            var occupied = File.Exists(plan.DestinationPath) || Directory.Exists(plan.DestinationPath);

            if (occupied)
            {
                // TAKEN, NOT UNUSABLE, and the difference is whether asking again can work. This name
                // is perfectly creatable; something simply got there first. Retrying keep-both
                // re-lists and picks the next free name, so the client can offer that - which
                // resolved_name_unusable, a Skip-only dead end, could not (RemEx-od7s).
                //
                // NOT destination_exists either, however true that sentence is. That code unlocks
                // Replace, and Replace re-answers the ORIGINAL request - overwrite the destination
                // the user first named - while the sheet is showing this invented sibling. A user
                // who chose keep-both precisely to protect the original would destroy it by
                // answering a question about a different file.
                throw FileConflictException.ResolvedNameTaken(plan.ResolvedName);
            }

            if (ex is not (UnauthorizedAccessException or DirectoryNotFoundException))
            {
                // REPORTED AS AN UNUSABLE NAME EVEN WHEN IT IS MERELY TAKEN, and that is a deliberate
                // under-claim (RemEx-od7s). "That name is taken" is the truer sentence, but the only
                // existing code carrying it is destination_exists, and the client unlocks REPLACE for
                // that. Replace re-answers the ORIGINAL request - overwrite b.txt - while the sheet
                // is naming the sibling b (2).txt, so a user who chose "keep both" to protect b.txt
                // would destroy it by answering a question about a different file. resolved_name_
                // unusable is Skip-only, so it costs a retry - and, until RemEx-od7s, a body string
                // that names the WRONG CAUSE, since the client renders this code as "this name is
                // too long for the destination folder". A wrong cause the user can recover from
                // still beats a right one wired to a button that deletes their file. RemEx-od7s adds
                // a code that can say "taken" AND offer keep-both safely.
                throw FileConflictException.ResolvedNameUnusable(plan.ResolvedName, ex);
            }

            // FALLING THROUGH TO THE OPERATION IS THE POINT of what is left: nothing is in the way,
            // so a denial or a missing parent is a property of the FOLDER rather than of the name we
            // picked - the same denial, and the same missing parent, would meet any name at all. The
            // operation below hits them too and reports them honestly.
            //
            // THE PROBE IS ALWAYS A FILE, even when the operation will create a DIRECTORY, so that
            // nobody re-derives this: a folder granting FILE_ADD_SUBDIRECTORY while denying
            // FILE_ADD_FILE lands exactly here - UnauthorizedAccessException, nothing occupying the
            // path - and falls through, so the directory copy proceeds and reports for itself. No
            // configuration is known where file creation is refused by some OTHER type while
            // directory creation would have succeeded, which is the only case this would misjudge.
            //
            // THE ONE CASE THE THROW ABOVE STILL OVER-CLAIMS, stated rather than hidden: a
            // volume-level refusal - a full disk, an exhausted quota, a read-only mount - arrives as
            // a bare IOException with nothing at the path, and lands there as a name verdict.
            // Separating it means sniffing HResults per platform, which is the guessing this code
            // refuses to do; the exposure is one zero-byte create rather than a whole recursive copy.
        }

        // DISPOSED OUTSIDE THE CLASSIFYING CATCH, so "everything in that catch means the OPEN failed"
        // holds without exception. Inside a using, a disposal failure - delete-on-close refused at
        // handle close - would run the classifier with the probe's OWN file still on disk, and it
        // would dutifully report a squatter that is us. Out here a disposal failure propagates raw,
        // which is honest: nothing about it is a verdict on the name.
        probe?.Dispose();

        create();
    }

    /// <summary>
    /// The bare names already present in <paramref name="directory"/>, for conflict renaming.
    /// </summary>
    /// <remarks>
    /// FILES AND FOLDERS TOGETHER, because a name is taken either way — offering "report (2)" as a
    /// free name when a FOLDER called "report (2)" is sitting there produces the collision the
    /// rename existed to avoid, one step later and with the user believing it was handled.
    ///
    /// A directory that cannot be listed yields nothing, which makes every candidate look free. That
    /// is the safe direction: the operation then proceeds and fails on the real filesystem with the
    /// ordinary collision error, rather than this method deciding an outcome it could not see.
    /// </remarks>
    private static IReadOnlyList<string> ListDirectoryNames(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public Task CreateDirectoryAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        if (!root.IsWritable)
            throw new UnauthorizedAccessException($"New folders are not permitted in '{root.DisplayName}' (read-only).");

        var resolved = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);

        // MKDIR IS REFUSAL-ONLY, AND THAT IS A LIMIT WORTH STATING. Both throws below carry a
        // conflict code, but this method takes no conflictResolution and the handler passes
        // none - so a client that offers Replace or Keep both here gets the same refusal back.
        // The codes are still right (they say WHY, and "skip" is a valid answer to both), but a
        // client must not present the retry actions on this operation. RemEx-agpn carries that.
        if (Directory.Exists(resolved))
            throw FileConflictException.DirectoryExists(Path.GetFileName(resolved));

        // A FILE STANDING WHERE A FOLDER IS GOING is the different-kind case here too, and review
        // caught it reporting the plain collision code — which would have had the sheet offer
        // "Replace" for a mkdir blocked by a file, i.e. deleting the user's file to make a folder.
        // The physical situation is identical to the copy/move branches, so the code must be.
        if (File.Exists(resolved))
            throw FileConflictException.DifferentKindExists(Path.GetFileName(resolved));

        Directory.CreateDirectory(resolved);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileSearchEntry>> SearchAsync(
        string rootId, string relativePath, string query, int maxResults, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        return SearchCore(Path.GetFullPath(root.AbsolutePath), root.DisplayName, relativePath, query, maxResults, ct);
    }

    /// <summary>Volume-mode counterpart of <see cref="SearchAsync"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    public Task<IReadOnlyList<FileSearchEntry>> SearchVolumeAsync(
        string volumeAbsolutePath, string relativePath, string query, int maxResults, CancellationToken ct)
    {
        var rootPath = Path.GetFullPath(volumeAbsolutePath);
        return SearchCore(rootPath, volumeAbsolutePath, relativePath, query, maxResults, ct);
    }

    private static Task<IReadOnlyList<FileSearchEntry>> SearchCore(
        string rootPath, string rootDisplay, string relativePath, string query, int maxResults, CancellationToken ct)
    {
        var baseDir = FilePathValidation.ResolveWithinRoot(rootPath, relativePath, rootDisplay);

        var cap = maxResults <= 0
            ? FileTransferLimits.SearchMaxResults
            : Math.Min(maxResults, FileTransferLimits.SearchMaxResults);

        var results = new List<FileSearchEntry>(Math.Min(cap, 64));
        if (!string.IsNullOrWhiteSpace(query) && Directory.Exists(baseDir))
            SearchRecursive(rootPath, baseDir, query, cap, results, ct);

        return Task.FromResult<IReadOnlyList<FileSearchEntry>>(results);
    }

    public Task<FileMetadata> GetMetadataAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        var resolved = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
        return GetMetadataCore(resolved, root.DisplayName, relativePath);
    }

    /// <summary>Volume-mode counterpart of <see cref="GetMetadataAsync"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    public Task<FileMetadata> GetVolumeMetadataAsync(string volumeAbsolutePath, string relativePath, CancellationToken ct)
    {
        var resolved = FilePathValidation.ResolveWithinRoot(volumeAbsolutePath, relativePath, volumeAbsolutePath);
        return GetMetadataCore(resolved, volumeAbsolutePath, relativePath);
    }

    private static Task<FileMetadata> GetMetadataCore(string resolved, string rootDisplay, string relativePath)
    {
        if (Directory.Exists(resolved))
        {
            var dir = new DirectoryInfo(resolved);
            int itemCount;
            try
            {
                itemCount = dir.EnumerateFileSystemInfos().Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                itemCount = 0;
            }

            return Task.FromResult(new FileMetadata
            {
                Size = 0,
                CreatedUtc = ToUnixMs(dir.CreationTimeUtc),
                ModifiedUtc = ToUnixMs(dir.LastWriteTimeUtc),
                IsDirectory = true,
                ItemCount = itemCount,
                MimeType = null,
                ReadOnly = (dir.Attributes & FileAttributes.ReadOnly) != 0,
            });
        }

        if (File.Exists(resolved))
        {
            var file = new FileInfo(resolved);
            return Task.FromResult(new FileMetadata
            {
                Size = file.Length,
                CreatedUtc = ToUnixMs(file.CreationTimeUtc),
                ModifiedUtc = ToUnixMs(file.LastWriteTimeUtc),
                IsDirectory = false,
                ItemCount = null,
                MimeType = InferMimeType(resolved),
                ReadOnly = file.IsReadOnly,
            });
        }

        throw new FileNotFoundException($"'{relativePath}' not found in root '{rootDisplay}'.");
    }

    public Task<string?> GetThumbnailBase64Async(string rootId, string relativePath, int maxDim, CancellationToken ct)
    {
        var root = GetConfiguredRoot(rootId);
        var resolved = FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
        return GetThumbnailCore(resolved, root.DisplayName, relativePath, maxDim, ct);
    }

    /// <summary>Volume-mode counterpart of <see cref="GetThumbnailBase64Async"/>. See <see cref="OpenVolumeForReadAsync"/>.</summary>
    public Task<string?> GetVolumeThumbnailBase64Async(string volumeAbsolutePath, string relativePath, int maxDim, CancellationToken ct)
    {
        var resolved = FilePathValidation.ResolveWithinRoot(volumeAbsolutePath, relativePath, volumeAbsolutePath);
        return GetThumbnailCore(resolved, volumeAbsolutePath, relativePath, maxDim, ct);
    }

    private async Task<string?> GetThumbnailCore(string resolved, string rootDisplay, string relativePath, int maxDim, CancellationToken ct)
    {
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found in shared root '{rootDisplay}': {relativePath}");

        var effectiveMaxDim = maxDim <= 0 ? FileTransferLimits.ThumbnailDefaultMaxDim : maxDim;
        return await _thumbnailService.TryCreateThumbnailBase64Async(
            resolved, effectiveMaxDim, FileTransferLimits.ThumbnailMaxBytes, ct);
    }

    /// <summary>
    /// Recursively collects search hits (name contains <paramref name="query"/>, case-insensitive) under
    /// <paramref name="currentDir"/>, stopping once <paramref name="cap"/> results are gathered. Restricted
    /// system paths are skipped and unreadable subtrees are ignored rather than aborting the whole search.
    /// </summary>
    private static void SearchRecursive(
        string rootPath, string currentDir, string query, int cap, List<FileSearchEntry> results, CancellationToken ct)
    {
        if (results.Count >= cap)
            return;

        ct.ThrowIfCancellationRequested();

        IEnumerable<FileSystemInfo> children;
        try
        {
            children = new DirectoryInfo(currentDir).EnumerateFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (results.Count >= cap)
                return;
            ct.ThrowIfCancellationRequested();

            if (FilePathValidation.IsRestrictedSystemPath(child.FullName))
                continue;

            var isDirectory = child is DirectoryInfo;
            if (child.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FileSearchEntry
                {
                    Name = child.Name,
                    RelativePath = ToRootRelative(rootPath, child.FullName),
                    IsDirectory = isDirectory,
                    SizeBytes = child is FileInfo fi ? fi.Length : 0,
                    ModifiedUnixMs = ToUnixMs(child.LastWriteTimeUtc),
                });
            }

            if (isDirectory)
                SearchRecursive(rootPath, child.FullName, query, cap, results, ct);
        }
    }

    /// <summary>Returns a forward-slash path relative to the root so both PC and Android can re-navigate it.</summary>
    private static string ToRootRelative(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        return relative.Replace('\\', '/');
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }

    /// <summary>
    /// Restores normal ACL inheritance on a file that arrived by rename (Windows only, no-op elsewhere).
    /// </summary>
    /// <remarks>
    /// NTFS does not recompute inherited ACEs when a file is renamed across directories: the moved file
    /// keeps the ACEs it inherited from where it came from, still flagged as inherited but from the old
    /// parent. Staging lives machine-wide under ProgramData, so without this a file promoted into the
    /// user's Documents would arrive carrying ProgramData's ACL — the owner would get read-only access
    /// to their own received file, and every other local account would gain read access it never had
    /// under the old copy. Clearing protection with preserveInheritance:false drops the stale ACEs and
    /// lets the new parent's inheritable ACEs apply, which reproduces exactly what creating the file in
    /// place used to produce. Unix rename() preserves owner and mode and the agent already runs as the
    /// signed-in user, so there is nothing to repair there (RemEx-fq6f).
    /// </remarks>
    private void RestoreInheritedAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Deliberately catch-all. By this point the file is already sitting complete and correct at
            // its destination, so letting anything escape would report verified:false for a transfer
            // that actually succeeded — strictly worse than delivering it with a stale ACL. A narrower
            // filter missed the generic exceptions SetAccessControl raises for unrecognized Win32
            // errors, which is exactly the case this is here to absorb.
            _logger.LogWarning(ex, "Could not restore inherited permissions on {Path} after promotion.", path);
        }
    }

    private static bool AreSameVolume(string a, string b)
    {
        var fullA = Path.GetFullPath(a);
        var fullB = Path.GetFullPath(b);

        if (OperatingSystem.IsWindows())
        {
            return string.Equals(Path.GetPathRoot(fullA), Path.GetPathRoot(fullB), StringComparison.OrdinalIgnoreCase);
        }

        // Path.GetPathRoot returns "/" for EVERY absolute Unix path, so the comparison above would call
        // any two paths same-volume and a genuinely cross-device promotion would silently take the
        // rename branch — where .NET falls back to an internal copy that is synchronous, uncancellable
        // and not atomic. Compare mount points instead (RemEx-fq6f).
        return string.Equals(MountPointOf(fullA), MountPointOf(fullB), StringComparison.Ordinal);
    }

    /// <summary>Longest mounted filesystem root that contains <paramref name="fullPath"/>.</summary>
    private static string MountPointOf(string fullPath)
    {
        var best = "/";
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var mount = drive.RootDirectory.FullName;
                if (mount.Length <= best.Length)
                    continue;
                var prefix = mount.EndsWith('/') ? mount : mount + '/';
                if (fullPath.StartsWith(prefix, StringComparison.Ordinal) || fullPath == mount)
                    best = mount;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable mount table: fall back to "/" for both sides, which reports same-volume and
            // therefore behaves exactly as this did before. Degrading to the old behaviour is the right
            // failure here — it still delivers the file.
        }
        return best;
    }

    private static bool IsDestinationInsideSource(string sourceDir, string destination)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedSource = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDest = Path.GetFullPath(destination);
        return normalizedDest.StartsWith(normalizedSource + Path.DirectorySeparatorChar, comparison)
            || normalizedDest.StartsWith(normalizedSource + Path.AltDirectorySeparatorChar, comparison);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir, bool overwrite, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(target) && !overwrite)
                // Reported the same way as a top-level collision even though it is one file INSIDE a
                // recursive copy. The name it carries is that inner file's, which is what the user
                // needs to see — a code naming the folder they dragged would send them looking at the
                // wrong thing.
                throw FileConflictException.FileExists(Path.GetFileName(target));
            File.Copy(file, target, overwrite);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectoryRecursive(sub, Path.Combine(destDir, Path.GetFileName(sub)), overwrite, ct);
        }
    }

    /// <summary>
    /// Best-effort MIME type from the file extension for <see cref="FileMetadata.MimeType"/>. Covers common
    /// document/image/media/archive types; returns null for unknown extensions (the client shows a generic
    /// icon). Intentionally a small static table — no <c>System.Web</c>/registry lookups needed on the host.
    /// </summary>
    private static string? InferMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".ini" or ".cfg" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        ".rar" => "application/vnd.rar",
        ".gz" or ".tgz" => "application/gzip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".heic" => "image/heic",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".webm" => "video/webm",
        ".exe" => "application/vnd.microsoft.portable-executable",
        _ => null,
    };

    private static IReadOnlyList<FileSharedRoot> MapToSharedRoots(IReadOnlyList<ConfiguredRoot> roots)
        => roots.Select(r => new FileSharedRoot
        {
            RootId = r.RootId,
            DisplayName = r.DisplayName,
            IsWritable = r.IsWritable,
            CanRename = r.CanRename,
            CanMove = r.CanMove,
            CanDelete = r.CanDelete,
            CanRemoveRoot = r.CanRemoveRoot,
        }).ToList();

    private ConfiguredRoot GetConfiguredRoot(string rootId)
    {
        var root = LoadConfiguredRoots().FirstOrDefault(candidate => candidate.RootId == rootId);
        if (root is null)
            throw new UnauthorizedAccessException($"Unknown shared root '{rootId}'.");

        return root;
    }

    // Browse/transfer/delete/rename/hash resolve through the SAME shared validator as copy/move/search/
    // metadata/thumbnail, so the root-escape guard and the full restricted-system-path denylist
    // (/proc, /sys, /dev, /run, /boot/efi) are enforced uniformly rather than via a narrower local copy.
    private string ResolvePath(string rootId, string relativePath)
    {
        var root = GetConfiguredRoot(rootId);
        return FilePathValidation.ResolveWithinRoot(root.AbsolutePath, relativePath, root.DisplayName);
    }

    private IReadOnlyList<ConfiguredRoot> LoadConfiguredRoots()
    {
        if (!File.Exists(_configPath))
        {
            var defaults = CreateDefaultRoots();
            SaveConfiguredRoots(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var roots = JsonSerializer.Deserialize<List<ConfiguredRoot>>(json, JsonOptions);
            if (roots is not null)
                return roots
                    .Where(root => !string.IsNullOrWhiteSpace(root.AbsolutePath))
                    .Where(root => Directory.Exists(root.AbsolutePath))
                    .Select(root => root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) })
                    .ToList();
        }
        catch (Exception ex)
        {
            // Do not rethrow — empty/default roots is a recoverable state.
            // Log so operators can diagnose configuration corruption or permission issues.
            _logger.LogError(ex,
                "Failed to load configured file-transfer roots from {Path}. " +
                "Falling back to defaults.", _configPath);
        }

        var fallbackRoots = CreateDefaultRoots();
        SaveConfiguredRoots(fallbackRoots);
        return fallbackRoots;
    }

    private void SaveConfiguredRoots(IReadOnlyList<ConfiguredRoot> roots)
    {
        var json = JsonSerializer.Serialize(roots, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static List<ConfiguredRoot> CreateDefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var transfers = Path.Combine(home, "RemEx Transfers");
        Directory.CreateDirectory(transfers);

        var candidates = new List<ConfiguredRoot>
        {
            new()
            {
                RootId = "transfers",
                DisplayName = "RemEx Transfers",
                AbsolutePath = transfers,
                IsWritable = true,
                CanRename = true,
                CanMove = true,
                CanDelete = true,
                CanRemoveRoot = false,
            },
            new()
            {
                RootId = "downloads",
                DisplayName = "Downloads",
                AbsolutePath = Path.Combine(home, "Downloads"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
            new()
            {
                RootId = "desktop",
                DisplayName = "Desktop",
                AbsolutePath = Path.Combine(home, "Desktop"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
            new()
            {
                RootId = "documents",
                DisplayName = "Documents",
                AbsolutePath = Path.Combine(home, "Documents"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
            new()
            {
                RootId = "pictures",
                DisplayName = "Pictures",
                AbsolutePath = Path.Combine(home, "Pictures"),
                IsWritable = false,
                CanRemoveRoot = true,
            },
        };

        return candidates
            .Where(root => Directory.Exists(root.AbsolutePath))
            .Select(root => root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) })
            .ToList();
    }
}
