package com.clindsay94.remex.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class PairingUiState(val isLoading: Boolean = false, val pairingError: String? = null)

class PairingViewModel : ViewModel() {
    private val _uiState = MutableStateFlow(PairingUiState())
    val uiState: StateFlow<PairingUiState> = _uiState.asStateFlow()

    // Reentrancy guards. The pairingRequired SharedFlow has replay=1, so each
    // PairingScreen recomposition would otherwise re-fire StartPairing in parallel
    // and race on the static _pairingWebSocket in the native layer.
    @Volatile private var startPairingInFlight: Boolean = false
    @Volatile private var startPairingSucceeded: Boolean = false

    suspend fun submitPin(
            host: String,
            port: Int,
            pin: String,
            onSuccess: suspend (String, String) -> Unit
    ): Boolean {
        _uiState.value = PairingUiState(isLoading = true)

        val result =
                withContext(Dispatchers.IO) {
                    RemexCoreClient.SubmitPairingPin(pin).getOrNull() ?: ""
                }

        if (result.startsWith("OK:")) {
            val parts = result.substring(3).split("|")
            if (parts.size >= 2) {
                onSuccess(parts[0], parts[1])
                _uiState.value = PairingUiState(isLoading = false)
                return true
            }

            _uiState.value =
                    PairingUiState(
                            isLoading = false,
                            pairingError =
                                    "Pairing succeeded but the native response was malformed."
                    )
            return false
        }

        // Surface the actual native error rather than masking it; this helps both the user
        // and the developer reading logcat understand whether the PIN was wrong, the session
        // expired, or the WebSocket dropped.
        val message =
                when {
                    result.startsWith("ERROR: ") -> result.removePrefix("ERROR: ")
                    result.isBlank() -> "Pairing failed (empty native response)"
                    else -> result
                }
        _uiState.value = PairingUiState(isLoading = false, pairingError = message)
        return false
    }

    fun setError(msg: String) {
        _uiState.value = _uiState.value.copy(pairingError = msg, isLoading = false)
    }

    fun startPairing(hostUrl: String, clientName: String, clientVersion: String, clientId: String) {
        // Reentrancy guard: don't kick off a second StartPairing while one is in flight,
        // and don't redo a successful one (the WebSocket is already alive on the native side).
        if (startPairingInFlight || startPairingSucceeded) return
        startPairingInFlight = true

        viewModelScope.launch {
            _uiState.value = PairingUiState(isLoading = true)
            val result =
                    withContext(Dispatchers.IO) {
                        RemexCoreClient.StartPairing(hostUrl, clientName, clientVersion, clientId)
                                .getOrNull()
                                ?: ""
                    }
            startPairingInFlight = false
            if (result == "OK") {
                startPairingSucceeded = true
                _uiState.value = PairingUiState(isLoading = false)
            } else {
                val message =
                        when {
                            result.startsWith("ERROR: ") -> result.removePrefix("ERROR: ")
                            result.isBlank() -> "Could not reach host (empty native response)"
                            else -> result
                        }
                _uiState.value =
                        PairingUiState(
                                isLoading = false,
                                pairingError = "Could not start pairing: $message"
                        )
            }
        }
    }

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
    val state by viewModel.uiState.collectAsState()
    var pin by remember { mutableStateOf("") }
    val context = androidx.compose.ui.platform.LocalContext.current
    val settingsManager = remember(context) { SettingsManager(context.applicationContext) }

    LaunchedEffect(Unit) {
        // Build the URL to pass to StartPairing.
        // Path must match RemexConstants.WebSocketPath on the host side ("/ws").
        val hostUrl = "wss://$host:$port/ws"
        val clientId = withContext(Dispatchers.IO) { settingsManager.getOrCreateClientId() }
        viewModel.startPairing(hostUrl, "Android Client", "2.0.0", clientId)
    }

    Scaffold(topBar = { TopAppBar(title = { Text(stringResource(R.string.pairing_title)) }) }) {
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
                    onValueChange = { if (it.length <= 6) pin = it },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    label = { Text("PIN") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
            )

            AnimatedVisibility(visible = state.pairingError != null) {
                Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.padding(top = 16.dp)
                ) {
                    Icon(
                            Icons.Default.ErrorOutline,
                            contentDescription = "Error",
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
                        enabled = !state.isLoading
                ) { Text(stringResource(R.string.pairing_cancel)) }

                val coroutineScope = rememberCoroutineScope()
                Button(
                        onClick = {
                            coroutineScope.launch {
                                val paired =
                                        viewModel.submitPin(host, port, pin) { hostId, spkiHash ->
                                            try {
                                                RemexCoreClient.SetPinnedHostHash(hostId, spkiHash)
                                                RemexCoreClient.SetPinnedHostHash(host, spkiHash)
                                                PinnedHostStore.setPin(context, hostId, spkiHash)
                                                PinnedHostStore.setPin(context, host, spkiHash)
                                            } catch (e: Exception) {
                                                // If setPin still fails after recovery attempts,
                                                // surface it to the ViewModel
                                                viewModel.setError(
                                                        e.message
                                                                ?: "Failed to save pinned host securely."
                                                )
                                                // Re-throw to prevent calling onPairSuccess()
                                                throw e
                                            }
                                        }
                                if (paired) {
                                    onPairSuccess()
                                }
                            }
                        },
                        modifier = Modifier.weight(1f),
                        enabled = pin.length == 6 && !state.isLoading
                ) {
                    if (state.isLoading) {
                        CircularProgressIndicator(
                                modifier = Modifier.size(24.dp),
                                color = MaterialTheme.colorScheme.onPrimary,
                                strokeWidth = 2.dp
                        )
                    } else {
                        Text(stringResource(R.string.pairing_submit))
                    }
                }
            }
        }
    }
}
