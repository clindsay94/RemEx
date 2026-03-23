package com.clindsay94.remex.ui.screens

import android.util.Log
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.RemexCoreClient
import org.json.JSONObject

@Suppress("unused")
@Composable
fun DashboardScreen() {
    var cpuUsage by remember { mutableStateOf("0%") }
    var gpuUsage by remember { mutableStateOf("0%") }
    var ramUsage by remember { mutableStateOf("0 GB") }
    var isConnectedState by remember { mutableStateOf(false) }

    // Binds the C# JNI callbacks to Compose State
    DisposableEffect(Unit) {
        val listener = object : RemexCoreClient.RemexCallback {
            override fun onTelemetryUpdate(telemetryData: String) {
                try {
                    val json = JSONObject(telemetryData)
                    val payload = json.getJSONObject("Telemetry")
                    cpuUsage = "${payload.optInt("CpuLoad", 0)}%"
                    gpuUsage = "${payload.optInt("GpuLoad", 0)}%"
                    ramUsage = "${payload.optDouble("RamUsedGb", 0.0)} GB"
                } catch (e: Exception) {
                    Log.e("DashboardScreen", "Error parsing telemetry JSON", e)
                }
            }

            override fun onConnectionStateChanged(isConnected: Boolean) {
                isConnectedState = isConnected
            }
        }
        RemexCoreClient.setCallback(listener)

        onDispose {
            RemexCoreClient.setCallback(null)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(24.dp)
    ) {
        Text(
            text = if (isConnectedState) "RemEx Telemetry (Connected)" else "RemEx Telemetry (Disconnected)",
            style = MaterialTheme.typography.headlineMedium
        )

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            SensorCard("CPU", cpuUsage, Modifier.weight(1f))
            SensorCard("GPU", gpuUsage, Modifier.weight(1f))
            SensorCard("RAM", ramUsage, Modifier.weight(1f))
        }
    }
}

@Composable
fun SensorCard(title: String, value: String, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.aspectRatio(1f),
        shape = MaterialTheme.shapes.large,
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.secondaryContainer
        )
    ) {
        Column(
            modifier = Modifier
                .padding(16.dp)
                .fillMaxSize(),
            verticalArrangement = Arrangement.Center
        ) {
            Text(
                text = title,
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSecondaryContainer
            )
            Text(
                text = value,
                style = MaterialTheme.typography.headlineSmall,
                color = MaterialTheme.colorScheme.onSecondaryContainer
            )
        }
    }
}
