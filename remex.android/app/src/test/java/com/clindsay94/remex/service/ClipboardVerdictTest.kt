package com.clindsay94.remex.service

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The clipboard verdict parser refuses anything it does not recognise (RemEx-hgqs).
 *
 * **THIS FILE EXISTS BECAUSE THE FIRST VERSION FAILED OPEN AND NOTHING NOTICED.** The parsing lived
 * inline in a view-model coroutine as a `when` with no `else`: it matched `"empty"` and
 * `"too_large"`, and every other value — including `"unavailable"`, the reason the C# export returns
 * when it throws — fell through to the send. So a validator that had failed produced an unvalidated
 * payload on the PC's clipboard and a message telling the user it had worked.
 *
 * The C# half of the same change went to real trouble to make that case distinguishable: an extra
 * `failureJson` parameter on the JNI export helper, and a dedicated `UnavailableNativeJson()` whose
 * whole purpose is to give a failure the same shape as a verdict so the phone can tell. The Kotlin
 * side then drove around the guard rail. One table-driven test over the five inputs is what closes
 * that, and it is only possible because the logic moved out of the coroutine — plain Kotlin, no
 * Android dependency, no Robolectric.
 *
 * The wire vocabulary is a closed set owned by another language. Every case below that is not an
 * explicit "yes" must come back [ClipboardVerdict.Unavailable], because the alternative sends
 * whatever the user last copied having checked nothing about it.
 */
class ClipboardVerdictTest {

    private val max = 262144

    @Test
    fun `a clean verdict is sendable`() {
        assertEquals(
                ClipboardVerdict.Sendable,
                clipboardVerdictOf("""{"reason":"none","byteCount":11,"maxBytes":$max}"""),
        )
    }

    @Test
    fun `an empty clipboard is refused`() {
        assertEquals(
                ClipboardVerdict.Empty,
                clipboardVerdictOf("""{"reason":"empty","byteCount":0,"maxBytes":$max}"""),
        )
    }

    @Test
    fun `too large carries the limit in kilobytes, not the payload size`() {
        // The limit is the actionable half, and reporting byteCount would report a measurement of
        // the user's private content back to them.
        assertEquals(
                ClipboardVerdict.TooLarge(256),
                clipboardVerdictOf("""{"reason":"too_large","byteCount":900000,"maxBytes":$max}"""),
        )
    }

    @Test
    fun `an export failure is refused rather than sent`() {
        // THE BUG THIS FILE WAS WRITTEN FOR. "unavailable" is what the C# export returns when it
        // throws; the old parser let it through to the send.
        assertEquals(
                ClipboardVerdict.Unavailable,
                clipboardVerdictOf("""{"reason":"unavailable","byteCount":0,"maxBytes":$max}"""),
        )
    }

    @Test
    fun `a reason this side does not recognise is refused`() {
        // The set is owned by the C# side and can grow. A value added there and not here must fail
        // closed, or adding one silently disables validation on every phone running an older build.
        assertEquals(
                ClipboardVerdict.Unavailable,
                clipboardVerdictOf("""{"reason":"quarantined","byteCount":5,"maxBytes":$max}"""),
        )
    }

    @Test
    fun `a missing reason key is refused`() {
        // optString returns "" for an absent key - which is not any known reason, and under the old
        // parser was not any known reason either, and was sent anyway.
        assertEquals(
                ClipboardVerdict.Unavailable,
                clipboardVerdictOf("""{"byteCount":5,"maxBytes":$max}"""),
        )
    }

    @Test
    fun `malformed json is refused rather than throwing`() {
        // A throw here would surface as an unhandled coroutine exception on the main thread. It also
        // must not be mistaken for a refusal reason the user should act on.
        assertEquals(ClipboardVerdict.Unavailable, clipboardVerdictOf("not json at all"))
        assertEquals(ClipboardVerdict.Unavailable, clipboardVerdictOf("{"))
    }

    @Test
    fun `a null answer is refused`() {
        // What the caller passes when the native library is missing or its stub threw.
        assertEquals(ClipboardVerdict.Unavailable, clipboardVerdictOf(null))
    }

    @Test
    fun `too large without a usable limit is refused rather than claiming a limit of zero`() {
        // "The limit is 0 KB." is worse than naming no limit: it is a sentence that cannot be acted
        // on and reads as a bug, which it would be.
        assertEquals(
                ClipboardVerdict.Unavailable,
                clipboardVerdictOf("""{"reason":"too_large","byteCount":900000}"""),
        )
    }
}
