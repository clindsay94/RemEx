package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun PersonalizationScreen(
    viewModel: PersonalizationViewModel = viewModel(),
    onMenuClick: () -> Unit = {}
) {
    val settings by viewModel.personalization.collectAsState()

    var themeMode by remember(settings.themeMode) { mutableStateOf(settings.themeMode) }
    var palette by remember(settings.themePalette) { mutableStateOf(settings.themePalette) }
    var fontScale by remember(settings.fontScale) { mutableFloatStateOf(settings.fontScale) }
    var cornerRadius by remember(settings.cardCornerRadius) { mutableIntStateOf(settings.cardCornerRadius) }
    var cardOpacity by remember(settings.cardOpacity) { mutableFloatStateOf(settings.cardOpacity) }

    LaunchedEffect(themeMode, palette, fontScale, cornerRadius, cardOpacity) {
        viewModel.save(themeMode, palette, fontScale, cornerRadius, cardOpacity)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Personalization", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = onMenuClick) {
                        Icon(Icons.Default.Menu, contentDescription = "Menu")
                    }
                }
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

            Text(
                text = "Tune palette, typography scale, card corners, and card opacity. Changes are saved automatically.",
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.bodyMedium
            )

            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
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
                Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Palette", fontWeight = FontWeight.SemiBold)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf("default", "cyber", "solar", "monolith").forEach { option ->
                            FilterChip(
                                selected = palette == option,
                                onClick = { palette = option },
                                label = { Text(option.replaceFirstChar { it.uppercase() }) }
                            )
                        }
                    }
                }
            }

            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)) {
                Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Font Scale: ${"%.2f".format(fontScale)}", fontWeight = FontWeight.SemiBold)
                    Slider(
                        value = fontScale,
                        onValueChange = { fontScale = it },
                        valueRange = 0.85f..1.4f
                    )

                    Spacer(modifier = Modifier.height(4.dp))

                    Text("Card Corner Radius: ${cornerRadius}dp", fontWeight = FontWeight.SemiBold)
                    Slider(
                        value = cornerRadius.toFloat(),
                        onValueChange = { cornerRadius = it.toInt() },
                        valueRange = 4f..36f
                    )

                    Spacer(modifier = Modifier.height(4.dp))

                    Text("Card Opacity: ${"%.2f".format(cardOpacity)}", fontWeight = FontWeight.SemiBold)
                    Slider(
                        value = cardOpacity,
                        onValueChange = { cardOpacity = it },
                        valueRange = 0.4f..1.0f
                    )
                }
            }
        }
    }
}
