using Remex.Core.Guards;
using Remex.Core.Validation;

namespace Remex.Core.Models;

/// <summary>
/// Picks the "Keep both" name for a file whose name is already taken, using the host's rules.
/// </summary>
/// <remarks>
/// <para>
/// THE PHONE MUST NOT INVENT THIS NAME (RemEx-12cj). If the client guesses a suffix and the host
/// picks a different one, the user is shown one filename and gets another — or worse, the guess
/// still collides and the transfer fails again with the same opaque error the conflict sheet exists
/// to replace. So the rule lives here, in the shared assembly, and the host is the authority.
/// </para>
/// <para>
/// CASE SENSITIVITY IS THE TRAP, and it is CLAUDE.md's hard rule 4 in a different costume. On a
/// case-insensitive volume <c>Report.pdf</c> and <c>report.pdf</c> are the same file; on a
/// case-sensitive one they are two. A generator that compared names case-insensitively everywhere
/// would skip a perfectly free name on ext4; one that compared case-sensitively everywhere would
/// hand back a name that still collides on NTFS. The comparison therefore follows the DESTINATION
/// VOLUME, which is why this takes the existing-name set rather than touching the disk — the caller
/// knows where it is writing, and the function stays pure and testable on either platform.
/// </para>
/// <para>
/// The comparison is ORDINAL, never culture-aware, and that is load-bearing rather than incidental.
/// Under Turkish casing rules <c>I</c> folds to dotless <c>ı</c>, so a culture-aware comparer would
/// declare <c>file.txt</c> free on a Windows host that already holds <c>FILE.TXT</c> — and "keep
/// both" would overwrite. NTFS's own case folding comes from the volume's invariant <c>$UpCase</c>
/// table, not from the user's locale, so ordinal is what actually matches the filesystem.
/// </para>
/// </remarks>
public static class FileConflictNaming
{
    /// <summary>
    /// The highest suffix this will try before reporting that no name is available.
    /// </summary>
    /// <remarks>
    /// A destination holding ten thousand same-named files is not a case worth serving, but it must
    /// still be reported rather than guessed at — see the return contract on
    /// <see cref="NextAvailableName"/>.
    /// </remarks>
    public const int MaxSuffix = 10_000;

    /// <summary>
    /// Returns <paramref name="fileName"/> if it is free, otherwise the first
    /// <c>name (n).ext</c> that is not in <paramref name="existingNames"/>, or <see langword="null"/>
    /// if no such name exists.
    /// </summary>
    /// <param name="fileName">
    /// The desired file name, with no directory component. Validated with
    /// <see cref="FilePathValidation.IsValidFileName(string?, out string?)"/>; anything it rejects
    /// yields <see langword="null"/> rather than a silently amputated name. That is null, empty or
    /// whitespace, <c>.</c>, <c>..</c>, a name carrying either path separator, and any name holding
    /// a character in <c>Path.GetInvalidFileNameChars()</c>.
    /// <para>
    /// THAT LAST ONE FOLLOWS THE HOST OS, not the destination volume, and it is the one place in
    /// this class where those differ. The set is 41 characters on Windows and two on Linux, so
    /// <c>a*.txt</c> is refused by a Windows host and accepted by a Linux one. That is the right
    /// default — the host is the process that will call <c>File.Create</c>, so it should validate
    /// against its own filesystem — but a Linux host writing to the exFAT stick or CIFS share
    /// described under <paramref name="caseSensitive"/> will accept a name that volume cannot hold,
    /// and fail at write time instead of here.
    /// </para>
    /// </param>
    /// <param name="existingNames">
    /// Names already present in the destination directory. A caller resolving a BATCH must add each
    /// returned name to this set before resolving the next one; two uploads both called
    /// <c>report.pdf</c> against one unchanged snapshot would otherwise both be told
    /// <c>report (2).pdf</c> and the second would clobber the first.
    /// </param>
    /// <param name="caseSensitive">
    /// Whether the DESTINATION VOLUME distinguishes case — not merely whether the host is Linux.
    /// A Linux host writing to an exFAT stick or a CIFS share is case-insensitive, and Windows can
    /// enable per-directory case sensitivity. There is no default on purpose: a wrong guess here is
    /// exactly the bug this function exists to avoid, so the caller has to say which it means.
    /// </param>
    /// <returns>
    /// A name that is guaranteed absent from <paramref name="existingNames"/>, or
    /// <see langword="null"/> when the name is unusable or every suffix up to
    /// <see cref="MaxSuffix"/> is taken. NULL MEANS STOP, NOT "USE THE ORIGINAL". An earlier draft
    /// returned <paramref name="fileName"/> on exhaustion, which is a name this function had just
    /// proven was taken and which no caller could tell apart from success — in a "keep both" path
    /// that is an overwrite of the file the user asked to preserve.
    /// <para>
    /// The two null reasons are not distinguished here. Neither can be mistaken for success, so
    /// there is no data-loss path either way; a caller that needs to tell a user WHY should re-run
    /// <see cref="FilePathValidation.IsValidFileName(string?, out string?)"/>, which hands back the
    /// message, rather than growing a second validation path.
    /// </para>
    /// <para>
    /// ABSENT IS NOT THE SAME AS CREATABLE, and the caller still has to handle a failed create.
    /// Two reasons. The suffix lengthens the name, so a 255-character name at the filesystem's
    /// component limit comes back 259 characters long and cannot be created; no clamp is applied
    /// here because a correct one needs destination knowledge this function deliberately refuses to
    /// guess — ext4 counts 255 BYTES, so a UTF-8 name can blow the limit at half that many
    /// characters. And <paramref name="existingNames"/> is a snapshot, so create with
    /// <c>FileMode.CreateNew</c> rather than <c>File.Create</c>, which truncates. RemEx-cirk.
    /// </para>
    /// </returns>
    public static string? NextAvailableName(
        string? fileName,
        IReadOnlyCollection<string> existingNames,
        bool caseSensitive)
    {
        Guard.NotNull(existingNames);

        if (!FilePathValidation.IsValidFileName(fileName, out _)) return null;

        // Non-null once validation passes; IsValidFileName rejects null and empty.
        var name = fileName!;

        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var taken = new HashSet<string>(existingNames, comparer);

        if (!taken.Contains(name)) return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        // Only split where the split leaves something to suffix and loses nothing. A dotfile
        // (".gitignore") is all extension and no stem, so the naive form yields " (2).gitignore" —
        // a leading space, and a name no file manager on either platform produces; "..gitignore"
        // leaves a stem of "." for the same reason, hence Trim rather than a length check. A
        // trailing dot ("report.") loses the dot, which is a DIFFERENT file on a case-sensitive
        // volume. In either case suffix the whole name instead and keep it intact.
        if (stem.Trim('.').Length == 0 || !string.Equals(stem + extension, name, StringComparison.Ordinal))
        {
            stem = name;
            extension = string.Empty;
        }

        for (int n = 2; n <= MaxSuffix; n++)
        {
            var candidate = $"{stem} ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}";
            if (!taken.Contains(candidate)) return candidate;
        }

        return null;
    }
}
