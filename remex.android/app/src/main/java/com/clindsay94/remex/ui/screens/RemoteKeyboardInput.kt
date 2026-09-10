package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.text.input.InputTransformation
import androidx.compose.foundation.text.input.TextFieldBuffer
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue

private const val REMOTE_KEYBOARD_BUFFER_LIMIT = 256
private const val REMOTE_KEYBOARD_BUFFER_TAIL = 128
private const val KEYCODE_BACKSPACE = 8
private const val KEYCODE_ENTER = 13

/**
 * Builds the [InputTransformation] that drives the remote keyboard from local edits.
 *
 * RemEx-d459: replaces `RemoteMouseScreen`'s `BasicTextField(value/onValueChange)` +
 * `applyRemoteKeyboardEdit(TextFieldValue, TextFieldValue, ...)` pairing with
 * `BasicTextField(state=)` + this transform, now that field consumes edit deltas directly from a
 * [TextFieldBuffer] instead of diffing two whole `TextFieldValue`s handed back and forth.
 *
 * The `TextFieldValue` overload of [applyRemoteKeyboardEdit] below is KEPT, not deleted as the bead
 * originally called for: `RemoteDesktopScreen.kt` (its chord-modifier keyboard field, around line
 * 1055) has its own independent `BasicTextField(value=)` calling that exact overload, which was not
 * mentioned in this bead's scope and has no characterization tests of its own. Migrating it blind
 * was judged out of scope - see the bead for the writeup.
 */
internal fun remoteKeyboardInputTransformation(
        onSendText: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit
): InputTransformation = InputTransformation {
    // InputTransformation's SAM is `TextFieldBuffer.() -> Unit` in this Compose Foundation version
    // (confirmed by compiling against it) rather than the `(TextFieldBuffer) -> Unit` shape some
    // published docs still describe - `this` here is the buffer.
    applyRemoteKeyboardEdit(this, onSendText, onSendKeyPress)
}

/**
 * Diffs [buffer]'s pre-edit text ([TextFieldBuffer.originalText]) against its pending post-edit
 * text ([TextFieldBuffer.asCharSequence]), emits the backspace/text/enter events that reproduce the
 * edit on the remote host, and collapses the local caret to the end of the buffer.
 *
 * Deliberately does NOT cap the buffer length - see [trimRemoteKeyboardBufferIfIdle]. The IME
 * composition range is a PAIR OF OFFSETS INTO THE TEXT; trimming characters off the head while a
 * composition is active leaves those offsets pointing at positions that no longer exist. This is a
 * PINNED invariant carried over from the pre-migration diff (RemEx-d459 note 3), not a new judgement
 * call: the trim must stay suppressed while composing. `TextFieldBuffer` does not expose the
 * composition range to `InputTransformation` publicly, so the cap cannot be applied here at all - it
 * runs from a separate, state-level site that CAN see composition instead ([trimRemoteKeyboardBufferIfIdle]),
 * keeping this transform pure delta-consumption.
 */
internal fun applyRemoteKeyboardEdit(
        buffer: TextFieldBuffer,
        onSendText: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit
) {
    val oldText = buffer.originalText.toString()
    val nextText = buffer.asCharSequence().toString()

    if (oldText != nextText) {
        val prefixLength = commonPrefixLength(oldText, nextText)
        val suffixLength = commonSuffixLength(oldText, nextText, prefixLength)

        val removedCount = oldText.length - prefixLength - suffixLength
        repeat(removedCount.coerceAtLeast(0)) { onSendKeyPress(KEYCODE_BACKSPACE) }

        val insertedEnd = nextText.length - suffixLength
        if (insertedEnd >= prefixLength) {
            emitInsertedText(nextText.substring(prefixLength, insertedEnd), onSendText, onSendKeyPress)
        }
    }

    buffer.selection = TextRange(nextText.length)
}

/**
 * Trims [state]'s buffer to its tail once it has grown past [REMOTE_KEYBOARD_BUFFER_LIMIT], unless
 * [isComposing] - which defaults to reading [TextFieldState.composition] directly, the production
 * path. Call from a reactive site (e.g. `snapshotFlow` on `state.text`/`state.composition`), not a
 * polling loop: this function does one length check and, at most, one buffer edit per call.
 *
 * RemEx-d459 follow-up: moved out of [applyRemoteKeyboardEdit] because `TextFieldBuffer` cannot see
 * composition. `TextFieldState.composition` is public, so the guard lives here instead, observing
 * state that spans the whole composing lifetime rather than one `InputTransformation` call. The
 * asymmetry this preserves is intentional (pinned pre-migration, RemEx-d459 note 3): on the normal
 * (non-composing) path the buffer is trimmed and the caret collapses to the end; while composing,
 * nothing here touches the buffer at all, exactly like the old `TextFieldValue` path left composition
 * untouched while still collapsing selection.
 *
 * The [isComposing] parameter exists because there is no public way to drive a real `TextFieldState`
 * into a composing state outside an actual IME session - production always uses the default.
 */
internal fun trimRemoteKeyboardBufferIfIdle(
        state: TextFieldState,
        isComposing: Boolean = state.composition != null
) {
    if (isComposing || state.text.length <= REMOTE_KEYBOARD_BUFFER_LIMIT) return

    state.edit {
        replace(0, length - REMOTE_KEYBOARD_BUFFER_TAIL, "")
        selection = TextRange(length)
    }
}

/**
 * Original `TextFieldValue`-based diff. The only remaining caller is `RemoteDesktopScreen.kt`'s
 * chord-modifier keyboard field (`BasicTextField(value=)` around line 1055, RemEx-yi8o) - its
 * `singleCharacterInsertion` helper and the modifier-chord routing built around this call have NO
 * characterization tests of their own. Migrating THAT call site to `InputTransformation` requires
 * writing tests for it FIRST, the way `RemoteKeyboardEditTest.kt` pinned this function's behaviour
 * before RemEx-d459 touched it - do not migrate blind.
 *
 * This overload itself keeps a small, deliberately narrow set of tests in that same file (the
 * "legacy TextFieldValue overload" section) covering only its two guards - the returned text
 * becoming the next diff's baseline, and the composition-suppressed trim - so it does not regress
 * silently just because it has no new callers. Do not add new callers; new UI should use
 * [remoteKeyboardInputTransformation].
 */
internal fun applyRemoteKeyboardEdit(
        currentValue: TextFieldValue,
        newValue: TextFieldValue,
        onSendText: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit
): TextFieldValue {
    val oldText = currentValue.text
    val nextText = newValue.text

    if (oldText != nextText) {
        val prefixLength = commonPrefixLength(oldText, nextText)
        val suffixLength = commonSuffixLength(oldText, nextText, prefixLength)

        val removedCount = oldText.length - prefixLength - suffixLength
        repeat(removedCount.coerceAtLeast(0)) { onSendKeyPress(KEYCODE_BACKSPACE) }

        val insertedEnd = nextText.length - suffixLength
        if (insertedEnd >= prefixLength) {
            emitInsertedText(nextText.substring(prefixLength, insertedEnd), onSendText, onSendKeyPress)
        }
    }

    val normalizedValue =
            newValue.copy(selection = TextRange(nextText.length), composition = newValue.composition)

    return trimKeyboardBufferIfNeeded(normalizedValue)
}

private fun trimKeyboardBufferIfNeeded(value: TextFieldValue): TextFieldValue {
    if (value.composition != null || value.text.length <= REMOTE_KEYBOARD_BUFFER_LIMIT) {
        return value
    }

    val trimmed = value.text.takeLast(REMOTE_KEYBOARD_BUFFER_TAIL)
    return TextFieldValue(
            text = trimmed,
            selection = TextRange(trimmed.length)
    )
}

private fun emitInsertedText(
        text: String,
        onSendText: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit
) {
    if (text.isEmpty()) return

    val parts = text.split('\n')
    parts.forEachIndexed { index, part ->
        if (part.isNotEmpty()) {
            onSendText(part)
        }
        if (index < parts.size - 1) {
            onSendKeyPress(KEYCODE_ENTER)
        }
    }
}


private fun commonPrefixLength(left: String, right: String): Int {
    val max = minOf(left.length, right.length)
    var index = 0
    while (index < max && left[index] == right[index]) {
        index++
    }
    return index
}

private fun commonSuffixLength(left: String, right: String, prefixLength: Int): Int {
    val max = minOf(left.length, right.length) - prefixLength
    var count = 0
    while (count < max &&
                    left[left.length - 1 - count] == right[right.length - 1 - count]) {
        count++
    }
    return count
}
