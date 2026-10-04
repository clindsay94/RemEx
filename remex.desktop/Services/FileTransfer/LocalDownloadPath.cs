using System;
using System.IO;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Turns a path the REMOTE side supplied into a local path that is guaranteed to stay inside the
/// folder the person picked, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// A FOLDER DOWNLOAD IS THE ONE FLOW THAT WRITES TO LOCAL PATHS BUILT FROM REMOTE STRINGS. The manifest
/// comes from whatever is on the other end — the PC's own host, or, since RemEx-xt0af, a phone — and a
/// hostile or broken one can name <c>..</c>, a rooted path, a drive-qualified segment, or a name no
/// file system accepts. <see cref="Path.Combine(string[])"/> would let a rooted segment discard the
/// destination folder entirely, so every one of those is refused here rather than repaired.
/// </para>
/// <para>
/// TWO CHECKS, AND THE SECOND IS NOT REDUNDANT. The per-segment rules catch everything known; the final
/// containment check compares the fully resolved path against the resolved root, so a case the rules
/// missed still cannot land outside it.
/// </para>
/// </remarks>
public static class LocalDownloadPath
{
    /// <summary>The longest single name either NTFS or ext4 accepts (NAME_MAX).</summary>
    public const int MaxSegmentLength = 255;

    private static readonly char[] InvalidNameChars = BuildInvalidNameChars();

    /// <summary>
    /// Resolves <paramref name="remoteRelative"/> under <paramref name="localRoot"/>. Returns false, and
    /// a null <paramref name="fullPath"/>, when the remote path must not be written.
    /// </summary>
    public static bool TryResolve(string localRoot, string? remoteRelative, out string? fullPath)
    {
        fullPath = null;
        var relative = ToSafeRelativePath(remoteRelative);
        if (relative is null || string.IsNullOrWhiteSpace(localRoot))
            return false;

        var root = Path.GetFullPath(localRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(root, candidate))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// The remote path as a local relative path with the platform's separators, or null when any
    /// segment is unsafe: empty, <c>.</c> or <c>..</c>, ending in a dot or a space (which Windows
    /// strips, so two names would collide), rooted or drive-qualified, containing a
    /// backslash or a character the file system rejects, a reserved device name, or longer than
    /// <see cref="MaxSegmentLength"/>.
    /// </summary>
    /// <remarks>
    /// One leading or trailing '/' is tolerated, because a listing can legitimately be rooted at the
    /// shared folder's top; an empty segment anywhere else is refused, not collapsed.
    /// </remarks>
    public static string? ToSafeRelativePath(string? remoteRelative)
    {
        if (string.IsNullOrWhiteSpace(remoteRelative))
            return null;

        var trimmed = remoteRelative.Trim('/');
        if (trimmed.Length == 0)
            return null;

        var segments = trimmed.Split('/');
        foreach (var segment in segments)
        {
            if (!IsSafeSegment(segment))
                return null;
        }

        return Path.Combine(segments);
    }

    private static bool IsSafeSegment(string segment)
    {
        // "." and "..", and also "..." or ". ." — Windows strips trailing dots and spaces, so a name made
        // of nothing else collapses to nothing and the next name lands one level up from where it looks.
        if (string.IsNullOrWhiteSpace(segment) || segment.Trim('.', ' ').Length == 0)
            return false;

        // A TRAILING DOT OR SPACE IS REFUSED, NOT TRIMMED. Windows drops them, so "photo.jpg." and
        // "photo.jpg" are one file there: the second download would silently overwrite the first, and
        // "Camera." would merge into "Camera". Refusing the name leaves it out of the folder download
        // like every other unusable name (the queued count says how many made it), on both platforms,
        // so a listing behaves the same on Linux as on Windows.
        if (segment[^1] is '.' or ' ')
            return false;
        if (segment.Length > MaxSegmentLength)
            return false;

        // A backslash is a separator on Windows and an ordinary character on Linux. Refused on both, so
        // a name that is safe on one machine cannot become "..\..\x" on the other.
        if (segment.Contains('\\', StringComparison.Ordinal) || segment.Contains(':', StringComparison.Ordinal))
            return false;
        if (Path.IsPathRooted(segment))
            return false;
        if (segment.IndexOfAny(InvalidNameChars) >= 0)
            return false;

        return !IsReservedDeviceName(segment);
    }

    /// <summary>
    /// CON, NUL, COM1 and the rest open a DEVICE on Windows in any folder, with or without an extension.
    /// Refused everywhere, so a listing behaves the same on both platforms.
    /// </summary>
    private static bool IsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (stem.Length is < 3 or > 4)
            return false;

        var upper = stem.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL")
            return true;

        return upper.Length == 4
            && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
            && upper[3] is >= '1' and <= '9';
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// The union of what Windows refuses in a name, applied on every platform. Linux would accept most
    /// of these, but a downloaded tree that only exists on one OS is a bug report waiting to happen.
    /// </summary>
    private static char[] BuildInvalidNameChars()
    {
        var chars = new System.Collections.Generic.HashSet<char>(Path.GetInvalidFileNameChars())
        {
            '<', '>', ':', '"', '|', '?', '*', '/', '\\', '\0',
        };
        for (var c = (char)1; c < 32; c++)
            chars.Add(c);
        return [.. chars];
    }
}
