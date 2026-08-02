package com.clindsay94.remex

import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue
import com.clindsay94.remex.ui.screens.applyRemoteKeyboardEdit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * CHARACTERIZATION tests for the keyboard edit diff, written BEFORE it is deleted (RemEx-iyn2).
 *
 * The bead's plan is to replace `applyRemoteKeyboardEdit` with `InputTransformation`, which hands
 * you the edit deltas the diff currently reconstructs. That is the right move — but the diff
 * special-cases real IME behaviour (autocorrect replacing a word, swipe input committing a whole
 * word, multi-line commits), and **nobody has written down what it actually does in those cases**.
 * Migrating without that is a rewrite against an unspecified target.
 *
 * So these tests describe the CURRENT behaviour rather than an ideal one. Where the current
 * behaviour is arguably wrong it is pinned anyway and labelled, because the migration's job is to
 * be equivalent first and better second — a keystroke path that changes silently is one nobody
 * notices until they are typing a password into a remote machine.
 *
 * KEYCODE_BACKSPACE is 8 and KEYCODE_ENTER is 13, declared as private constants in
 * `RemoteKeyboardInput.kt` and therefore written as literals here.
 */
class RemoteKeyboardEditTest {

    private class Recorder {
        val text = mutableListOf<String>()
        val keys = mutableListOf<Int>()

        /**
         * What the function RETURNED, which matters as much as what it sent: callers assign it
         * straight back to their state, so it becomes the baseline the NEXT edit diffs against.
         */
        lateinit var result: TextFieldValue
    }

    private fun edit(old: TextFieldValue, new: TextFieldValue): Recorder {
        val recorder = Recorder()
        recorder.result = applyRemoteKeyboardEdit(
            currentValue = old,
            newValue = new,
            onSendText = { recorder.text += it },
            onSendKeyPress = { recorder.keys += it }
        )
        return recorder
    }

    private fun edit(old: String, new: String): Recorder =
        edit(TextFieldValue(old), TextFieldValue(new))

    @Test
    fun `typing one character sends that character`() {
        val recorder = edit("hell", "hello")

        assertEquals(listOf("o"), recorder.text)
        assertTrue(recorder.keys.isEmpty())
    }

    @Test
    fun `deleting one character sends one backspace`() {
        val recorder = edit("hello", "hell")

        assertTrue(recorder.text.isEmpty())
        assertEquals(listOf(8), recorder.keys)
    }

    @Test
    fun `no change sends nothing at all`() {
        val recorder = edit("hello", "hello")

        assertTrue(recorder.text.isEmpty())
        assertTrue(recorder.keys.isEmpty())
    }

    @Test
    fun `swipe input committing a whole word sends it as one text event`() {
        // Gesture typing commits the entire word at once. Sending it as one text event rather than
        // seven keystrokes is both faster and what the host expects.
        val recorder = edit("", "keyboard")

        assertEquals(listOf("keyboard"), recorder.text)
        assertTrue(recorder.keys.isEmpty())
    }

    @Test
    fun `autocorrect replacing a word backspaces only the differing tail`() {
        // MEASURED, NOT ASSUMED - and my first guess was wrong, which is exactly why this file
        // exists. "teh " and "the " share the prefix "t" and the suffix " " only: position 1 is
        // 'e' vs 'h' and position 2 is 'h' vs 'e', so neither extends the match. The diff therefore
        // removes TWO characters and sends "he" back, not one and "h".
        //
        // That is more traffic than a human would produce by hand, but far less than clearing and
        // retyping the word - and it is the behaviour a replacement has to match, because the host
        // sees every backspace.
        val recorder = edit("teh ", "the ")

        assertEquals(listOf(8, 8), recorder.keys)
        assertEquals(listOf("he"), recorder.text)
    }

    @Test
    fun `deleting from the middle costs one backspace, not a retype of the tail`() {
        // THIS IS WHAT THE SUFFIX MATCH BUYS. "abcd" -> "acd" shares the prefix "a" and the suffix
        // "cd", so the diff removes exactly the one character that went. A replacement that only
        // matched the prefix would send three backspaces and retype "cd" - the same end state in
        // four events instead of one, on a path where every event crosses the network and the host
        // replays it as a real keystroke into whatever window has focus.
        val recorder = edit("abcd", "acd")

        assertEquals(listOf(8), recorder.keys)
        assertTrue(recorder.text.isEmpty())
    }

    @Test
    fun `a multi-line commit sends each line as text with enter between`() {
        // Paste, or a keyboard that commits a newline. Newlines cannot ride in a text event, so
        // they become explicit ENTER key presses BETWEEN the lines - not after the last one.
        val recorder = edit("", "one\ntwo")

        assertEquals(listOf("one", "two"), recorder.text)
        assertEquals(listOf(13), recorder.keys)
    }

    @Test
    fun `a trailing newline sends enter with no empty text event after it`() {
        val recorder = edit("", "one\n")

        assertEquals(listOf("one"), recorder.text)
        assertEquals(listOf(13), recorder.keys)
    }

    @Test
    fun `clearing the field backspaces once per character`() {
        val recorder = edit("abc", "")

        assertTrue(recorder.text.isEmpty())
        assertEquals(listOf(8, 8, 8), recorder.keys)
    }

    @Test
    fun `an ambiguous repeated-character edit resolves by longest common prefix`() {
        // CHARACTERIZATION, NOT ENDORSEMENT. "aa" -> "aaa" could be an insertion at any of three
        // positions and a prefix/suffix diff cannot tell which. The current code takes the longest
        // common prefix first, so it reports the insertion at the END. That is invisible for
        // repeated characters - the host receives the same result either way - but it IS the
        // behaviour, and a replacement that resolved it differently would only diverge on inputs
        // where the difference is unobservable. Pinned so nobody spends time "fixing" it.
        val recorder = edit("aa", "aaa")

        assertEquals(listOf("a"), recorder.text)
        assertTrue(recorder.keys.isEmpty())
    }

    @Test
    fun `replacing a selection is a delete followed by an insert`() {
        // Selecting "world" and typing "there": the diff sees the shared prefix "hello " and no
        // shared suffix, so it removes what went and sends what arrived. Asserted as the FULL key
        // list rather than a count of backspaces, so a stray ENTER cannot hide inside it.
        val recorder = edit("hello world", "hello there")

        assertEquals(listOf(8, 8, 8, 8, 8), recorder.keys)
        assertEquals(listOf("there"), recorder.text)
    }

    @Test
    fun `deleting a selection sends backspaces and no text at all`() {
        // Named by the bead and distinct from the two cases above: this deletes a selection that
        // does NOT start at position zero, so the prefix is non-empty and the insert is empty. A
        // replacement that emitted an empty text event here would have the host type nothing, which
        // is harmless, or clear the field, which is not.
        val recorder = edit("hello world", "hello ")

        assertEquals(listOf(8, 8, 8, 8, 8), recorder.keys)
        assertTrue(recorder.text.isEmpty())
    }

    @Test
    fun `the returned value carries the new text, not the old`() {
        // THIS ASSERTION EXISTS BECAUSE ITS ABSENCE WAS CAUGHT IN REVIEW. The first version of the
        // caret test compared result.selection against result.text.length - two properties of the
        // same returned object - so it held for ANY self-consistent return, including one that
        // discarded the edit and handed back the old text. That mutant passed the whole file.
        //
        // The return value is not decoration: callers assign it back to their state, so it is the
        // baseline the NEXT diff runs against. A migration that returned the wrong text would send
        // the right events once and then corrupt every keystroke after it, silently.
        val recorder = edit("hell", "hello")

        assertEquals("hello", recorder.result.text)
    }

    @Test
    fun `the caret is normalized to the end of the text`() {
        // The remote host has its own caret; this field is a keystroke buffer rather than a
        // document, so the local selection is collapsed to the end after every edit. A replacement
        // that preserved the local caret would let the user move it and silently desync the two.
        // Asserted against a literal rather than against result.text.length, so a return that
        // dropped the edit cannot satisfy it by being consistent with itself.
        val recorder = edit(
            TextFieldValue("hell"),
            TextFieldValue("hello", selection = TextRange(2))
        )

        assertEquals(5, recorder.result.selection.start)
        assertEquals(5, recorder.result.selection.end)
    }

    @Test
    fun `an over-long buffer is trimmed to its tail`() {
        // THE HIDDEN STATE NOTHING ELSE PINS. This field is never cleared by the user - it only
        // accumulates - so without a cap it grows for the whole session, and a migration that
        // dropped the cap would keep every test green while it did.
        //
        // THE FIXTURE IS NOT UNIFORM, AND THAT IS THE POINT. The first version of this test used
        // "x".repeat(300), for which takeLast(128) and take(128) are the same string - so it pinned
        // that a trim HAPPENS while saying nothing about WHICH HALF SURVIVES. Keeping the wrong
        // half is worse than keeping none: the returned value is the baseline the next diff runs
        // against, so retaining the OLDEST characters would make every later keystroke diff against
        // stale text and fire a burst of wrong backspaces at the host. Verified: with a uniform
        // fixture, swapping takeLast for take left all 397 tests green.
        //
        // The limit is 256 and the retained tail is 128, both private to RemoteKeyboardInput.kt.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")

        val recorder = edit("", long)

        assertEquals(128, recorder.result.text.length)
        assertEquals(long.takeLast(128), recorder.result.text)
        assertEquals(128, recorder.result.selection.start)

        // AND THE HOST STILL RECEIVED EVERYTHING. The trim is a local buffer concern; if it ever
        // shortened what was SENT, the user would watch characters go missing from the far end.
        assertEquals(listOf(long), recorder.text)
    }

    @Test
    fun `a buffer at the limit is left alone`() {
        // The trim fires ABOVE the limit, not at it. Pinned so the boundary cannot drift by one
        // and start discarding text a user can still see.
        val atLimit = "x".repeat(256)

        assertEquals(256, edit("", atLimit).result.text.length)
    }

    @Test
    fun `an in-progress composition suppresses the trim`() {
        // CHARACTERIZATION OF A DELIBERATE EXCEPTION. While the IME is composing - mid-word, before
        // the user has committed anything - the composition range indexes into the text. Trimming
        // underneath it would leave the IME pointing at offsets that no longer exist. So the cap is
        // skipped until the composition ends, and the buffer is allowed to exceed it briefly.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")

        val recorder = edit(
            TextFieldValue(""),
            TextFieldValue(long, selection = TextRange(300), composition = TextRange(299, 300))
        )

        assertEquals(long, recorder.result.text)
        assertEquals(TextRange(299, 300), recorder.result.composition)
        assertEquals(listOf(long), recorder.text)
    }

    @Test
    fun `a composition is preserved when no trim is needed`() {
        // The normal path copies the composition through untouched. Composition handling is exactly
        // what changes most under InputTransformation, so dropping it must not stay green.
        val recorder = edit(
            TextFieldValue("hell"),
            TextFieldValue("hello", composition = TextRange(4, 5))
        )

        assertEquals(TextRange(4, 5), recorder.result.composition)
    }

    @Test
    fun `every edit sends something or nothing, never a partial`() {
        // The invariant the host depends on: a text change always produces at least one event, and
        // an unchanged value produces none. A silent no-op on a real change would drop a keystroke,
        // which the user only discovers by reading what arrived.
        //
        // BOTH HALVES ARE EXERCISED. An earlier version listed only changed pairs, so the expected
        // expression was the constant `true` and the second half of the stated invariant - the one
        // that stops a redraw from replaying the last keystroke - was never tested at all.
        val changes = listOf(
            "" to "a", "a" to "", "ab" to "ba", "hello" to "hello world",
            "hello world" to "hello", "teh" to "the", "x" to "x\ny",
            "" to "", "hello" to "hello"
        )

        for ((old, new) in changes) {
            val recorder = edit(old, new)
            val sentSomething = recorder.text.isNotEmpty() || recorder.keys.isNotEmpty()

            assertEquals("'$old' -> '$new'", old != new, sentSomething)
        }
    }
}
