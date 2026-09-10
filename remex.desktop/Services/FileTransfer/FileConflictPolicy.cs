using Remex.Core.Models;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>What the person at the PC can do about one filename collision.</summary>
public enum FileConflictAction
{
    /// <summary>Overwrite what is there. Never offered when the destination is a different kind of thing.</summary>
    Replace,

    /// <summary>Let the host pick the next free name. The PC never composes it.</summary>
    KeepBoth,

    /// <summary>Leave this item alone and carry on with the rest of the paste.</summary>
    Skip,

    /// <summary>Abandon the whole paste, including the items not reached yet.</summary>
    Cancel,
}

/// <summary>
/// Decides which answers a collision may be offered on the PC side.
/// </summary>
/// <remarks>
/// <para>
/// **A C# MIRROR OF <c>FileConflictPolicy.kt</c>, DELIBERATELY, AND NOT A NEW DIALECT.** The wire
/// vocabulary is <see cref="FileTransferErrorCodes"/> and <see cref="FileConflictResolutions"/> in
/// <c>remex.core</c>; both clients read the same constants and reach the same offer lists from
/// them. A PC that offered a different set for the same code would mean the two clients disagree
/// about which answers are SAFE, and every disagreement of that kind resolves in favour of data
/// loss on one of them.
/// </para>
/// <para>
/// **PURE, AND SEPARATE FROM THE VIEW MODEL, because the decisions are where a mistake costs a
/// file.** Offering Replace for a collision whose replace deletes a directory tree is not visible
/// in a screenshot of the prompt, and nothing undoes it.
/// </para>
/// <para>
/// NO <c>mkdir</c> BRANCH, unlike the Kotlin. The phone's sheet is reached from every manage
/// operation; this prompt is reached only from paste, so a branch for mkdir here would be a rule
/// no call site can exercise and no mutant can kill — the shape this repo's own sweeps flag
/// (RemEx-thwlr). <c>MakeDirectoryRemoteAsync</c> keeps throwing, which is refusal-only by another
/// route.
/// </para>
/// </remarks>
public static class FileConflictPolicy
{
    /// <summary>The actions worth offering for <paramref name="errorCode"/>, in render order.</summary>
    /// <remarks>
    /// KEEP BOTH FIRST, REPLACE SECOND, matching the phone. Review caught the Android ordering
    /// putting the destructive answer at the top, and position is a stronger recommendation than
    /// any amount of de-emphasis: the first button is the one a hurried user presses.
    /// </remarks>
    public static IReadOnlyList<FileConflictAction> ActionsFor(string? errorCode)
    {
        // Not a collision at all. An ordinary failure — the disk filled up, the folder went
        // read-only — has no answer a prompt could collect, and offering "Replace" for one invites
        // a retry that cannot work.
        if (string.IsNullOrWhiteSpace(errorCode))
            return [];

        return errorCode switch
        {
            FileTransferErrorCodes.DestinationExists =>
                [FileConflictAction.KeepBoth, FileConflictAction.Replace, FileConflictAction.Skip, FileConflictAction.Cancel],

            // KEEP BOTH AND SKIP, NEVER REPLACE. The name is taken, so asking again genuinely works
            // — the host re-lists the directory and picks the next free name. But Replace answers
            // the ORIGINAL request, overwriting the destination the user first named while the
            // prompt is showing the sibling the host invented. A user who chose keep-both precisely
            // to preserve that file would destroy it by answering a question about a different one.
            FileTransferErrorCodes.ResolvedNameTaken =>
                [FileConflictAction.KeepBoth, FileConflictAction.Skip, FileConflictAction.Cancel],

            // NO REPLACE. The destination is the OTHER KIND of thing, so replacing means deleting a
            // whole directory tree to make room for one file, or deleting a file to make a folder.
            // Nobody intends either from a paste and nothing undoes them; the host refuses it
            // outright, so the button would not even work — but the reason it is withheld is that
            // it should never have been offered.
            FileTransferErrorCodes.DestinationIsDifferentKind =>
                [FileConflictAction.KeepBoth, FileConflictAction.Skip, FileConflictAction.Cancel],

            // SKIP OR CANCEL FOR EVERYTHING ELSE, INCLUDING resolved_name_unusable. That code gets
            // its own SENTENCE (see FileConflictPrompt) but not its own branch, because the actions
            // genuinely are the same and an explicit case would be unkillable by any mutant — the
            // exact reasoning the Kotlin records for deleting its own copy of it. The host already
            // tried the rename and the destination refused the name it chose, so asking it to
            // choose again from the same too-long stem cannot work, and Replace was declined a
            // moment ago.
            //
            // A CODE THIS BUILD DOES NOT KNOW lands here too. Guessing which actions a newer host
            // permits is how a client offers a destructive button for a situation it does not
            // understand.
            _ => [FileConflictAction.Skip, FileConflictAction.Cancel],
        };
    }

    /// <summary>The wire value for <paramref name="action"/>, or null when it sends no retry at all.</summary>
    public static string? ResolutionFor(FileConflictAction action) => action switch
    {
        FileConflictAction.Replace => FileConflictResolutions.Replace,
        FileConflictAction.KeepBoth => FileConflictResolutions.KeepBoth,

        // SKIP AND CANCEL SEND NOTHING. Neither is a resolution the host understands — they are the
        // PC declining to retry, which is why neither can fail and why Skip is the safe default.
        _ => null,
    };
}
