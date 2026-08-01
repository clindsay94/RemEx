package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins that input actually passes through the capability gate, and records which view model does not
 * yet have one (RemEx-q9zw, RemEx-i8ty).
 *
 * A SOURCE-READING TEST, WHICH THIS MODULE ONLY USES WHEN THE ALTERNATIVE IS NO TEST AT ALL.
 * `RemoteDesktopViewModel` is an `AndroidViewModel` reaching a JNI client, and there is no
 * Robolectric here, so the gate itself cannot be exercised behaviourally. Deleting either guard
 * currently fails nothing. `SendDispatcherDeclarationOrderTest` is the established precedent for
 * exactly this trade, and it already iterates these same two view models as peer input paths.
 *
 * THE SECOND TEST WAS DELIBERATELY THE WRONG WAY ROUND UNTIL RemEx-i8ty, and the handover worked.
 * `RemoteControlViewModel` sends the same `desktop_input` envelope from the Remote Mouse screen, and
 * that screen needs no video stream, so it is reachable on a host where remote desktop is off
 * entirely — a wider exposure than the path RemEx-q9zw gated, not a narrower one. That bead pinned
 * the gap by asserting the view model was NOT gated, precisely so this one could not close without
 * coming back here. It did not drift; it failed, and was inverted. The mechanism is worth keeping in
 * mind next time a fix has to be split.
 */
class RemoteDesktopViewModelInputGateTest {

    /**
     * The file's CODE, with comments stripped.
     *
     * STRIPPING IS NOT TIDINESS, IT IS THE DIFFERENCE BETWEEN A GUARD AND A PLACEBO. A first version
     * of the `SharingStarted.Eagerly` assertion below scanned the raw text, and that literal appears
     * twice in the file it checks: once in the code and once in the KDoc explaining why the code
     * says it. So the assertion passed on its own documentation, and swapping the real call to
     * `WhileSubscribed` left the suite green while permanently reopening the gate. Review caught it;
     * the injection that was supposed to have proven it had replaced BOTH occurrences at once, so it
     * failed for the wrong reason and looked like evidence.
     *
     * `SendDispatcherDeclarationOrderTest` already strips comments for exactly this reason, and says
     * so: "a guard that fails on its own documentation gets deleted rather than obeyed". The mirror
     * image — a guard that PASSES on its own documentation — is worse, because nothing draws
     * attention to it.
     */
    private fun source(name: String): String {
        val file =
                File("src/main/java/com/clindsay94/remex/ui/screens/$name.kt").takeIf { it.isFile }
                        ?: File("app/src/main/java/com/clindsay94/remex/ui/screens/$name.kt")
        assertTrue("expected to find $name at ${file.path}", file.isFile)
        return file.readText()
                .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
                .replace(Regex("""//.*"""), "")
    }

    private val gate = "if (!_capabilityState.value.supportsInputSimulation) return"

    @Test
    fun `both remote desktop send paths check the capability before sending`() {
        val text = source("RemoteDesktopViewModel")

        // Two choke points, because there are two ways input leaves that class: sendInput carries
        // mouse, keyboard, scroll and typed text; sendPointerBatch carries the stylus/touch stream.
        assertEquals(
                "expected the capability gate at both send choke points",
                2,
                Regex(Regex.escape(gate)).findAll(text).count()
        )

        // Position matters as much as presence for the batch path: gating before the consumer is
        // started means a host that cannot inject never even spins up the sender coroutine.
        val batch = text.indexOf("fun sendPointerBatch(")
        val startCall = text.indexOf("startPointerSenderIfNeeded()", batch)
        val gateInBatch = text.indexOf(gate, batch)
        assertTrue("sendPointerBatch should be gated", gateInBatch in (batch + 1) until startCall)
    }

    @Test
    fun `the remote mouse view model checks the capability too`() {
        val text = source("RemoteControlViewModel")

        assertTrue(
                "RemoteControlViewModel still sends desktop_input, so this test is still needed",
                text.contains("\"desktop_input\"")
        )
        // STILL KEYED ON THE CAPABILITY NAME RATHER THAN THE GUARD TEXT. That was what let the
        // previous, inverted version of this assertion fire at all: the sibling's guard reads
        // `_capabilityState`, and this class named its holder `capabilityState`, so a whole-string
        // match would have stayed green and the handover would have been lost in silence. The same
        // property makes it robust now — `supportsInputSimulation` cannot appear in that file for
        // any other reason.
        assertTrue(
                "RemoteControlViewModel must gate input on supportsInputSimulation (RemEx-i8ty)",
                text.contains("supportsInputSimulation")
        )

        // The guard has to precede the launch, not sit inside it: returning early avoids scheduling
        // a coroutine only to drop it, and keeps the refusal off the single-parallelism dispatcher
        // that carries keystroke ordering.
        val send = text.indexOf("private fun sendInput(")
        val gateHere = text.indexOf("supportsInputSimulation", send)
        val launch = text.indexOf("viewModelScope.launch(sendDispatcher)", send)
        assertTrue("the gate should precede the launch", gateHere in (send + 1) until launch)

        // SharingStarted.Eagerly is load bearing and silently substitutable. Nothing collects this
        // state - it is read as `.value` at send time - so under WhileSubscribed the upstream would
        // never be subscribed to, `.value` would stay at its initial value forever, and the gate
        // would be permanently open while looking correct. The settings flows in the same file DO
        // use WhileSubscribed, which is what makes the wrong choice look idiomatic here. This
        // assertion only means anything because `source` strips comments first - see there.
        assertTrue(
                "the capability state must be shared Eagerly; WhileSubscribed would never collect",
                text.contains("SharingStarted.Eagerly")
        )
    }
}
