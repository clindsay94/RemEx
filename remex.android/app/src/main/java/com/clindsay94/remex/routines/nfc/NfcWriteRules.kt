package com.clindsay94.remex.routines.nfc

/** What the write sheet read from a tag before touching it (spec 1.5, §8.3.2). Pure JVM. */
data class NfcTagInspection(
    val kind: Kind,
    val writable: Boolean,
    /** Usable NDEF bytes, or -1 when the tag does not say (an unformatted tag). */
    val maxSize: Int,
    /** The first URI record already on the tag, if any. */
    val existingUri: String?,
) {
    enum class Kind { NDEF, FORMATABLE, UNSUPPORTED }
}

enum class NfcWriteOutcome { WRITTEN, LOST_CONTACT, READ_ONLY, TOO_SMALL }

/** Every row of the write flow's state table (spec 1.5, R-UX-14). */
sealed interface NfcWriteState {
    data object NoHardware : NfcWriteState

    data object Off : NfcWriteState

    data object Waiting : NfcWriteState

    /** "This tag runs <other>. Replace it with <this>?" */
    data class ExistingTag(val otherRoutineId: String, val otherName: String) : NfcWriteState

    data object Writing : NfcWriteState

    data object Success : NfcWriteState

    data object ReadOnly : NfcWriteState

    data object TooSmall : NfcWriteState

    data object LostContact : NfcWriteState

    /** "Test it": waiting for a tap; nothing runs during a test tap. */
    data object TestWaiting : NfcWriteState

    data object TestSuccess : NfcWriteState

    data object TestFailed : NfcWriteState
}

enum class NfcHaptic { NONE, CONFIRM, REJECT }

/** The write sheet's reducer (spec 1.5). No Android types, so every row is reachable in a unit test. */
object NfcWriteRules {
    fun initial(hasHardware: Boolean, enabled: Boolean): NfcWriteState =
        when {
            !hasHardware -> NfcWriteState.NoHardware
            !enabled -> NfcWriteState.Off
            else -> NfcWriteState.Waiting
        }

    /** The checks before a write (spec 1.5, R-SYS-41): read-only and too-small tags are refused first. */
    sealed interface Preflight {
        data object Write : Preflight

        data class Stop(val state: NfcWriteState) : Preflight
    }

    /**
     * @param routineId the routine this sheet writes a tag for.
     * @param replaceConfirmedFor the other routine the person already agreed to replace.
     * @param routineName a routine's name on THIS phone, or null when this phone has no such routine
     *   (another phone's tag, or one for a deleted routine, is overwritten without asking).
     */
    fun preflight(
        inspection: NfcTagInspection,
        routineId: String,
        payloadBytes: Int,
        replaceConfirmedFor: String?,
        routineName: (String) -> String?,
    ): Preflight {
        if (inspection.kind == NfcTagInspection.Kind.UNSUPPORTED || !inspection.writable) return Preflight.Stop(NfcWriteState.ReadOnly)
        if (inspection.maxSize in 0 until payloadBytes) return Preflight.Stop(NfcWriteState.TooSmall)
        val existing = NfcRoutineTag.parse(inspection.existingUri)
        if (existing != null && existing.routineId != routineId && existing.routineId != replaceConfirmedFor) {
            val name = routineName(existing.routineId)
            if (name != null) return Preflight.Stop(NfcWriteState.ExistingTag(existing.routineId, name))
        }
        return Preflight.Write
    }

    fun afterWrite(outcome: NfcWriteOutcome): NfcWriteState =
        when (outcome) {
            NfcWriteOutcome.WRITTEN -> NfcWriteState.Success
            NfcWriteOutcome.LOST_CONTACT -> NfcWriteState.LostContact
            NfcWriteOutcome.READ_ONLY -> NfcWriteState.ReadOnly
            NfcWriteOutcome.TOO_SMALL -> NfcWriteState.TooSmall
        }

    fun afterTestRead(matches: Boolean): NfcWriteState = if (matches) NfcWriteState.TestSuccess else NfcWriteState.TestFailed

    /** Which states listen for a tag. */
    fun readsTags(state: NfcWriteState): Boolean = state == NfcWriteState.Waiting || state == NfcWriteState.TestWaiting

    fun haptic(state: NfcWriteState): NfcHaptic =
        when (state) {
            NfcWriteState.Success, NfcWriteState.TestSuccess -> NfcHaptic.CONFIRM
            NfcWriteState.ReadOnly, NfcWriteState.TooSmall, NfcWriteState.LostContact, NfcWriteState.TestFailed -> NfcHaptic.REJECT
            else -> NfcHaptic.NONE
        }
}
