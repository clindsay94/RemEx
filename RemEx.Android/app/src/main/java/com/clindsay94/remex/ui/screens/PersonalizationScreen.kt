package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.graphics.toColorInt
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.ui.theme.cardShape
import com.clindsay94.remex.ui.theme.materialShapesList

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun PersonalizationScreen(
    viewModel: PersonalizationViewModel = viewModel()
) {
    val settingsState by viewModel.personalization.collectAsState()

    if (settingsState == null) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text("Personalization", fontWeight = FontWeight.Bold) }
                )
            }
        ) { padding ->
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding),
                contentAlignment = Alignment.Center
            ) {
                CircularProgressIndicator()
            }
        }
        return
    }

    val settings = settingsState!!

    var themeMode by remember { mutableStateOf(settings.themeMode) }
    var palette by remember { mutableStateOf(settings.themePalette) }
    var themeStyle by remember { mutableStateOf(settings.themeStyle) }
    var seedColor by remember { mutableStateOf(settings.themeSeedColor) }
    var fontFamily by remember { mutableStateOf(settings.fontFamily) }
    var fontScale by remember { mutableFloatStateOf(settings.fontScale) }
    var cornerRadius by remember { mutableIntStateOf(settings.cardCornerRadius) }
    var cardOpacity by remember { mutableFloatStateOf(settings.cardOpacity) }
    var pcCardShapePreset by remember { mutableFloatStateOf(settings.pcCardShapePreset) }
    var telemetryCardShapePreset by remember { mutableFloatStateOf(settings.telemetryCardShapePreset) }
    var appLauncherCardShapePreset by remember { mutableFloatStateOf(settings.appLauncherCardShapePreset) }
    var taskManagerCardShapePreset by remember { mutableFloatStateOf(settings.taskManagerCardShapePreset) }
    var remoteDesktopCardShapePreset by remember { mutableFloatStateOf(settings.remoteDesktopCardShapePreset) }
    var remoteControlCardShapePreset by remember { mutableFloatStateOf(settings.remoteControlCardShapePreset) }
    var remoteMouseCardShapePreset by remember { mutableFloatStateOf(settings.remoteMouseCardShapePreset) }

    LaunchedEffect(
        themeMode,
        palette,
        themeStyle,
        seedColor,
        fontFamily,
        fontScale,
        cornerRadius,
        cardOpacity,
        pcCardShapePreset,
        telemetryCardShapePreset,
        appLauncherCardShapePreset,
        taskManagerCardShapePreset,
        remoteDesktopCardShapePreset,
        remoteControlCardShapePreset,
        remoteMouseCardShapePreset
    ) {
        viewModel.save(
            themeMode = themeMode,
            themePalette = palette,
            themeStyle = themeStyle,
            themeSeedColor = seedColor,
            fontFamily = fontFamily,
            fontScale = fontScale,
            cardCornerRadius = cornerRadius,
            cardOpacity = cardOpacity,
            pcCardShapePreset = pcCardShapePreset,
            telemetryCardShapePreset = telemetryCardShapePreset,
            appLauncherCardShapePreset = appLauncherCardShapePreset,
            taskManagerCardShapePreset = taskManagerCardShapePreset,
            remoteDesktopCardShapePreset = remoteDesktopCardShapePreset,
            remoteControlCardShapePreset = remoteControlCardShapePreset,
            remoteMouseCardShapePreset = remoteMouseCardShapePreset
        )
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Personalization", fontWeight = FontWeight.Bold) }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Text(
                text = "Material 3 Expressive controls",
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold
            )

            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(
                    modifier = Modifier.padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text("Theme Mode", fontWeight = FontWeight.SemiBold)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("system", "light", "dark").forEach { option ->
                            FilterChip(
                                selected = themeMode == option,
                                onClick = { themeMode = option },
                                label = { Text(option.replaceFirstChar { it.uppercase() }) }
                            )
                        }
                    }
                }
            }

            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(
                    modifier = Modifier.padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text("Palette Style", fontWeight = FontWeight.SemiBold)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("tonal_spot", "expressive", "fruit_salad", "rainbow", "vibrant").forEach { option ->
                            FilterChip(
                                selected = themeStyle == option,
                                onClick = { themeStyle = option },
                                label = { Text(option.replace("_", " ").split(" ").joinToString(" ") { it.replaceFirstChar { it.uppercase() } }) }
                            )
                        }
                    }

                    Text("Palette Mode", fontWeight = FontWeight.SemiBold)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("default", "custom").forEach { option ->
                            FilterChip(
                                selected = palette == option,
                                onClick = { palette = option },
                                label = { Text(option.replaceFirstChar { it.uppercase() }) }
                            )
                        }
                    }

                    if (palette == "custom") {
                        Spacer(modifier = Modifier.height(8.dp))
                        Text("Seed Color", style = MaterialTheme.typography.labelMedium)

                        val colors = listOf(
                            "#6750A4", "#0061A4", "#006A60", "#7D5260",
                            "#625B71", "#BA1A1A", "#686000", "#006D31"
                        )

                        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            colors.forEach { colorHex ->
                                val colorValue = Color(colorHex.toColorInt())
                                Box(
                                    modifier = Modifier
                                        .size(40.dp)
                                        .background(colorValue, MaterialTheme.shapes.small)
                                        .padding(4.dp)
                                        .clickable { seedColor = colorHex }
                                        .let {
                                            if (seedColor.equals(colorHex, ignoreCase = true)) {
                                                it.background(
                                                    MaterialTheme.colorScheme.onSurface.copy(alpha = 0.3f),
                                                    MaterialTheme.shapes.small
                                                )
                                            } else it
                                        }
                                )
                            }
                        }

                        OutlinedTextField(
                            value = seedColor,
                            onValueChange = { seedColor = it },
                            label = { Text("Hex Color Code") },
                            modifier = Modifier.fillMaxWidth(),
                            singleLine = true
                        )
                    }
                }
            }

            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(
                    modifier = Modifier.padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text("Typography", fontWeight = FontWeight.SemiBold)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("default", "roboto", "lato", "montserrat", "poppins", "mono", "cursive").forEach { option ->
                            FilterChip(
                                selected = fontFamily == option,
                                onClick = { fontFamily = option },
                                label = { Text(option.replaceFirstChar { it.uppercase() }) }
                            )
                        }
                    }

                    Text("Font Scale: ${"%.2f".format(fontScale)}", fontWeight = FontWeight.SemiBold)
                    Slider(value = fontScale, onValueChange = { fontScale = it }, valueRange = 0.85f..1.4f)
                }
            }

            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(
                    modifier = Modifier.padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text("Card General Style", fontWeight = FontWeight.SemiBold)
                    Text("Corner Radius: ${cornerRadius}dp", style = MaterialTheme.typography.bodySmall)
                    Slider(value = cornerRadius.toFloat(), onValueChange = { cornerRadius = it.toInt() }, valueRange = 0f..40f)

                    Text("Opacity: ${"%.2f".format(cardOpacity)}", style = MaterialTheme.typography.bodySmall)
                    Slider(value = cardOpacity, onValueChange = { cardOpacity = it }, valueRange = 0.4f..1.0f)

                    Spacer(modifier = Modifier.height(8.dp))
                    Text("Individual Card Shapes", fontWeight = FontWeight.Bold)

                    val cards = listOf(
                        "PC Status" to { v: Float -> pcCardShapePreset = v } to pcCardShapePreset,
                        "Telemetry" to { v: Float -> telemetryCardShapePreset = v } to telemetryCardShapePreset,
                        "App Launcher" to { v: Float -> appLauncherCardShapePreset = v } to appLauncherCardShapePreset,
                        "Task Manager" to { v: Float -> taskManagerCardShapePreset = v } to taskManagerCardShapePreset,
                        "Remote Desktop" to { v: Float -> remoteDesktopCardShapePreset = v } to remoteDesktopCardShapePreset,
                        "Remote Control" to { v: Float -> remoteControlCardShapePreset = v } to remoteControlCardShapePreset,
                        "Remote Mouse" to { v: Float -> remoteMouseCardShapePreset = v } to remoteMouseCardShapePreset
                    )

                    val maxShapes = (materialShapesList.size - 1).toFloat()

                    cards.forEach { (pair, current) ->
                        val (label, setter) = pair
                        Spacer(modifier = Modifier.height(24.dp))
                        Text(label, style = MaterialTheme.typography.labelLarge)
                        
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(16.dp),
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Slider(
                                value = current,
                                onValueChange = { setter(it) },
                                valueRange = 0f..maxShapes,
                                modifier = Modifier.weight(1f)
                            )
                            
                            Box(
                                modifier = Modifier
                                    .size(80.dp)
                                    .clip(cardShape(current, cornerRadius))
                                    .background(MaterialTheme.colorScheme.primary)
                            )
                        }
                    }
                }
            }
        }
    }
}
