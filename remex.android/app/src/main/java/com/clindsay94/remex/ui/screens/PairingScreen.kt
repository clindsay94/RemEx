package com.clindsay94.remex.ui.screens

import android.content.Context
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import com.clindsay94.remex.DeviceName
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.RemexClientManager
import kotlinx.coroutines.flow.update
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.LiveRegionMode

data class PairingUiState(
    val isLoading: Boolean = false,
    val pairingError: String? = null,
    val autoFilledPin: String? = null,
    // True when an auto-fetch was attempted on a trusted transport but returned no PIN, so the
    // screen can prompt the user to type the PIN shown on the PC. Never set on untrusted transports
    // (where we deliberately never attempt an auto-fetch), so it doesn't nag in the normal manual case.
    val autoPinFetchFailed: Boolean = false,
    // True once the native side has reported a cause that left the pairing session unusable — the
    // set is PairingErrors.killsSession, and it is reached from TWO places now. SubmitPin's own
    // failures (wrong PIN, expired session, lost key state, confirm timeout, an abandoned
    // submission, an unexpected exception), and the PIN auto-fetch, where a timeout or an abandoned
    // attempt cancels a read and cancelling a read aborts the socket (RemEx-d3z9).
    //
    // Whichever way it happened, the session is gone for good — re-submitting, with the same or a
    // different PIN, can only repeat the same failure. Submit must stay disabled once this is true;
    // only Cancel (which disposes this screen/ViewModel and lets the user start a fresh pairing
    // attempt) still works. See RemEx-aor9.
    val sessionDead: Boolean = false,

    // Which phase of the native handshake is running, as the token the native side emits — mapped to
    // a sentence at the render site, never here (RemEx-g87x). Null between attempts and once the
    // wait is over. Worth showing because the wait can be ninety seconds against a PC that accepts
    // TCP and TLS and then goes quiet, and a spinner alone cannot say which of those is stuck.
    val phase: String? = null,
)

class PairingViewModel : ViewModel() {
    private val _uiState = MutableStateFlow(PairingUiState())
    val uiState: StateFlow<PairingUiState> = _uiState.asStateFlow()

    // Reentrancy guards. The pairingRequired SharedFlow has replay=1, so each
    // PairingScreen recomposition would otherwise re-fire StartPairing in parallel
    // and race on the static _pairingWebSocket in the native layer.
    @Volatile private var startPairingInFlight: Boolean = false
    @Volatile private var startPairingSucceeded: Boolean = false

    suspend fun submitPin(
            context: Context,
            host: String,
            port: Int,
            pin: String,
            onSuccess: suspend (String, String, String) -> Unit
    ): Boolean {
        _uiState.value = PairingUiState(isLoading = true)

        val result =
                try {
                    // No withContext here: SubmitPairingPin hops to Dispatchers.IO itself
                    // (RemEx-uach). A hop at the call site would model the very belief that let
                    // RemexClientManager omit one and block the UI thread.
                    //
                    // ABOVE THE NATIVE BUDGET, NOT BELOW IT. The native gives the host 30s to confirm
                    // the PIN. This bound did nothing until RemEx-defb made it real — and a real 15s
                    // bound over a 30s budget would abandon pairings that were about to succeed, and
                    // tear the session down doing it. A backstop catches a native that never returns;
                    // it must not pre-empt one that is still working.
                    withTimeout(35_000) {
                        RemexCoreClient.SubmitPairingPin(pin).getOrNull() ?: ""
                    }
                } catch (_: TimeoutCancellationException) {
                    _uiState.value =
                            PairingUiState(
                                    isLoading = false,
                                    pairingError =
                                            context.getString(R.string.pairing_error_timeout)
                            )
                    return false
                }

        if (result.startsWith("OK:")) {
            val parts = result.substring(3).split("|")
            if (parts.size >= 2) {
                // parts[2] is the PAIR-1 reconnect secret (OK:hostId|spki|reconnectSecret).
                // Older/edge responses may omit it, so default to empty and let the caller
                // skip persistence when blank.
                onSuccess(parts[0], parts[1], if (parts.size >= 3) parts[2] else "")
                _uiState.value = PairingUiState(isLoading = false)
                return true
            }

            _uiState.value =
                    PairingUiState(
                            isLoading = false,
                            pairingError =
                                    context.getString(R.string.pairing_error_malformed_response)
                    )
            return false
        }

        val message =
                when {
                    // Every reachable native SubmitPin failure lands here — wrong PIN, expired
                    // session, lost key state, confirm timeout, an abandoned submission, or an
                    // unexpected exception. All six are developer-grade English naming ports, paths
                    // and cert internals, so
                    // the diagnostic is logged and the failure's cause code selects a localized
                    // sentence for the user instead (RemEx-6gkr). Nothing logged it before, so
                    // logcat gains what the screen loses.
                    //
                    // Five of the six resolve to advice that names Cancel, because Submit stays
                    // enabled here while re-using a session that is already unusable — so "get a
                    // fresh PIN and try again", followed literally in place, just comes back "No
                    // active pairing session" and repeats forever. See the dead-session note in
                    // PairingErrors, which also explains why the connection screen needs the
                    // opposite advice. The fifth, an unexpected exception, is unclassified and
                    // falls to the generic message. The abandoned case joins the dead-session
                    // group because abandoning a submission tears the session down too (RemEx-defb).
                    //
                    // Careful if the Submit predicate ever loosens: SubmitPairingPinNative has a
                    // SEVENTH error return, "PIN is required" (ARG_MISSING), and it is the one case
                    // that does NOT clear pairing state — so "get a fresh PIN" would be wrong
                    // advice for it. It is unreachable only because Submit requires 6 digits.
                    result.startsWith("ERROR: ") -> {
                        val failure = parsePairingError(result)
                        android.util.Log.w(
                                "PairingViewModel",
                                "PIN submission failed [${failure.code ?: "no code"}]: ${failure.detail}"
                        )
                        context.getString(pairingErrorMessageRes(failure.code))
                    }
                    result.isBlank() -> context.getString(R.string.pairing_error_empty_response)
                    else -> {
                        android.util.Log.w("PairingViewModel", "Unknown pairing error: $result")
                        context.getString(R.string.pairing_error_unknown)
                    }
                }
        // Every reachable "ERROR: " cause here clears the native pairing session
        // (ClearActivePairingState()), so Submit must not stay enabled to invite a re-submit that can
        // only repeat the same failure — see the sessionDead doc comment on PairingUiState.
        val sessionDead = result.startsWith("ERROR: ")
        _uiState.value =
                PairingUiState(isLoading = false, pairingError = message, sessionDead = sessionDead)
        return false
    }

    fun setError(msg: String) {
        _uiState.value = _uiState.value.copy(pairingError = msg, isLoading = false)
    }

    fun startPairing(
            context: Context,
            hostUrl: String,
            clientName: String,
            clientVersion: String,
            clientId: String,
            allowAutoPin: Boolean
    ) {
        // Reentrancy guard: don't kick off a second StartPairing while one is in flight,
        // and don't redo a successful one (the WebSocket is already alive on the native side).
        if (startPairingInFlight || startPairingSucceeded) return
        startPairingInFlight = true

        // CLEARS THE FLOW'S REPLAY CACHE, NOT THE SCREEN'S STATE. Clearing state here would be dead
        // code twice over: the launch below replaces the whole state object anyway, and the replayed
        // token from the previous attempt then arrives and overwrites whatever it set. So a retry
        // would open by announcing the phase the LAST attempt died on — usually "waiting for your PC
        // to show a PIN", which is the most misleading of the three (RemEx-g87x).
        RemexClientManager.clearPairingProgress()

        viewModelScope.launch {
            _uiState.value = PairingUiState(isLoading = true)

            // COLLECTED HERE RATHER THAN IN init, and the difference is not style. A launch in the
            // constructor needs Dispatchers.Main to exist, which it does not under plain JUnit — so
            // it made this ViewModel unconstructable and broke every existing test that builds one.
            // The full suite caught it; the targeted run did not.
            //
            // Scoping it to the attempt is also simply more correct: phases only arrive while one is
            // running, and the collector stops when it ends rather than living as long as the screen.
            val phases =
                    launch {
                        // update {} rather than a read-modify-write on .value. Every writer of this
                        // state is on the same dispatcher today, so the plain form is safe by
                        // coincidence rather than by construction — and this is the only write that
                        // arrives from a flow rather than from the linear body below.
                        RemexClientManager.pairingProgress.collect { phase ->
                            _uiState.update { it.copy(phase = phase) }
                        }
                    }

            val result =
                    try {
                        // StartPairing hops to Dispatchers.IO itself (RemEx-uach).
                        //
                        // 95s because the native budgets total 90 — a 10s TCP probe, then 20s of TLS
                        // and upgrade, then 60s of handshake. Same reasoning as the PIN submission
                        // below: this became a real bound in RemEx-defb, and at its old 15s it would
                        // have killed any pairing whose probe was slow, which is most of the ones
                        // that need patience. Shortening what the user actually waits for is a
                        // question about the NATIVE budgets, and a separate one (RemEx-r89n).
                        //
                        // The margin does NOT cover time spent queued on the native pairing lock,
                        // which is unbudgeted — behind a connection-screen auto-pair this can fire
                        // before the attempt even starts. The outcome is a spurious "timed out" and
                        // then an immediate unwind once the attempt does begin, because the abort is
                        // recorded against its id and consumed at the start. Not a hang.
                        withTimeout(95_000) {
                            RemexCoreClient.StartPairing(
                                            hostUrl,
                                            clientName,
                                            clientVersion,
                                            clientId
                                    )
                                    .getOrNull()
                                    ?: ""
                        }
                    } catch (_: TimeoutCancellationException) {
                        startPairingInFlight = false
                        // Cancelled on BOTH exits, not just the happy one. The collector never
                        // completes on its own, and a coroutine does not finish while a child is
                        // still running — so missing this leaks the whole attempt's coroutine and
                        // leaves a stale phase overwriting state after the attempt is over.
                        phases.cancel()
                        _uiState.value =
                                PairingUiState(
                                        isLoading = false,
                                        pairingError =
                                                context.getString(R.string.pairing_error_timeout)
                                )
                        return@launch
                    }

            phases.cancel()
            startPairingInFlight = false
            if (result == "OK") {
                startPairingSucceeded = true
                // After the handshake, auto-fetch the PIN over the already-open native pairing
                // WebSocket (pairing_pin_request) ONLY when the caller has determined the transport
                // is trusted (loopback or an active Tailscale/WireGuard tunnel). The host enforces
                // the SAME TransportTrust gate on its side; on untrusted transports it replies with
                // no PIN and the user types it manually, so the PIN keeps its out-of-band, anti-MITM
                // value. The old trust-all HTTPS fetch is gone — this uses the .NET-trusted socket,
                // which Google Play's ASI scanner does not flag.
                //
                // The 8s bound is real now. It was not until RemEx-defb: the native call is a
                // blocking JNI frame, and cancellation cannot interrupt one, so this timeout used to
                // be observed only once the native side returned on its own. RemexCoreClient now
                // runs these on a thread it can walk away from AND tells the native side to abandon
                // the attempt, so giving up here actually gives up.
                // Hoisted out of the fetch branch because the reply is read TWICE now — once for a
                // PIN, once for whether the session survived — and those are different questions.
                //
                // FetchPairingPin hops to Dispatchers.IO itself (RemEx-uach). Caught by TYPE rather
                // than by runCatching: runCatching would also swallow the CancellationException
                // raised when the surrounding scope dies, and this continues on to publish UI state
                // afterwards.
                var abandonedByUs = false
                val raw: String? =
                        if (allowAutoPin) {
                            try {
                                withTimeout(8_000) { RemexCoreClient.FetchPairingPin().getOrNull() }
                            } catch (_: TimeoutCancellationException) {
                                // OUR timeout kills the session just as surely as the native one.
                                // Reaching here means the abandon was already sent, which cancels
                                // the native read — and a cancelled read aborts the socket. The
                                // native's reply says so, but nothing can read it: the abandoned
                                // call's result is discarded by construction. So the fact has to be
                                // carried across from here (RemEx-d3z9).
                                abandonedByUs = true
                                null
                            }
                        } else null

                val fetchedPin = if (allowAutoPin) parseFetchedPin(raw) else null

                // A FETCH CAN TAKE THE SESSION WITH IT, so the outcome is not merely "did we get a
                // PIN". Bounding the fetch means cancelling a read, and cancelling a read aborts the
                // socket — so a fetch that timed out or was abandoned leaves nothing to type a PIN
                // into (RemEx-d3z9). Inviting manual entry then is inviting a failure the user
                // cannot understand: the submission dies on a dead socket and reports an unknown
                // error. Saying the session is gone is the only followable answer.
                val fetchFailure = if (allowAutoPin) parsePairingError(raw) else null
                val sessionDied = abandonedByUs || PairingErrors.killsSession(fetchFailure?.code)

                _uiState.value = PairingUiState(
                        isLoading = false,
                        autoFilledPin = fetchedPin,
                        sessionDead = sessionDied,
                        pairingError =
                                if (sessionDied) {
                                    context.getString(pairingErrorMessageRes(fetchFailure?.code))
                                } else null,
                        // Only flag a failure when we actually attempted a fetch (trusted transport)
                        // and got nothing back — that's when the "enter the PIN shown on your PC"
                        // notice is helpful. On untrusted transports we never tried, so no notice.
                        // Suppressed when the session died, because that message is the useful one
                        // and two notices at once contradict each other.
                        autoPinFetchFailed = allowAutoPin && fetchedPin == null && !sessionDied,
                )
            } else {
                // Every branch yields a COMPLETE, self-contained message, matching how submitPin()
                // above builds its error. Keep it that way: nesting one advice-bearing string
                // inside another gave the user two next steps, two sentence terminators, and — on
                // the unknown branch — contradictory instructions, which is why RemEx-meqm was
                // reverted twice before landing. RemEx-6gkr then removed the last wrapper here: the
                // cause code selects a single finished sentence instead, which orphaned
                // pairing_error_start_failed. Removing it, along with pairing_error_generic (which
                // was already unreferenced before this change), is RemEx-fegh — kept separate so
                // the 9-locale parity sweep stands on its own.
                val message =
                        when {
                            result.startsWith("ERROR: ") -> {
                                // Same as submitPin above: map the native cause code to a localized
                                // sentence and log the diagnostic. This supersedes wrapping the raw
                                // English detail in pairing_error_start_failed (RemEx-6gkr).
                                val failure = parsePairingError(result)
                                android.util.Log.w(
                                        "PairingViewModel",
                                        "Pairing start failed [${failure.code ?: "no code"}]: ${failure.detail}"
                                )
                                context.getString(pairingErrorMessageRes(failure.code))
                            }
                            result.isBlank() -> context.getString(R.string.pairing_error_reach_failed)
                            else -> {
                                android.util.Log.w("PairingViewModel", "Unknown pairing error: $result")
                                context.getString(R.string.pairing_error_unknown)
                            }
                        }
                _uiState.value = PairingUiState(isLoading = false, pairingError = message)
            }
        }
    }

    /**
     * Parses the native FetchPairingPin result string into a 6-digit PIN, or null. Accepts ONLY the
     * "OK:<pin>|<expiryUnixMs>" success shape with an exactly-6-digit numeric PIN; "UNSUPPORTED",
     * "ERROR: ...", blank, and any other/malformed input all yield null. Pure and total so it can be
     * unit-tested without the native layer (RemEx-1t0b test plan). Replaces the deleted trust-all
     * HTTPS auto-fetch (httpFetchPin/tryFetchPinFromHost) that Google Play's ASI scanner flagged.
     */
    internal fun parseFetchedPin(raw: String?): String? {
        if (raw == null || !raw.startsWith("OK:")) return null
        val pin = raw.substring(3).substringBefore("|")
        return pin.takeIf { it.length == 6 && it.all { c -> c.isDigit() } }
    }

    // The failure parser and the code→string mapping live in [PairingErrors] rather than here,
    // because RemexClientManager's auto-pair path needs them too and is not a ViewModel. These
    // delegates keep the call sites above short and keep the unit tests bound to this surface.
    internal fun parsePairingError(raw: String?): PairingFailure = PairingErrors.parse(raw)

    // This ViewModel only ever backs the dedicated pairing screen, so it binds the surface here
    // rather than making every call site restate it.
    internal fun pairingErrorMessageRes(code: String?): Int =
            PairingErrors.messageRes(code, PairingSurface.Dedicated)

    fun resetPairingState() {
        // Called when the user explicitly retries (taps Retry / Cancel + reopen).
        startPairingInFlight = false
        startPairingSucceeded = false
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairingScreen(
        host: String,
        port: Int,
        onPairSuccess: () -> Unit,
        onCancel: () -> Unit,
        viewModel: PairingViewModel = viewModel()
) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val context = androidx.compose.ui.platform.LocalContext.current

    // HOISTED OUT OF THE catch BLOCK BELOW (RemEx-2evl3). A resource read through
    // LocalContext.current is not configuration-aware, so it can return a stale string after a
    // Configuration change - and this app ships nine locales, which makes a language change the one
    // its users actually perform. stringResource is @Composable and cannot be called from a catch,
    // so the string is read here and closed over.
    val pairingSaveFailedMessage = stringResource(R.string.pairing_error_save_failed)
    val settingsManager = remember(context) { SettingsManager(context.applicationContext) }
    val coroutineScope = rememberCoroutineScope()
    var pin by remember { mutableStateOf("") }

    // Keyed on the parameters it reads, not Unit. Today every host/port change arrives as a new
    // nav entry (the route embeds both), so a change cannot reach this composable without recreating
    // it — but a Unit key states that the effect is independent of its inputs, which is false, and
    // that is the kind of claim a later refactor gets caught by.
    LaunchedEffect(host, port) {
        val hostUrl = "wss://$host:$port/ws"
        val clientId = withContext(Dispatchers.IO) { settingsManager.getOrCreateClientId() }
        // Only allow the PIN to be fetched over the wire when the transport is trusted
        // (loopback or an active Tailscale/WireGuard tunnel). Otherwise the user enters it
        // manually and the PIN retains its out-of-band, anti-MITM value.
        val allowAutoPin =
                com.clindsay94.remex.security.TransportTrust.canAutoFetchPin(context, host)
        // What this phone calls itself, rather than the constant every phone used to send
        // (RemEx-8m3r). The PC keeps this at pairing and shows it from then on, so three phones in
        // one household must not arrive as three identical rows.
        viewModel.startPairing(
                context,
                hostUrl,
                DeviceName.forPairing(context),
                // The app's real version, not the literal every build used to send (RemEx-2zng).
                // The host LOGS this and does nothing else with it — it is not stored, compared or
                // gated on — so the cost of the old literal was one wrong line in the log, on every
                // pairing since 2.0.1. That line is the first thing anyone reads when a phone and a
                // PC disagree, which is what made a cosmetic-looking value worth fixing.
                BuildConfig.VERSION_NAME,
                clientId,
                allowAutoPin
        )
    }

    // Auto-fill PIN when the host relays it over the pairing WebSocket after the handshake
    LaunchedEffect(state.autoFilledPin) {
        val fetched = state.autoFilledPin
        if (!fetched.isNullOrBlank() && pin.isEmpty()) {
            pin = fetched
        }
    }

    PairingScreenContent(
        state = state,
        pin = pin,
        onPinChange = { if (it.length <= 6) pin = it },
        onCancel = onCancel,
        onSubmitPin = {
            coroutineScope.launch {
                val paired =
                        viewModel.submitPin(context, host, port, pin) { hostId, spkiHash, reconnectSecret ->
                            try {
                                RemexCoreClient.SetPinnedHostHash(hostId, spkiHash)
                                RemexCoreClient.SetPinnedHostHash(host, spkiHash)
                                PinnedHostStore.setPin(context, hostId, spkiHash)
                                PinnedHostStore.setPin(context, host, spkiHash)
                                // So forgetting this PC later can find every key it was stored
                                // under, even if the pin has already been partly cleared
                                // (RemEx-uxem).
                                PinnedHostStore.recordAliases(
                                        context,
                                        listOf(hostId, host, spkiHash)
                                )
                                // Persist the PAIR-1 reconnect secret so the next reconnect —
                                // always non-loopback over Tailscale — can answer the host's
                                // ReconnectChallenge. Without this the PIN screen pairs OK but
                                // the first reconnect fails with "Pairing required". Stored under
                                // both keys, mirroring the auto-connect path (RemexClientManager).
                                if (reconnectSecret.isNotBlank()) {
                                    PinnedHostStore.setReconnectSecret(context, hostId, reconnectSecret)
                                    PinnedHostStore.setReconnectSecret(context, host, reconnectSecret)
                                    // Also key by the SPKI hash — stable across all addresses — so
                                    // reconnects retrieve the right secret regardless of which IP
                                    // (LAN / Tailscale) is in use. (RemEx-060g)
                                    PinnedHostStore.setReconnectSecret(context, spkiHash, reconnectSecret)
                                }
                            } catch (e: Exception) {
                                viewModel.setError(
                                        pairingSaveFailedMessage
                                )
                                throw e
                            }
                        }
                if (paired) {
                    onPairSuccess()
                }
            }
        }
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairingScreenContent(
    state: PairingUiState,
    pin: String,
    onPinChange: (String) -> Unit,
    onCancel: () -> Unit,
    onSubmitPin: () -> Unit
) {
    val motionScheme = MaterialTheme.motionScheme
    Scaffold(topBar = {
        RemexFlexibleTopBar(title = stringResource(R.string.pairing_title))
    }) {
            padding ->
        Column(
                // Was a fixed column with no scroll: at large font scale or in landscape the
                // keyboard could cover Submit and Cancel with no way to reach them. Scroll
                // gives an escape, imePadding keeps the viewport above the keyboard
                // (RemEx-a9ci).
                modifier =
                        Modifier.fillMaxSize()
                                .padding(padding)
                                .imePadding()
                                .verticalScroll(rememberScrollState())
                                .padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
        ) {
            Text(
                    text = stringResource(R.string.pairing_prompt),
                    style = MaterialTheme.typography.bodyLarge,
                    modifier = Modifier.padding(bottom = 24.dp)
            )

            OutlinedTextField(
                    value = pin,
                    onValueChange = onPinChange,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    label = { Text(stringResource(R.string.connection_label_pairing_pin)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
            )

            // WHAT IS ACTUALLY HAPPENING, while it happens. The wait can be ninety seconds against
            // a PC that accepts TCP and TLS and then goes quiet, and a bare spinner cannot say which
            // of those is stuck — so the user cannot tell a slow network from a wedged host, or know
            // whether cancelling costs them anything (RemEx-g87x).
            //
            // Gated on isLoading as well as the phase so a token that arrives late, or one this
            // build does not recognise (phaseRes returns null), leaves nothing on screen.
            // REMEMBERED ACROSS THE EXIT ANIMATION. Every exit path builds a fresh state, which
            // nulls the phase in the SAME update that clears isLoading — and AnimatedVisibility keeps
            // its content composed while shrinking. Reading the live value would blank the text and
            // then collapse an empty box, which is a worse ending than the one this replaces.
            val phaseRes = PairingErrors.phaseRes(state.phase)
            var lastPhaseRes by remember { mutableStateOf<Int?>(null) }
            if (phaseRes != null) lastPhaseRes = phaseRes
            AnimatedVisibility(
                    visible = state.isLoading && phaseRes != null,
                    enter =
                            expandVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeIn(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    exit =
                            shrinkVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeOut(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    label = "pairingPhase"
            ) {
                Text(
                        text = lastPhaseRes?.let { stringResource(it) }.orEmpty(),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier =
                                Modifier.fillMaxWidth()
                                        .padding(top = 8.dp)
                                        // Announced to a screen reader as it changes. Text that
                                        // updates silently through a ninety-second wait leaves a
                                        // blind user with exactly the bare-spinner experience this
                                        // change exists to end.
                                        .semantics { liveRegion = LiveRegionMode.Polite }
                )
            }

            // Shown only when a trusted-transport auto-fetch came back empty: guide the
            // non-technical user to read the PIN off their PC and type it in.
            AnimatedVisibility(
                    visible = state.autoPinFetchFailed,
                    enter =
                            expandVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeIn(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    exit =
                            shrinkVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeOut(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    label = "autoPinFetchFailed"
            ) {
                Text(
                        text = stringResource(R.string.pairing_auto_pin_failed),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                )
            }

            AnimatedVisibility(
                    visible = state.pairingError != null,
                    enter =
                            expandVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeIn(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    exit =
                            shrinkVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeOut(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    label = "pairingError"
            ) {
                Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.padding(top = 16.dp)
                ) {
                    Icon(
                            Icons.Default.ErrorOutline,
                            contentDescription = null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
                            tint = MaterialTheme.colorScheme.error
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                            text = state.pairingError ?: "",
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodyMedium
                    )
                }
            }

            Spacer(Modifier.height(32.dp))

            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(16.dp)
            ) {
                OutlinedButton(
                        onClick = onCancel,
                        modifier = Modifier.weight(1f),
                        // Always enabled: if pairing hangs (PC offline mid-pairing), Cancel must
                        // remain the user's way out of the loading state.
                        enabled = true
                ) { Text(stringResource(R.string.pairing_cancel)) }

                Button(
                        onClick = onSubmitPin,
                        modifier = Modifier.weight(1f),
                        // sessionDead means the native pairing session this screen was using is
                        // already gone (see PairingUiState doc comment) — re-enabling Submit here
                        // would just offer a retry that repeats "No active pairing session" forever.
                        // Cancel (always enabled above) is the only path that still works.
                        enabled = pin.length == 6 && !state.isLoading && !state.sessionDead
                ) {
                    AnimatedContent(
                            targetState = state.isLoading,
                            transitionSpec = {
                                    val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                                    fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                            },
                            label = "pairingSubmitContent"
                    ) { loading ->
                        if (loading) {
                            RemexLoadingIndicator(
                                    modifier = Modifier.size(24.dp),
                                    color = MaterialTheme.colorScheme.onPrimary
                            )
                        } else {
                            Text(stringResource(R.string.pairing_submit))
                        }
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun PairingScreenPreview() {
    RemExTheme {
        PairingScreenContent(
            state = PairingUiState(isLoading = false, pairingError = "Invalid PIN entered."),
            pin = "123456",
            onPinChange = {},
            onCancel = {},
            onSubmitPin = {}
        )
    }
}
