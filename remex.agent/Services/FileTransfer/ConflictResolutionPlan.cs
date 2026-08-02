using Remex.Core.Models;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// What a requested conflict resolution turns into (RemEx-6vd8).
/// </summary>
/// <param name="Overwrite">Whether the operation should replace what is there.</param>
/// <param name="DestinationPath">
/// The destination to actually use — the requested one, or a renamed sibling for "keep both".
/// </param>
/// <param name="ResolvedName">
/// The new bare name when it was changed, else null. Null means "what you asked for is what you
/// got", which is what the response reports back.
/// </param>
public readonly record struct ConflictResolutionPlan(
    bool Overwrite,
    string DestinationPath,
    string? ResolvedName);

/// <summary>
/// Turns a client's answer to a filename collision into a concrete destination (RemEx-6vd8).
/// </summary>
/// <remarks>
/// <para>
/// **THE HOST PICKS THE NAME, NEVER THE CLIENT, AND THIS IS WHERE THAT IS ENFORCED.** If the phone
/// composed "report (2).pdf" itself, one of two things happens: the host picks differently and the
/// user is shown one filename while getting another, or the guess collides too and the operation
/// fails again with the same opaque error the whole feature exists to replace. Only the host can see
/// what else is in that directory.
/// </para>
/// <para>
/// **CASE SENSITIVITY IS DECIDED HERE, FROM THE HOST'S OWN OS, AND HAS NO DEFAULT.** On Windows
/// <c>Report.pdf</c> and <c>report.pdf</c> are the same file; on Linux they are two. A single rule is
/// wrong on one of them — it either skips a free name on Linux or hands back one that still collides
/// on Windows. The phone cannot answer this question about a machine it is not running on, which is
/// the second reason the naming cannot live on the client.
/// </para>
/// </remarks>
public static class ConflictResolver
{
    /// <summary>
    /// Resolves <paramref name="conflictResolution"/> against the real destination directory.
    /// </summary>
    /// <param name="conflictResolution">
    /// One of <see cref="FileConflictResolutions"/>, or null/blank for "no answer given".
    /// </param>
    /// <param name="requestedDestination">The absolute destination path the client asked for.</param>
    /// <param name="overwriteRequested">The legacy <c>overwrite</c> flag, still honoured.</param>
    /// <param name="listDirectory">
    /// Returns the bare names already present in a directory. Injected so the naming rule is testable
    /// without a filesystem — the decision is what matters, and it is what a mistake misleads with.
    /// </param>
    /// <param name="caseSensitive">
    /// Whether the host's filesystem distinguishes case. No default, deliberately: see the remarks
    /// on this class.
    /// </param>
    /// <param name="rootPath">
    /// The shared root the destination has already been confined to. The renamed sibling must land
    /// strictly INSIDE it — see the remarks on the escape this closes.
    /// </param>
    public static ConflictResolutionPlan Resolve(
        string? conflictResolution,
        string requestedDestination,
        string rootPath,
        bool overwriteRequested,
        Func<string, IReadOnlyList<string>> listDirectory,
        bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(listDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        // NO ANSWER MEANS THE OLD BEHAVIOUR, NOT A GUESS. A client that predates this field, or one
        // that simply has not asked the user yet, must get exactly what it got before: the operation
        // fails and reports a collision. Inventing a resolution here would silently overwrite or
        // rename files for every client that never opted in.
        if (string.IsNullOrWhiteSpace(conflictResolution))
            return new ConflictResolutionPlan(overwriteRequested, requestedDestination, null);

        if (conflictResolution.Equals(FileConflictResolutions.Replace, StringComparison.Ordinal))
            return new ConflictResolutionPlan(true, requestedDestination, null);

        if (!conflictResolution.Equals(FileConflictResolutions.KeepBoth, StringComparison.Ordinal))
        {
            // AN UNRECOGNISED VALUE FALLS BACK TO THE REFUSAL, NOT TO A BEST GUESS. A future client
            // sending a resolution this host does not implement must be told no, because the two
            // things it could mean — replace and rename — are the two outcomes a user would most
            // want to have been asked about.
            return new ConflictResolutionPlan(overwriteRequested, requestedDestination, null);
        }

        var directory = Path.GetDirectoryName(requestedDestination);
        var requestedName = Path.GetFileName(requestedDestination);

        // A destination with no directory part is not something this can reason about, and guessing
        // a sibling name for it would put the file somewhere the caller did not ask for.
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(requestedName))
            return new ConflictResolutionPlan(overwriteRequested, requestedDestination, null);

        // THE SIBLING'S DIRECTORY MUST BE INSIDE THE ROOT, AND REVIEW PROVED WHY THIS IS NOT
        // REDUNDANT WITH THE CALLER'S CONFINEMENT. ResolveWithinRoot deliberately maps "", "/", "."
        // and "x/.." to THE ROOT ITSELF — all legitimate ways to name it. When the destination IS
        // the root, its parent is the root's PARENT, which no check ever saw: renaming a sibling of
        // "Shared" produces "Shared (2)" next to it, outside the share entirely. A copy would write
        // a stray file there; a move would relocate the whole tree out of the share.
        //
        // Confinement is therefore re-established here rather than assumed, because the property the
        // caller enforced ("the destination is within the root") does not imply the one this needs
        // ("the destination's PARENT is within the root").
        if (!IsWithinRoot(directory, rootPath))
            return new ConflictResolutionPlan(overwriteRequested, requestedDestination, null);

        var existing = listDirectory(directory);
        var chosen = FileConflictNaming.NextAvailableName(requestedName, existing, caseSensitive);

        // NO FREE NAME MEANS FALL BACK TO THE REFUSAL, NOT TO A FORCED ONE. NextAvailableName gives
        // up after 10,000 suffixes, and the honest response to that is the collision error the
        // client already knows how to show — the same answer it would have got without asking. The
        // alternatives are both worse: overwriting silently destroys a file the user asked to keep,
        // and appending a name anyway puts back the collision this exists to remove.
        if (chosen is null)
            return new ConflictResolutionPlan(overwriteRequested, requestedDestination, null);

        // KEEP BOTH NEVER OVERWRITES, even if the caller also set the legacy flag. The two fields can
        // disagree, and of the two possible readings only one is safe: the user asked to keep the
        // existing file, so nothing may be destroyed to satisfy it.
        return new ConflictResolutionPlan(
            Overwrite: false,
            DestinationPath: Path.Combine(directory, chosen),

            // Null when the name did not actually change, so the response only reports a renamed
            // file when there was one. Reporting "resolved to report.pdf" for a request that asked
            // for report.pdf is noise the UI would have to filter out again.
            ResolvedName: string.Equals(chosen, requestedName, StringComparison.Ordinal) ? null : chosen);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the root or lives beneath it.
    /// </summary>
    /// <remarks>
    /// Compared with the separator appended, so <c>C:\SharedOther</c> is not treated as being inside
    /// <c>C:\Shared</c> — a plain <c>StartsWith</c> is the classic way this check is written wrong.
    /// Case handling follows the host OS, matching how the rest of the file confines paths.
    /// </remarks>
    private static bool IsWithinRoot(string candidate, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string normalizedCandidate, normalizedRoot;
        try
        {
            normalizedCandidate = Normalize(candidate);
            normalizedRoot = Normalize(rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path this cannot even normalize is one it must not vouch for.
            return false;
        }

        return normalizedCandidate.Equals(normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Whether this host's filesystem distinguishes <c>Report.pdf</c> from <c>report.pdf</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the OS rather than probed, which is the pragmatic answer rather than the perfect
    /// one: a case-insensitive mount on Linux, or a case-sensitive directory on Windows, both exist
    /// and both would be judged wrongly here. Probing means creating a file to find out, and a
    /// readiness-style side effect inside a copy is worse than the error it would prevent. The
    /// failure mode is benign in the common direction — on Windows we may skip a name that was
    /// actually free, producing "report (2).pdf" where "Report.pdf" would have done.
    /// </remarks>
    public static bool HostFileSystemIsCaseSensitive => !OperatingSystem.IsWindows();
}
