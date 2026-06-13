package com.clindsay94.remex.ui.screens

import android.content.Context
import androidx.compose.animation.AnimatedVisibility
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
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class PairingUiState(
    val isLoading: Boolean = false,
    val pairingError: String? = null,
    val autoFilledPin: String? = null,
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
                                    context.getString(R.string.pairing_error_malformed_response)
                    )
            return false
        }

        // Surface the actual native error rather than masking it; this helps both the user
        // and the developer reading logcat understand whether the PIN was wrong, the session
        // expired, or the WebSocket dropped.
        val message =
                when {
                    result.startsWith("ERROR: ") -> result.removePrefix("ERROR: ")
                    result.isBlank() -> context.getString(R.string.pairing_error_empty_response)
                    else -> result
                }
        _uiState.value = PairingUiState(isLoading = false, pairingError = message)
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
                    withContext(Dispatchers.IO) {
                        RemexCoreClient.StartPairing(hostUrl, clientName, clientVersion, clientId)
                                .getOrNull()
                                ?: ""
                    }
            startPairingInFlight = false
            if (result == "OK") {
                startPairingSucceeded = true
                // After the handshake, auto-fetch the PIN from the host's HTTP endpoint ONLY when
                // the caller has determined the transport is trusted (loopback or an active
                // Tailscale/WireGuard tunnel). On those paths the channel is already authenticated
                // and MITM-resistant, so relaying the PIN over it leaks nothing an attacker could
                // use. On plain LAN/internet we deliberately leave it null so the PIN keeps its
                // out-of-band, anti-MITM purpose and the user must type it manually.
                val fetchedPin =
                        if (allowAutoPin) withContext(Dispatchers.IO) { tryFetchPinFromHost(hostUrl) }
                        else null
                _uiState.value = PairingUiState(isLoading = false, autoFilledPin = fetchedPin)
            } else {
                val message =
                        when {
                            result.startsWith("ERROR: ") -> result.removePrefix("ERROR: ")
                            result.isBlank() -> context.getString(R.string.pairing_error_reach_failed)
                            else -> result
                        }
                _uiState.value =
                        PairingUiState(
                                isLoading = false,
                                pairingError = context.getString(R.string.pairing_error_start_failed, message)
                        )
            }
        }
    }

    private suspend fun tryFetchPinFromHost(hostUrl: String): String? {
        val uri = try { java.net.URI(hostUrl) } catch (e: Exception) { return null }
        val base = "https://${uri.host}:${uri.port}"
        // The /pairing-pin endpoint can transiently 404 in the window between the WebSocket
        // handshake returning and the host publishing the active session (TryGetActivePinInfo
        // uses a non-blocking lock acquire), so retry a few times. On the first miss we also
        // POST /start-pairing, which proactively materialises a session and returns the PIN —
        // it safely reuses the already-active session bound to this client's ECDH key.
        repeat(5) { attempt ->
            httpFetchPin("$base/pairing-pin", "GET")?.let { return it }
            if (attempt == 0) httpFetchPin("$base/start-pairing", "POST")?.let { return it }
            kotlinx.coroutines.delay(400)
        }
        return null
    }

    // Trust-all TLS is acceptable here ONLY because this path is gated behind
    // TransportTrust.canAutoFetchPin (loopback or an active Tailscale tunnel): the
    // WireGuard transport already authenticates the peer, so cert verification adds nothing.
    private fun httpFetchPin(urlStr: String, method: String): String? {
        return try {
            val trustAll = arrayOf<javax.net.ssl.TrustManager>(object : javax.net.ssl.X509TrustManager {
                override fun checkClientTrusted(c: Array<java.security.cert.X509Certificate>, a: String) {}
                override fun checkServerTrusted(c: Array<java.security.cert.X509Certificate>, a: String) {}
                override fun getAcceptedIssuers(): Array<java.security.cert.X509Certificate> = arrayOf()
            })
            val sslCtx = javax.net.ssl.SSLContext.getInstance("TLS")
            sslCtx.init(null, trustAll, java.security.SecureRandom())
            val conn = java.net.URL(urlStr).openConnection() as javax.net.ssl.HttpsURLConnection
            conn.sslSocketFactory = sslCtx.socketFactory
            conn.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }
            conn.requestMethod = method
            conn.connectTimeout = 3000
            conn.readTimeout = 3000
            if (method == "POST") { conn.doOutput = true; conn.outputStream.use {} }
            if (conn.responseCode != 200) return null
            val json = conn.inputStream.bufferedReader().readText()
            org.json.JSONObject(json).optString("pin").takeIf { it.length == 6 }
        } catch (e: Exception) { null }
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

    // Auto-fill PIN when the host returns it via HTTP after the WebSocket handshake
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
                        viewModel.submitPin(context, host, port, pin) { hostId, spkiHash ->
                            try {
                                RemexCoreClient.SetPinnedHostHash(hostId, spkiHash)
                                RemexCoreClient.SetPinnedHostHash(host, spkiHash)
                                PinnedHostStore.setPin(context, hostId, spkiHash)
                                PinnedHostStore.setPin(context, host, spkiHash)
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

                Button(
                        onClick = onSubmitPin,
                        modifier = Modifier.weight(1f),
                        enabled = pin.length == 6 && !state.isLoading
                ) {
                    if (state.isLoading) {
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
