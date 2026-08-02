package com.clindsay94.remex.service

/**
 * Wire values the host sends and accepts for a filename collision (RemEx-agpn).
 *
 * Mirrors `Remex.Core.Models.FileTransferErrorCodes` and `FileConflictResolutions` VERBATIM. These
 * are protocol tokens, not prose — the whole point of the host change that introduced them is that
 * a client can branch on something that is never translated.
 */
object FileConflictCodes {
    const val DESTINATION_EXISTS = "destination_exists"
    const val DESTINATION_IS_DIFFERENT_KIND = "destination_is_different_kind"

    /**
     * The host renamed for "keep both" and the destination refused the new name (RemEx-cirk).
     *
     * **KNOWN EXPLICITLY RATHER THAN FALLING THROUGH TO THE UNKNOWN-CODE BRANCH.** Skip is the only
     * sensible answer either way, so the ACTIONS would have been the same - but the unknown branch
     * also leaves the sheet asserting the wrong cause, and this arrives at the worst moment for
     * that: after the user has already chosen Keep both.
     */
    const val RESOLVED_NAME_UNUSABLE = "resolved_name_unusable"
}

object FileConflictResolutions {
    const val REPLACE = "replace"
    const val KEEP_BOTH = "keep_both"
}

/** What the user can do about one collision. */
enum class ConflictAction {
    /** Overwrite what is there. Never offered when the destination is a different kind of thing. */
    Replace,

    /** Let the host pick the next free name. The phone never composes it. */
    KeepBoth,

    /** Do nothing for this item and carry on. Always available, and the fail-closed default. */
    Skip,
}

/**
 * Decides which answers a collision may be offered, and remembers one across a batch (RemEx-agpn).
 *
 * **PURE, AND SEPARATE FROM THE SHEET, because the decisions are where a mistake costs a file.**
 * Offering "Replace" for a collision whose replace deletes a directory tree, or letting an
 * apply-to-all Replace leak past the batch it was given for, are both data loss — and neither is
 * visible in a screenshot of the sheet.
 */
object FileConflictPolicy {

    /**
     * The actions worth offering for [errorCode] on [operation].
     *
     * @param operation one of `FileManageOperations`.
     */
    fun actionsFor(errorCode: String?, operation: String): List<ConflictAction> {
        // Not a collision at all. A sheet must not open for an ordinary failure - "Replace" is a
        // meaningless answer to "the disk is full", and offering it invites a retry that cannot work.
        if (errorCode == null) return emptyList()

        // MKDIR IS REFUSAL-ONLY, and the host says so: CreateDirectoryAsync emits a code but accepts
        // no conflictResolution, so a Replace or Keep both retry re-fails identically. Offering them
        // produces a dead-end loop where the user answers the same question forever. The code still
        // earns its place - it says WHY the mkdir failed - and Skip remains a real answer.
        if (operation == FileManageOperations.MKDIR) return listOf(ConflictAction.Skip)

        return when (errorCode) {
            // KEEP BOTH FIRST, REPLACE SECOND. Review caught the original order putting the
            // destructive answer at the top: de-weighting its emphasis is not enough, because
            // POSITION is the stronger recommendation and the top button is the one a hurried user
            // taps. The order here is the order the sheet renders.
            FileConflictCodes.DESTINATION_EXISTS ->
                listOf(ConflictAction.KeepBoth, ConflictAction.Replace, ConflictAction.Skip)

            // NO REPLACE. Here the destination is the OTHER KIND of thing, so replacing means
            // deleting a whole directory tree to make room for one file, or deleting a file to make
            // a folder. Nobody intends either from a copy and nothing undoes them. The host refuses
            // it outright, so the button would not even work - but the reason it is withheld is that
            // it should never have been offered.
            FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND ->
                listOf(ConflictAction.KeepBoth, ConflictAction.Skip)


            // SKIP ALONE FOR EVERYTHING ELSE, INCLUDING RESOLVED_NAME_UNUSABLE. An explicit branch
            // for that code was written here and removed: the else already yields Skip, so no mutant
            // could kill it, and an unkillable branch reads to a later maintainer as though it
            // decides something. The code IS known to this client - the sheet gives it its own
            // explanation - but the ACTIONS genuinely are the same, and for a good reason: the host
            // already tried the rename and the destination refused the name it chose, so retrying
            // keep-both asks it to choose again from the same too-long stem, and replace was
            // declined a moment ago. Nothing is left that could work.
            //
            // A CODE THIS CLIENT DOES NOT KNOW. A newer host may send one, and guessing which
            // actions it permits is how a client offers a destructive button for a situation it does
            // not understand. Skip is the only answer that is safe against every possible meaning.
            else -> listOf(ConflictAction.Skip)
        }
    }

    /** The wire value for [action], or null when the action sends no retry at all. */
    fun resolutionFor(action: ConflictAction): String? = when (action) {
        ConflictAction.Replace -> FileConflictResolutions.REPLACE
        ConflictAction.KeepBoth -> FileConflictResolutions.KEEP_BOTH

        // SKIP SENDS NOTHING. It is not a resolution the host understands - it is the client
        // declining to retry, which is why it can never fail and why it is the safe default.
        ConflictAction.Skip -> null
    }
}

/**
 * One batch's answer to "apply to all remaining" (RemEx-agpn).
 *
 * **SCOPED TO A BATCH BY CONSTRUCTION.** A remembered Replace that outlived the operation it was
 * given for would overwrite a file in some later, unrelated copy that the user was never asked
 * about — so this is created per batch and discarded with it, rather than living on the ViewModel
 * where forgetting to clear it is a silent data-loss bug.
 */
class BatchConflictChoice {
    private var remembered: ConflictAction? = null

    /** The answer to reuse without asking, or null when the user must be asked. */
    val standingAnswer: ConflictAction? get() = remembered

    /**
     * Records [action] for the rest of this batch.
     *
     * @param applyToAll false for a one-off answer, which is the default and changes nothing.
     */
    fun remember(action: ConflictAction, applyToAll: Boolean) {
        if (applyToAll) remembered = action
    }

    /**
     * Whether [action] can stand in for a collision reporting [errorCode] on [operation].
     *
     * **A STANDING ANSWER IS STILL CHECKED AGAINST EACH COLLISION, and that is the point.** A user
     * who chose "Replace, apply to all" for a batch of ordinary collisions has not agreed to delete
     * a directory tree when the next item turns out to be a different kind of thing — a remembered
     * answer that bypassed the offer list would do exactly that, silently, on an item they never saw.
     */
    fun canApply(action: ConflictAction, errorCode: String?, operation: String): Boolean =
        action in FileConflictPolicy.actionsFor(errorCode, operation)
}
