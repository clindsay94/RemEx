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
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class PairingUiState(
    val isLoading: Boolean = false,
    val pairingError: String? = null
)

class PairingViewModel : ViewModel() {
    private val _uiState = MutableStateFlow(PairingUiState())
    val uiState: StateFlow<PairingUiState> = _uiState.asStateFlow()

    fun submitPin(
        host: String,
        port: Int,
        pin: String,
        onSuccess: (String, String) -> Unit
    ) {
        viewModelScope.launch {
            _uiState.value = PairingUiState(isLoading = true)
            // The JNI integration logic for pairing is handled via RemexCoreClient.
            // Since submitPin Native is synchronous, we use a coroutine to offload it if needed,
            // or simply call it here. The actual hook into native will be added next.
            // For now, this is a placeholder where the native call will be made.
            
            val result = RemexCoreClient.SubmitPairingPin(pin)
            if (result.startsWith("OK:")) {
                val parts = result.substring(3).split("|")
                if (parts.size >= 2) {
                    onSuccess(parts[0], parts[1])
                    return@launch
                }
            }
            
            _uiState.value = PairingUiState(isLoading = false, pairingError = "Invalid PIN or pairing failed")
        }
    }

    fun setError(msg: String) {
        _uiState.value = _uiState.value.copy(pairingError = msg, isLoading = false)
    }

    fun startPairing(hostUrl: String, clientName: String, clientVersion: String) {
        viewModelScope.launch {
            _uiState.value = PairingUiState(isLoading = true)
            val result = RemexCoreClient.StartPairing(hostUrl, clientName, clientVersion)
            if (result != "OK") {
                _uiState.value = PairingUiState(isLoading = false, pairingError = "Failed to start pairing: $result")
            } else {
                _uiState.value = PairingUiState(isLoading = false)
            }
        }
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

    LaunchedEffect(Unit) {
        // Build the URL to pass to StartPairing
        val protocol = "wss" // We only use wss now
        val hostUrl = "$protocol://$host:$port"
        viewModel.startPairing(hostUrl, "Android Client", "2.0.0")
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.pairing_title)) }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
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
                ) {
                    Text(stringResource(R.string.pairing_cancel))
                }

                val context = androidx.compose.ui.platform.LocalContext.current
                Button(
                    onClick = {
                        viewModel.submitPin(host, port, pin) { hostId, spkiHash ->
                            PinnedHostStore.setPin(context, hostId, spkiHash)
                            onPairSuccess()
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
