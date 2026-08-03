package com.clindsay94.remex.ui.screens

import androidx.annotation.StringRes
import com.clindsay94.remex.R

/** A native pairing failure split into its machine-readable cause and its diagnostic text. */
internal data class PairingFailure(val code: String?, val detail: String)

/**
 * Where a pairing failure is being shown, which changes what the correct next step IS.
 *
 * The same cause needs different advice on the two surfaces, because the two offer different
 * recoveries. Getting this wrong is not cosmetic: telling a user to tap a button that the screen
 * does not have is worse than showing them English.
 */
internal enum class PairingSurface {
    /**
     * The dedicated pairing screen. Submit re-uses the existing native session, so once that
     * session is gone no amount of retyping can succeed — Cancel is the only working recovery.
     */
    Dedicated,

    /**
     * The inline PIN field on the connection screen. It has no Cancel button, and its
     * `connect()` calls StartPairing fresh on every tap, so retyping the current PIN and
     * tapping Connect DOES work here.
     */
    InlineConnect,
}

/**
 * Turns a native pairing failure string into something a user can read.
 *
 * The pairing exports in Remex.Core return `"ERROR: <CODE>: <diagnostic>"` (see PairingErrorCodes
 * there). Remex.Core is shared and NativeAOT and has no access to Android resources, so it cannot
 * localize anything itself — the code is the only way this side can tell the causes apart and pick
 * a translated sentence. The diagnostic is logged rather than shown, which is strictly more than
 * happened before, when it was displayed to the user and logged nowhere.
 *
 * This lives outside [PairingViewModel] because there are TWO callers: the pairing screen and
 * [com.clindsay94.remex.RemexClientManager]'s auto-pair path, which is not a ViewModel. Converting
 * only the screen left the auto-pair path showing raw English with a machine code bolted on the
 * front — worse than before the codes existed (RemEx-6gkr).
 */
internal object PairingErrors {

    /**
     * Splits a native pairing failure into its cause code and diagnostic.
     *
     * The code is uppercase ASCII with underscores, so the split is on the FIRST `": "` after the
     * prefix — the diagnostics themselves contain colons ("TCP probe to host:port timed out"), and
     * a candidate is only accepted as a code if it matches that alphabet. Anything else is treated
     * as codeless and returned whole as the detail, which is what an older libRemexCore.so or a
     * future unknown shape produces.
     *
     * Pure and total so it can be unit-tested without the native layer.
     */
    fun parse(raw: String?): PairingFailure {
        val text = raw.orEmpty()
        if (!text.startsWith("ERROR: ")) return PairingFailure(null, text)
        val body = text.removePrefix("ERROR: ")
        val sep = body.indexOf(": ")
        if (sep <= 0) return PairingFailure(null, body)
        val candidate = body.substring(0, sep)
        if (!candidate.all { it in 'A'..'Z' || it == '_' }) return PairingFailure(null, body)
        return PairingFailure(candidate, body.substring(sep + 2))
    }

    /**
     * The causes that leave the native pairing session unusable.
     *
     * **ONE LIST, READ BY BOTH THINGS THAT NEED IT.** [killsSession] and [messageRes] are two
     * questions about the same fact, and keeping two copies means a cause can be added to one and
     * not the other — which is invisible, because the user simply gets advice that does not fit the
     * state they are in. Deriving both from here costs nothing and removes the failure mode.
     *
     * The membership is not guessable from the names. PIN_FETCH_TIMEOUT reads like something you
     * retry and is not: see [killsSession] for why.
     */
    private val DEAD_SESSION_CAUSES =
            setOf(
                    "PIN_REJECTED",
                    "NO_SESSION",
                    "SESSION_KEY_LOST",
                    "PIN_CONFIRM_TIMEOUT",
                    "PAIRING_ABORTED_SESSION_LOST",
                    "PIN_FETCH_TIMEOUT",
            )

    /**
     * Whether this cause leaves the native pairing session unusable.
     *
     * **PIN_FETCH_TIMEOUT IS IN THE SET, AND THAT IS THE COUNTER-INTUITIVE ONE.** Here is the actual
     * derivation, because "a timeout means the session is gone" is not obvious and the reason
     * matters: inside `PairingClient.RequestPinAsync` the ONLY operation handed the cancellation
     * token is `webSocket.ReceiveAsync` — the send above it is called with no token at all. So a
     * PIN_FETCH_TIMEOUT can only have come from a cancelled receive, and `ClientWebSocket` registers
     * `Abort()` on the token it is given. Cancelled receive, aborted socket, dead session
     * (RemEx-d3z9; `CancelledReceiveKillsTheSocketTests` measures it against a real TLS connection).
     *
     * The same derivation is why PIN_UNAVAILABLE is NOT in the set. It is returned whenever
     * `RequestPinAsync` yields null without throwing — a reply carrying no PIN, a host Close frame,
     * or the loop falling out on an already-cancelled token — and none of those went through a
     * cancelled receive.
     */
    fun killsSession(code: String?): Boolean = code in DEAD_SESSION_CAUSES

    /**
     * Maps a native cause code onto the localized string that explains it to the user.
     *
     * Every target is placeholder-free on purpose. Wrapping a localized cause inside another
     * localized sentence produced doubled next steps and contradictory advice in RemEx-meqm, so the
     * cause string is shown alone. An unknown or absent code falls back to the generic message
     * rather than surfacing the raw diagnostic, which is what lets new codes be added natively
     * without a coordinated release.
     *
     * NOTE ON THE DEAD-SESSION GROUP: for those six causes the native session is unusable, though
     * NOT all for the same reason — PIN_REJECTED, PIN_CONFIRM_TIMEOUT, PAIRING_ABORTED_SESSION_LOST
     * and PIN_FETCH_TIMEOUT have had ClearActivePairingState() called on them, whereas
     * NO_SESSION and SESSION_KEY_LOST are bare guard returns where the session was already absent or
     * its key already gone (and for SESSION_KEY_LOST the socket stays non-null, so resubmitting
     * returns it again indefinitely).
     * What they share is that none can be fixed by resubmitting, so the advice depends entirely on
     * [surface]: on [PairingSurface.Dedicated] Submit stays enabled but re-uses that dead session
     * (RemEx-aor9), so the only escape is Cancel; on [PairingSurface.InlineConnect] there is no
     * Cancel and Connect restarts pairing outright, so entering the PC's current PIN does work.
     * PIN_REJECTED is the most common pairing failure of all, which is what makes the distinction
     * worth the parameter rather than a comment.
     */
    @StringRes
    fun messageRes(code: String?, surface: PairingSurface): Int =
            when (code) {
                // Never reached the host: bad address, or refused/unanswered TCP or TLS.
                "HOST_URL_INVALID", "TCP_TIMEOUT", "TCP_REFUSED", "TLS_TIMEOUT" ->
                        R.string.pairing_error_reach_failed
                // Reached the host during start, but it never answered. No stale Submit button is
                // sitting there on this path, so "check it is on and try again" is followable.
                "PAIR_TIMEOUT" -> R.string.pairing_error_timeout
                // The caller abandoned the attempt (RemEx-defb) — usually its own timeout, but any
                // cancellation of the surrounding scope does it too. Grouped with PAIR_TIMEOUT
                // because the session survives, so trying again is followable advice. Normally
                // unreachable: by the time the native returns this, the coroutine has already thrown
                // TimeoutCancellationException and that path showed the message. Mapping it anyway
                // keeps a surfaced abort from rendering as "unknown".
                "PAIRING_ABORTED" -> R.string.pairing_error_timeout
                "PAIR_MALFORMED" -> R.string.pairing_error_malformed_response
                // The native session is unusable for all of these — see the note above. The advice
                // has to match the recovery the surface actually offers.
                // Derived from DEAD_SESSION_CAUSES rather than restated, so this arm and
                // killsSession cannot drift. PAIRING_ABORTED_SESSION_LOST is here because abandoning
                // a PIN submission OR a PIN fetch calls ClearActivePairingState, and on Dedicated the
                // Submit button that stays enabled can then only fail (RemEx-aor9).
                in DEAD_SESSION_CAUSES ->
                        when (surface) {
                            PairingSurface.Dedicated -> R.string.pairing_error_verify_failed
                            PairingSurface.InlineConnect -> R.string.pairing_error_bad_pin
                        }
                // The host declined, has no active PIN, closed the socket, or the loop fell out
                // on an already-cancelled token — RequestPinAsync yields null for all of those. What
                // they share, and the reason this is survivable while the fetch TIMEOUT beside it is
                // not, is that none of them went through a CANCELLED receive. See killsSession.
                "PIN_UNAVAILABLE" -> R.string.pairing_error_empty_response
                // ARG_MISSING is a caller bug and is the one cause that does NOT clear session
                // state, so "try again" is right advice for it. UNEXPECTED and any code this build
                // does not recognise land here too.
                else -> R.string.pairing_error_unknown
            }
}
