package com.clindsay94.remex.ui.screens

import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue

private const val REMOTE_KEYBOARD_BUFFER_LIMIT = 256
private const val REMOTE_KEYBOARD_BUFFER_TAIL = 128
private const val KEYCODE_BACKSPACE = 8
private const val KEYCODE_ENTER = 13

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
