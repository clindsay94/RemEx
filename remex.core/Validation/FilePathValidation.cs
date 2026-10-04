using System.Globalization;

namespace Remex.Core.Validation;

/// <summary>
/// Centralized path-security helper for the file-sharing feature (plan §2). Extracted from the inline
/// <c>FileTransferService.ResolvePath</c> logic so every code path (browse, transfer, copy/move/mkdir,
/// search, metadata, thumbnails) enforces the SAME root-escape and system-path rules.
///
/// <para>
/// Pure <see cref="Path"/>/string math plus OS checks — no reflection or serialization — so it is safe
/// on the NativeAOT-compiled <c>Remex.Core</c> surface.
/// </para>
///
/// <para>
/// Security model: a relative path is resolved against the root via <see cref="Path.GetFullPath(string)"/>
/// and must remain inside the root prefix. This collapses <c>..</c> traversal and normalizes separators.
/// On Linux an additional denylist (<c>/proc</c>, <c>/sys</c>, <c>/dev</c>, <c>/run</c>, <c>/boot/efi</c>)
/// is enforced unconditionally to keep pseudo-filesystems and firmware paths off-limits even when a full
/// volume is shared. NOTE: like the original logic, <see cref="Path.GetFullPath(string)"/> does not
/// resolve symlinks — the prefix check bounds the LEXICAL path; defense against a symlink whose target
/// escapes the root is layered on top by callers that also refuse to follow reparse points where needed.
/// </para>
/// </summary>
public static class FilePathValidation
{
    /// <summary>
    /// Linux system paths that are always denied, regardless of shared-root configuration or a
    /// full-browse grant. Extended per plan §2 to include <c>/run</c> and <c>/boot/efi</c>.
    /// </summary>
    private static readonly string[] RestrictedLinuxPaths =
        ["/proc", "/sys", "/dev", "/run", "/boot/efi"];

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="rootAbsolutePath"/> and returns the
    /// full absolute path, guaranteeing the result stays inside the root and is not a restricted system
    /// path. Throws <see cref="UnauthorizedAccessException"/> on any escape or denied path.
    /// </summary>
    /// <param name="rootAbsolutePath">The shared root's absolute path (need not be pre-normalized).</param>
    /// <param name="relativePath">
    /// The client-supplied path relative to the root. Null / whitespace / "/" / "\" all mean "the root
    /// itself". Leading slashes are trimmed; embedded <c>..</c> is collapsed and then bounds-checked.
    /// </param>
    /// <param name="rootDisplayName">Optional friendly name used only in the exception message.</param>
    public static string ResolveWithinRoot(string rootAbsolutePath, string? relativePath, string? rootDisplayName = null)
    {
        if (!TryResolveWithinRoot(rootAbsolutePath, relativePath, out var resolved, out var error, rootDisplayName))
            throw new UnauthorizedAccessException(error);
        return resolved;
    }

    /// <summary>
    /// Non-throwing variant of <see cref="ResolveWithinRoot"/>. Returns true and sets
    /// <paramref name="resolved"/> on success; returns false and sets <paramref name="error"/> on any
    /// escape or restricted-path violation.
    /// </summary>
    public static bool TryResolveWithinRoot(
        string rootAbsolutePath,
        string? relativePath,
        out string resolved,
        out string? error,
        string? rootDisplayName = null)
    {
        resolved = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rootAbsolutePath))
        {
            error = "Access denied: no shared root was supplied.";
            return false;
        }

        var rootPath = Path.GetFullPath(rootAbsolutePath);

        var trimmedRelativePath = string.IsNullOrWhiteSpace(relativePath) || relativePath is "/" or "\\"
            ? string.Empty
            : relativePath.TrimStart('/', '\\');

        var candidate = string.IsNullOrEmpty(trimmedRelativePath)
            ? rootPath
            : Path.GetFullPath(Path.Combine(rootPath, trimmedRelativePath));

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Drive-letter roots (e.g. "Z:\") come back from Path.GetFullPath WITH a trailing separator,
        // so "rootPath + separator" would be "Z:\\" and no child ("Z:\Folder") could ever match — the
        // whole drive would look like it escapes itself. Strip any trailing separator before building the
        // containment prefix; the Equals check still accepts the root path exactly as GetFullPath returns it.
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var isInsideRoot = candidate.Equals(rootPath, pathComparison)
            || candidate.Equals(rootPrefix, pathComparison)
            || candidate.StartsWith(rootPrefix + Path.DirectorySeparatorChar, pathComparison)
            || candidate.StartsWith(rootPrefix + Path.AltDirectorySeparatorChar, pathComparison);

        if (!isInsideRoot)
        {
            error = rootDisplayName is null
                ? $"Access denied: '{relativePath}' escapes the shared root."
                : $"Access denied: '{relativePath}' escapes shared root '{rootDisplayName}'.";
            return false;
        }

        if (IsRestrictedSystemPath(candidate))
        {
            error = $"Access denied: '{relativePath}' is a restricted system path.";
            return false;
        }

        resolved = candidate;
        return true;
    }

    /// <summary>
    /// True when <paramref name="resolvedAbsolutePath"/> falls within the Linux system denylist. Guarded
    /// by an OS check, so it always returns false on non-Linux platforms (the denied paths are Linux-only).
    /// </summary>
    public static bool IsRestrictedSystemPath(string resolvedAbsolutePath)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(resolvedAbsolutePath))
            return false;

        foreach (var restricted in RestrictedLinuxPaths)
        {
            if (resolvedAbsolutePath == restricted
                || resolvedAbsolutePath.StartsWith(restricted + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a single path segment (a new file/folder name for rename/mkdir/copy/move). Rejects
    /// null/empty/whitespace, the reserved <c>.</c> and <c>..</c> names, any path separator, and any
    /// character in <see cref="Path.GetInvalidFileNameChars"/>. Both '/' and '\' are rejected on every
    /// platform (not just where the OS lists them) so a client cannot smuggle a traversal through a name.
    /// </summary>
    public static bool IsValidFileName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name cannot be empty.";
            return false;
        }

        if (name is "." or "..")
        {
            error = "Name cannot be '.' or '..'.";
            return false;
        }

        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
        {
            error = "Name cannot contain a path separator.";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Name contains invalid characters.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The longest single name a phone's storage accepts, in UTF-8 BYTES (NAME_MAX on ext4/f2fs counts
    /// bytes, so 128 two-byte characters are already too long). Mirrors <c>SharedPathPolicy.MAX_SEGMENT_LENGTH</c>
    /// on the phone, which counts the same way.
    /// </summary>
    public const int MaxRemoteNameLength = 255;

    /// <summary>The most segments a path sent to a phone may have; far past anything a real tree needs.</summary>
    public const int MaxRemotePathSegments = 64;

    /// <summary>
    /// Validates a NEW name for a file or folder on ANOTHER device — the phone — before the PC asks for it
    /// to be created or renamed there (RemEx-fgmne). Mirrors the phone's own
    /// <c>SharedPathPolicy.isSafeNewName</c>, and both sides run it: the phone is the one that cannot be
    /// talked around, and this one stops the PC sending what it already knows will be refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT <see cref="IsValidFileName"/>. That one applies THIS machine's invalid-character
    /// list, which on Windows would refuse <c>:</c>, <c>?</c> and <c>"</c> — all ordinary in an Android
    /// file name — while checking nothing about length. What matters for a name headed to a phone is
    /// that it is a NAME and not a path: no separator, no <c>.</c>/<c>..</c>, no control character, and
    /// no longer than the phone's filesystem takes.
    /// </para>
    /// <para>
    /// ALSO NO UNICODE FORMAT CHARACTER (<see cref="UnicodeCategory.Format"/>: U+202E right-to-left
    /// override, U+200B zero-width space, ...). They make a name read as something it is not, in a
    /// listing and in the delete confirmation. This rule is for names the PC INVENTS; the names of files
    /// that already exist go through <see cref="IsValidRemoteRelativePath"/>, which does not apply it, so a
    /// file whose name holds an emoji joined with U+200D can still be browsed and deleted.
    /// </para>
    /// </remarks>
    public static bool IsValidRemoteName(string? name, out string? error) =>
        CheckRemoteSegment(name, forNewName: true, out error);

    /// <summary>
    /// Validates a '/'-separated path under a phone's shared folder: empty (the folder itself) is fine;
    /// every segment must be a real name, so <c>..</c>, a backslash, a control character, an empty middle
    /// segment (<c>a//b</c>) or a segment over the byte cap refuses the whole path. One leading and one
    /// trailing '/' run is trimmed first, because a root-relative path may be written either way.
    /// </summary>
    /// <remarks>
    /// Names of things that ALREADY EXIST are accepted with a format character in them (see
    /// <see cref="IsValidRemoteName"/>); pass the FINAL segment of a path that names something new through
    /// <see cref="IsValidRemoteName"/> as well.
    /// </remarks>
    public static bool IsValidRemoteRelativePath(string? relativePath, out string? error)
    {
        error = null;
        var trimmed = (relativePath ?? string.Empty).Trim('/');
        if (trimmed.Length == 0)
            return true;

        var segments = trimmed.Split('/');
        if (segments.Length > MaxRemotePathSegments)
        {
            error = "Path is too deep.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!CheckRemoteSegment(segment, forNewName: false, out var segmentError))
            {
                error = segmentError;
                return false;
            }
        }

        return true;
    }

    private static bool CheckRemoteSegment(string? name, bool forNewName, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name cannot be empty.";
            return false;
        }

        if (name is "." or "..")
        {
            error = "Name cannot be '.' or '..'.";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxRemoteNameLength)
        {
            error = "Name is too long.";
            return false;
        }

        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
        {
            error = "Name cannot contain a path separator.";
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c)
                || (forNewName && char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            {
                error = "Name contains invalid characters.";
                return false;
            }
        }

        return true;
    }
}
