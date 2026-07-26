package com.clindsay94.remex.ui.screens

import android.content.Context
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
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

data class PairingUiState(
    val isLoading: Boolean = false,
    val pairingError: String? = null,
    val autoFilledPin: String? = null,
    // True when an auto-fetch was attempted on a trusted transport but returned no PIN, so the
    // screen can prompt the user to type the PIN shown on the PC. Never set on untrusted transports
    // (where we deliberately never attempt an auto-fetch), so it doesn't nag in the normal manual case.
    val autoPinFetchFailed: Boolean = false,
    // True once a native SubmitPin call has returned one of the five reachable "ERROR: " causes
    // (wrong PIN, expired session, lost key state, confirm timeout, unexpected exception). Every one
    // of those causes the native side to call ClearActivePairingState(), so the pairing session this
    // screen was using is gone for good — re-submitting, with the same or a different PIN, can only
    // repeat the same failure. Submit must stay disabled once this is true; only Cancel (which
    // disposes this screen/ViewModel and lets the user start a fresh pairing attempt) still works.
    // See RemEx-aor9.
    val sessionDead: Boolean = false,
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
                    withTimeout(15_000) {
                        withContext(Dispatchers.IO) {
                            RemexCoreClient.SubmitPairingPin(pin).getOrNull() ?: ""
                        }
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
                    // session, lost key state, confirm timeout, or an unexpected exception. All
                    // five are developer-grade English naming ports, paths and cert internals, so
                    // the diagnostic is logged and the failure's cause code selects a localized
                    // sentence for the user instead (RemEx-6gkr). Nothing logged it before, so
                    // logcat gains what the screen loses.
                    //
                    // Four of the five resolve to advice that names Cancel, because Submit stays
                    // enabled here while re-using a session that is already unusable — so "get a
                    // fresh PIN and try again", followed literally in place, just comes back "No
                    // active pairing session" and repeats forever. See the dead-session note in
                    // PairingErrors, which also explains why the connection screen needs the
                    // opposite advice. The fifth, an unexpected exception, is unclassified and
                    // falls to the generic message.
                    //
                    // Careful if the Submit predicate ever loosens: SubmitPairingPinNative has a
                    // SIXTH error return, "PIN is required" (ARG_MISSING), and it is the one case
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

        viewModelScope.launch {
            _uiState.value = PairingUiState(isLoading = true)
            val result =
                    try {
                        withTimeout(15_000) {
                            withContext(Dispatchers.IO) {
                                RemexCoreClient.StartPairing(
                                                hostUrl,
                                                clientName,
                                                clientVersion,
                                                clientId
                                        )
                                        .getOrNull()
                                        ?: ""
                            }
                        }
                    } catch (_: TimeoutCancellationException) {
                        startPairingInFlight = false
                        _uiState.value =
                                PairingUiState(
                                        isLoading = false,
                                        pairingError =
                                                context.getString(R.string.pairing_error_timeout)
                                )
                        return@launch
                    }
            startPairingInFlight = false
            if (result == "OK") {
                startPairingSucceeded = true
                // After the handshake, auto-fetch the PIN over the already-open native pairing
                // WebSocket (pairing_pin_request) ONLY when the caller has determined the transport
                // is trusted (loopback or an active Tailscale/WireGuard tunnel). The host enforces
                // the SAME TransportTrust gate on its side; on untrusted transports it replies with
                // no PIN and the user types it manually, so the PIN keeps its out-of-band, anti-MITM
                // value. The old trust-all HTTPS fetch is gone — this uses the .NET-trusted socket,
                // which Google Play's ASI scanner does not flag. The 8s outer bound is belt-and-
                // braces over the native call's own 5s budget.
                val fetchedPin: String? =
                        if (allowAutoPin) {
                            val raw = withContext(Dispatchers.IO) {
                                runCatching {
                                    withTimeout(8_000) { RemexCoreClient.FetchPairingPin().getOrNull() }
                                }.getOrNull()
                            }
                            parseFetchedPin(raw)
                        } else null
                _uiState.value = PairingUiState(
                        isLoading = false,
                        autoFilledPin = fetchedPin,
                        // Only flag a failure when we actually attempted a fetch (trusted transport)
                        // and got nothing back — that's when the "enter the PIN shown on your PC"
                        // notice is helpful. On untrusted transports we never tried, so no notice.
                        autoPinFetchFailed = allowAutoPin && fetchedPin == null,
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
    val settingsManager = remember(context) { SettingsManager(context.applicationContext) }
    val coroutineScope = rememberCoroutineScope()
    var pin by remember { mutableStateOf("") }

    LaunchedEffect(Unit) {
        val hostUrl = "wss://$host:$port/ws"
        val clientId = withContext(Dispatchers.IO) { settingsManager.getOrCreateClientId() }
        // Only allow the PIN to be fetched over the wire when the transport is trusted
        // (loopback or an active Tailscale/WireGuard tunnel). Otherwise the user enters it
        // manually and the PIN retains its out-of-band, anti-MITM value.
        val allowAutoPin =
                com.clindsay94.remex.security.TransportTrust.canAutoFetchPin(context, host)
        viewModel.startPairing(context, hostUrl, "Android Client", "2.0.0", clientId, allowAutoPin)
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
                                        context.getString(R.string.pairing_error_save_failed)
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
                modifier = Modifier.fillMaxSize().padding(padding).padding(24.dp),
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
                            contentDescription = stringResource(R.string.cd_error_icon),
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
