using System;
using System.IO;

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
}
