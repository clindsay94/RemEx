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
    /// Resolves, and re-resolves case-insensitively if the filesystem contradicts the first answer
    /// (RemEx-2knx).
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THE LIVELOCK THIS ENDS WAS DETERMINISTIC, NOT A RACE.** <c>HostFileSystemIsCaseSensitive</c>
    /// is <c>!IsWindows()</c>, so on Linux the name search compares Ordinal. On a case-INSENSITIVE
    /// mount under a Linux host — SMB, exFAT, ntfs-3g, ciopfs — a directory holding <c>b.txt</c> and
    /// <c>B (2).txt</c> resolves keep-both to <c>b (2).txt</c>, because that is genuinely absent
    /// Ordinal-wise. The mount then reports it as existing, the caller throws, the user retries, and
    /// the same name is chosen again. Every time. Only "skip" ended it.
    /// </para>
    /// <para>
    /// **THE FIX BELONGS HERE AND NOT IN THE NAME SEARCH, WHICH IS WHERE IT WAS TRIED FIRST AND
    /// REVERTED.** Making the search judge its generated candidates under both comparisons converges
    /// too, and is wrong: <c>FileConflictNamingTests.CaseSensitivityAppliesToTheSuffixedCandidatesToo</c>
    /// pins the opposite deliberately, mutation-proven, because on ext4 <c>REPORT (2).PDF</c> is a
    /// different file and <c>report (2).pdf</c> is a name the user is entitled to. That change would
    /// have taken it from every Linux user to protect the minority on an odd mount.
    /// </para>
    /// <para>
    /// WHAT IT GUARANTEES IS TERMINATION, NOT A NEW NAME. The second pass finds one whenever the
    /// mount folds the way <c>OrdinalIgnoreCase</c> does, which covers every ASCII case and the
    /// reported reproduction. Where it does not — exFAT's dotless i, a KELVIN SIGN, a ciopfs mount
    /// under a Turkish locale — the retry is detected and turned into a plain refusal instead, so
    /// the user gets Replace and Skip rather than the same rejected name a second time.
    /// </para>
    /// <para>
    /// THE SIGNAL IS CHEAP AND NEEDS NO CASE-SENSITIVITY PROBE, which is the distinction that
    /// matters — not that it is free, which an earlier draft of this claimed and which review
    /// corrected. It costs one existence check per keep-both rename, and the caller was about to
    /// make that same check anyway. What it does NOT do is the thing the remarks below argue
    /// against: creating a file to interrogate the mount. It waits for the mount to contradict the
    /// snapshot, which is proof rather than inference, and only then re-resolves. On ext4 the
    /// contradiction never happens and this is exactly the old behaviour.
    /// </para>
    /// <para>
    /// ONLY AN INVENTED NAME IS RECONSIDERED. When <see cref="ConflictResolutionPlan.ResolvedName"/>
    /// is null the destination is the one the user asked for, and quietly moving their file somewhere
    /// else because a name they chose is taken would be a different and worse behaviour than the
    /// error they get today.
    /// </para>
    /// </remarks>
    /// <param name="isTaken">
    /// Asks the real filesystem. Passed in rather than called here so this stays testable off-disk —
    /// the interesting case is a Linux host on a case-blind mount, which no Windows dev box can
    /// reproduce and which a hardcoded <c>File.Exists</c> would put out of reach.
    /// <para>
    /// A FILE OR A DIRECTORY, WITHOUT THE OVERWRITE EXEMPTION the call sites apply to a name the user
    /// chose. That is not an oversight: keep-both never overwrites, so a plan carrying a
    /// <c>ResolvedName</c> always has <c>Overwrite: false</c>, and an invented name is blocked by
    /// anything sitting on it. Both call sites can therefore pass the same predicate.
    /// </para>
    /// </param>
    public static ConflictResolutionPlan ResolveAllowingForACaseBlindMount(
        string? conflictResolution,
        string requestedDestination,
        string rootPath,
        bool overwriteRequested,
        Func<string, IReadOnlyList<string>> listDirectory,
        bool caseSensitive,
        Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);

        var plan = Resolve(
            conflictResolution, requestedDestination, rootPath, overwriteRequested, listDirectory, caseSensitive);

        // Nothing to reconsider: the search already compared insensitively, or it did not invent a
        // name, or the filesystem agrees with it.
        if (!caseSensitive || plan.ResolvedName is null || !isTaken(plan.DestinationPath))
        {
            return plan;
        }

        // The mount disagreed with the listing, so the listing's comparison was the wrong one for it.
        var relaxed = Resolve(
            conflictResolution, requestedDestination, rootPath, overwriteRequested, listDirectory, caseSensitive: false);

        // AND IF THE SECOND PASS COULD NOT IMPROVE ON THE FIRST, STOP RATHER THAN INSIST. Two ways
        // that happens, and review found both:
        //
        // It returns the SAME name when the mount's own fold is not the one OrdinalIgnoreCase
        // implements. exFAT folds U+0131 (dotless i) onto 'I' and .NET does not, so a directory
        // holding "i.txt" and "I (2).txt" gets "i (2).txt" from BOTH passes - and handing that back
        // is the original livelock with an extra step. U+212A KELVIN and a ciopfs mount under a
        // Turkish locale are the same shape. (Also the benign case: the name was taken by a racer
        // rather than by a case-variant, so it is not in the listing either pass read.)
        //
        // It returns NO name when the insensitive pass exhausts the suffix range that the sensitive
        // one did not, because folding collapses more siblings into "taken". That plan carries the
        // caller's own overwrite flag at the caller's own destination - so a client sending keep_both
        // AND the legacy overwrite:true would have the file it asked to keep destroyed. Keep-both's
        // one promise is that nothing is destroyed, and it has to survive this path failing.
        //
        // Either way the answer is a refusal at the requested name with overwrite OFF. That is a
        // terminating outcome rather than a converging one: the caller raises the ordinary
        // file-exists conflict, which offers Replace and Skip, instead of offering an invented name
        // the mount has already rejected once.
        // THE SECOND ANSWER IS CHECKED TOO, and leaving it unchecked was a third way to loop that the
        // tests caught. If the mount rejects the relaxed name as well, handing it back just moves the
        // livelock along by one name: the caller throws, the user retries, and both passes produce
        // the same pair again. One extra existence check, paid only on a mount that has already
        // contradicted us once, buys termination outright.
        if (relaxed.ResolvedName is null
            || string.Equals(relaxed.DestinationPath, plan.DestinationPath, StringComparison.Ordinal)
            || isTaken(relaxed.DestinationPath))
        {
            return new ConflictResolutionPlan(Overwrite: false, DestinationPath: requestedDestination, ResolvedName: null);
        }

        return relaxed;
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
