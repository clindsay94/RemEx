package com.clindsay94.remex

import androidx.compose.foundation.text.input.TextFieldBuffer
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue
import com.clindsay94.remex.ui.screens.applyRemoteKeyboardEdit
import com.clindsay94.remex.ui.screens.trimRemoteKeyboardBufferIfIdle
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * CHARACTERIZATION tests for the keyboard edit diff, written BEFORE it was deleted (RemEx-iyn2) and
 * migrated to drive `applyRemoteKeyboardEdit(TextFieldBuffer, ...)` instead of the
 * `TextFieldValue`-returning function it replaced (RemEx-d459).
 *
 * The bead's plan was to replace `applyRemoteKeyboardEdit` with `InputTransformation`, which hands
 * you the edit deltas the diff used to reconstruct from two whole `TextFieldValue`s. In practice the
 * diffing algorithm itself (prefix/suffix match, newline splitting) had to survive the move
 * unchanged: `TextFieldBuffer.changes` reports what the platform did to the buffer, and for a driven
 * `replace(0, length, new)` that is always a single whole-range change - so reproducing "two
 * backspaces + 'he'" out of that still requires the same prefix/suffix diff *inside* the reported
 * range. `applyRemoteKeyboardEdit` and its private helpers were kept for that reason - see
 * RemoteKeyboardInput.kt.
 *
 * WHAT THIS FILE DOES NOT PROVE. Every test here drives the buffer through `state.edit { replace(...) }`
 * - a programmatic, synthetic edit. That proves the diff algorithm's behaviour for a given
 * before/after text pair; it does NOT prove a real IME produces those exact buffer states for a real
 * composing sequence (Gboard swipe, autocorrect). From the public API contract alone (Context7 docs
 * against `androidx.compose.foundation:foundation` ~1.11.0-1.12.0-beta02, matching this project's
 * pin): `SetComposingTextCommand`/`SetComposingRegionCommand` exist as discrete `EditCommand`s, which
 * is consistent with each composing step arriving as its own edit session ahead of `transformInput`
 * - but the docs do not state this outright, and it is UNVERIFIED beyond that inference. The bead's
 * own acceptance criterion is an on-device IME pass (Gboard swipe + autocorrect against a real host)
 * that this test file cannot substitute for and does not attempt to; it is still outstanding.
 *
 * So these tests still describe the CURRENT behaviour rather than an ideal one, pinned via a harness
 * that drives a real `TextFieldState` the same way the framework would: `state.edit { replace(...) }`
 * to install the raw incoming edit, then `applyRemoteKeyboardEdit(this, ...)` called from inside that
 * same edit block, exactly as `InputTransformation.transformInput` is invoked in production.
 *
 * The buffer-length CAP is no longer part of that diff. It moved to `trimRemoteKeyboardBufferIfIdle`,
 * a separate `TextFieldState`-level function (RemEx-d459 follow-up), because `TextFieldBuffer` cannot
 * see the IME composition range but `TextFieldState.composition` can. This is a PINNED invariant
 * carried over from the pre-migration diff, not a new judgement call: the composition range is a pair
 * of offsets into the text, and trimming characters off the head while composing leaves those offsets
 * pointing at positions that no longer exist. See the tests below that target the new function
 * directly, and its doc comment in RemoteKeyboardInput.kt.
 *
 * KEYCODE_BACKSPACE is 8 and KEYCODE_ENTER is 13, declared as private constants in
 * `RemoteKeyboardInput.kt` and therefore written as literals here.
 */
class RemoteKeyboardEditTest {

    private class Recorder {
        val text = mutableListOf<String>()
        val keys = mutableListOf<Int>()

        /**
         * What the field's state ended up holding, which matters as much as what it sent: the next
         * edit diffs against this text, so a wrong result silently corrupts every keystroke after it.
         */
        lateinit var resultText: String
        var resultSelection: TextRange = TextRange.Zero
    }

    /**
     * Drives [applyRemoteKeyboardEdit] the way `InputTransformation.transformInput` does in
     * production: [rawEdit] installs the incoming, not-yet-transformed edit into the buffer within
     * the SAME `edit` block the function under test runs in - exactly as the framework applies a raw
     * edit into the buffer before invoking registered `InputTransformation`s against it. Doing the
     * raw edit and the transform in separate `edit` blocks would make `buffer.originalText` equal
     * `buffer.asCharSequence()` (both already post-edit), and the diff would never fire.
     */
    private fun edit(old: String, rawEdit: TextFieldBuffer.() -> Unit): Recorder {
        val recorder = Recorder()
        val state = TextFieldState(old)
        state.edit {
            rawEdit()
            applyRemoteKeyboardEdit(
                    buffer = this,
                    onSendText = { recorder.text += it },
                    onSendKeyPress = { recorder.keys += it }
            )
        }
        recorder.resultText = state.text.toString()
        recorder.resultSelection = state.selection
        return recorder
    }

    private fun edit(old: String, new: String): Recorder =
            edit(old) { replace(0, length, new) }

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
    fun `the field ends up holding the new text, not the old`() {
        // THIS ASSERTION EXISTS BECAUSE ITS ABSENCE WAS CAUGHT IN REVIEW. The first version of the
        // caret test compared the result's selection against the result's text length - two
        // properties of the same state - so it held for ANY self-consistent result, including one
        // that discarded the edit and left the old text in place. That mutant passed the whole file.
        //
        // The buffer's end state is not decoration: the NEXT edit diffs against it. A migration that
        // left the wrong text in the field would send the right events once and then corrupt every
        // keystroke after it, silently.
        val recorder = edit("hell", "hello")

        assertEquals("hello", recorder.resultText)
    }

    @Test
    fun `the caret is normalized to the end of the text`() {
        // The remote host has its own caret; this field is a keystroke buffer rather than a
        // document, so the local selection is collapsed to the end after every edit. A replacement
        // that preserved the local caret would let the user move it and silently desync the two.
        // The raw edit reports the incoming caret at position 2 (mid-text, as an IME might place it);
        // asserted against a literal rather than against resultText.length, so a result that dropped
        // the edit cannot satisfy it by being consistent with itself.
        val recorder = edit("hell") {
            replace(0, length, "hello")
            selection = TextRange(2)
        }

        assertEquals(5, recorder.resultSelection.start)
        assertEquals(5, recorder.resultSelection.end)
    }

    // --- trimRemoteKeyboardBufferIfIdle -------------------------------------------------------
    //
    // RemEx-d459 follow-up: the buffer-length cap used to be the last step inside
    // `applyRemoteKeyboardEdit`, exercised above via `edit(old, new)`. It moved to this separate
    // `TextFieldState`-level function because `TextFieldBuffer` cannot see the IME composition
    // range. `isComposing` is an explicit parameter (defaulting to the real
    // `TextFieldState.composition` check in production) because there is no public way to drive a
    // real `TextFieldState` into a composing state from a JVM unit test.

    @Test
    fun `an over-long buffer is trimmed to its tail once idle`() {
        // THE HIDDEN STATE NOTHING ELSE PINS. This field is never cleared by the user - it only
        // accumulates - so without a cap it grows for the whole session, and a migration that
        // dropped the cap would keep every test green while it did.
        //
        // THE FIXTURE IS NOT UNIFORM, AND THAT IS THE POINT. The first version of this test used
        // "x".repeat(300), for which takeLast(128) and take(128) are the same string - so it pinned
        // that a trim HAPPENS while saying nothing about WHICH HALF SURVIVES. Keeping the wrong
        // half is worse than keeping none: the result is the baseline the next diff runs against, so
        // retaining the OLDEST characters would make every later keystroke diff against stale text
        // and fire a burst of wrong backspaces at the host. Verified: with a uniform fixture, swapping
        // the trim to keep the head instead of the tail left all tests green.
        //
        // The limit is 256 and the retained tail is 128, both private to RemoteKeyboardInput.kt.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")
        val state = TextFieldState(long)

        trimRemoteKeyboardBufferIfIdle(state, isComposing = false)

        assertEquals(128, state.text.length)
        assertEquals(long.takeLast(128), state.text.toString())
        assertEquals(128, state.selection.start)
    }

    @Test
    fun `a buffer at the limit is left alone`() {
        // The trim fires ABOVE the limit, not at it. Pinned so the boundary cannot drift by one
        // and start discarding text a user can still see.
        val state = TextFieldState("x".repeat(256))

        trimRemoteKeyboardBufferIfIdle(state, isComposing = false)

        assertEquals(256, state.text.length)
    }

    @Test
    fun `an in-progress composition suppresses the trim`() {
        // PINNED, NOT A JUDGEMENT CALL (RemEx-d459 note 3, carried over verbatim from the
        // pre-migration diff). The IME composition range is a pair of offsets INTO THE TEXT.
        // Trimming characters off the head while a composition is active leaves those offsets
        // pointing at positions that no longer exist - a real IME reading a stale composing range
        // presents as garbled input, silently, with no exception and no log line. This is exactly
        // the failure class this repo guards hardest against, which is why the guard moved to a new
        // site instead of being dropped when `TextFieldBuffer` turned out not to expose composition.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")
        val state = TextFieldState(long)

        trimRemoteKeyboardBufferIfIdle(state, isComposing = true)

        assertEquals(long, state.text.toString())
    }

    @Test
    fun `the trim proceeds once composition has ended`() {
        // The other half of the same invariant: the guard is a suppression, not a permanent
        // disabling. Once composing stops being true for an otherwise-identical over-limit buffer,
        // the trim fires exactly as it does with no composition ever having been involved. Without
        // this test, a mutant that hardcoded `isComposing` to always suppress would pass the test
        // above and nothing else would catch it.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")
        val state = TextFieldState(long)

        trimRemoteKeyboardBufferIfIdle(state, isComposing = false)

        assertEquals(128, state.text.length)
        assertEquals(long.takeLast(128), state.text.toString())
    }

    @Test
    fun `the default isComposing argument reads the real composition, not a hardcoded value`() {
        // THE GAP THE TWO TESTS ABOVE DO NOT COVER. Both of them pass `isComposing` explicitly, so
        // neither one exercises the default expression `state.composition != null` at all - a
        // mutant that hardcoded the default to `true` (the cap silently dead for the whole session)
        // or inverted it to `state.composition == null` (trimming DURING composition, precisely
        // bead note 3's failure) would pass every other test in this file.
        //
        // This only pins the non-composing half of that binding: a fresh, un-edited `TextFieldState`
        // has `composition == null`, so calling with no second argument at all must still trim. The
        // composing half of the SAME binding is genuinely device-only - there is no public way to
        // put a real `TextFieldState.composition` into a non-null state outside an actual IME
        // session, so it is not faked here. See the class doc for the on-device gap this leaves.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")
        val state = TextFieldState(long)

        trimRemoteKeyboardBufferIfIdle(state)

        assertEquals(128, state.text.length)
        assertEquals(long.takeLast(128), state.text.toString())
    }

    // --------------------------------------------------------------------------------------------

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

    // --- legacy TextFieldValue overload (RemoteDesktopScreen.kt) --------------------------------
    //
    // RemEx-d459: this overload was kept rather than migrated - see its KDoc in
    // RemoteKeyboardInput.kt - but every test above moved to the new `TextFieldBuffer` entry point
    // when this file was rewritten, leaving the legacy overload's own guards with ZERO coverage.
    // Bead note 1's return-carrying-old-text mutant (the returned value silently holding the OLD
    // text, so the right events fire once and then every keystroke after it is diffed against stale
    // text) killed 6 tests before this bead and would have killed none of them after, without these.

    @Test
    fun `legacy overload's returned value carries the new text, not the old`() {
        // Mirrors "the field ends up holding the new text, not the old" above, for the
        // TextFieldValue-returning overload directly: RemoteDesktopScreen.kt assigns this return
        // value straight back to its own state, so a wrong return corrupts every keystroke after it.
        val result = applyRemoteKeyboardEdit(
                currentValue = TextFieldValue("hell"),
                newValue = TextFieldValue("hello"),
                onSendText = {},
                onSendKeyPress = {}
        )

        assertEquals("hello", result.text)
    }

    @Test
    fun `legacy overload trims an over-long buffer to its tail`() {
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")

        val result = applyRemoteKeyboardEdit(
                currentValue = TextFieldValue(""),
                newValue = TextFieldValue(long),
                onSendText = {},
                onSendKeyPress = {}
        )

        assertEquals(128, result.text.length)
        assertEquals(long.takeLast(128), result.text)
    }

    @Test
    fun `legacy overload suppresses the trim while composing`() {
        // Unlike the new `TextFieldBuffer` entry point, `TextFieldValue.composition` is a public,
        // settable field, so this half of the guard IS directly testable here - no device needed.
        val long = (0 until 300).map { 'a' + (it % 26) }.joinToString("")

        val result = applyRemoteKeyboardEdit(
                currentValue = TextFieldValue(""),
                newValue = TextFieldValue(long, composition = TextRange(long.length - 1, long.length)),
                onSendText = {},
                onSendKeyPress = {}
        )

        assertEquals(long, result.text)
    }
}
