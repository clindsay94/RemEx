package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.graphics.toColorInt
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.ui.theme.cardShape
import com.clindsay94.remex.ui.theme.materialShapesList
import com.google.android.material.color.utilities.Hct
import com.google.android.material.color.utilities.TonalPalette
import kotlin.math.roundToInt

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun PersonalizationScreen(
    viewModel: PersonalizationViewModel = viewModel()
) {
    val settingsState by viewModel.personalization.collectAsState()

    if (settingsState == null) {
        Scaffold(
            topBar = { TopAppBar(title = { Text("Personalization", fontWeight = FontWeight.Bold) }) }
        ) { padding ->
            Box(modifier = Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
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
        themeMode, palette, themeStyle, seedColor, fontFamily, fontScale, cornerRadius,
        cardOpacity, pcCardShapePreset, telemetryCardShapePreset, appLauncherCardShapePreset,
        taskManagerCardShapePreset, remoteDesktopCardShapePreset, remoteControlCardShapePreset,
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
        topBar = { TopAppBar(title = { Text("Personalization", fontWeight = FontWeight.Bold) }) }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(20.dp)
        ) {
            Text(
                text = "Appearance Studio",
                style = MaterialTheme.typography.headlineMedium,
                fontWeight = FontWeight.Black,
                letterSpacing = (-1).sp
            )

            // ═══ Theme Mode & Style ═══
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    SectionHeader("Theme & Strategy", Icons.Default.Tune)
                    
                    Text("Display Mode", style = MaterialTheme.typography.labelMedium)
                    SingleSelectChips(
                        options = listOf("system", "light", "dark"),
                        selected = themeMode,
                        onSelected = { themeMode = it }
                    )

                    Text("Dynamic Palette Strategy", style = MaterialTheme.typography.labelMedium)
                    SingleSelectChips(
                        options = listOf("tonal_spot", "expressive", "fruit_salad", "rainbow", "vibrant"),
                        selected = themeStyle,
                        onSelected = { themeStyle = it }
                    )
                }
            }

            // ═══ Ultimate Color Studio ═══
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    SectionHeader("Color Studio", Icons.Default.Palette)
                    
                    Text("Palette Mode", style = MaterialTheme.typography.labelMedium)
                    SingleSelectChips(
                        options = listOf("default", "custom"),
                        selected = palette,
                        onSelected = { palette = it }
                    )

                    if (palette == "custom") {
                        val currentHct = remember(seedColor) { 
                            try { Hct.fromInt(seedColor.toColorInt()) } catch (e: Exception) { Hct.fromInt(0xFF6750A4.toInt()) }
                        }

                        Spacer(modifier = Modifier.height(8.dp))
                        
                        // Hue Slider
                        Text("Hue: ${currentHct.hue.roundToInt()}°", style = MaterialTheme.typography.labelSmall)
                        HueSlider(
                            value = currentHct.hue.toFloat(),
                            onValueChange = { newHue ->
                                val updated = Hct.from(newHue.toDouble(), currentHct.chroma, currentHct.tone)
                                seedColor = String.format("#%06X", (0xFFFFFF and updated.toInt()))
                            }
                        )

                        // Tonal Row
                        Text("Tonal Range", style = MaterialTheme.typography.labelSmall)
                        TonalRow(hct = currentHct)

                        // Mini Dashboard Preview
                        Spacer(modifier = Modifier.height(12.dp))
                        Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                            MiniCardPreview(
                                seedColor = seedColor,
                                shapePreset = telemetryCardShapePreset,
                                cornerRadius = cornerRadius,
                                opacity = cardOpacity
                            )
                        }

                        OutlinedTextField(
                            value = seedColor,
                            onValueChange = { if (it.length <= 7) seedColor = it },
                            label = { Text("Manual Hex Code") },
                            modifier = Modifier.fillMaxWidth(),
                            singleLine = true,
                            textStyle = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Bold)
                        )
                    }
                }
            }

            // ═══ Individual Card Shapes ═══
            Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    SectionHeader("Expressive Geometry", Icons.Default.Tune)
                    
                    Text("Corner Base: ${cornerRadius}dp", style = MaterialTheme.typography.labelMedium)
                    Slider(value = cornerRadius.toFloat(), onValueChange = { cornerRadius = it.toInt() }, valueRange = 4f..36f)

                    val shapeConfigs = listOf(
                        "Telemetry cards" to telemetryCardShapePreset to { v: Float -> telemetryCardShapePreset = v },
                        "App Launcher" to appLauncherCardShapePreset to { v: Float -> appLauncherCardShapePreset = v },
                        "Task Manager" to taskManagerCardShapePreset to { v: Float -> taskManagerCardShapePreset = v },
                        "PC Connection Orb" to pcCardShapePreset to { v: Float -> pcCardShapePreset = v }
                    )

                    val maxShapes = (materialShapesList.size - 1).toFloat()

                    shapeConfigs.forEach { (config, setter) ->
                        val (label, current) = config
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(label, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Bold)
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                Slider(value = current, onValueChange = setter, valueRange = 0f..maxShapes, modifier = Modifier.weight(1f))
                                Box(
                                    modifier = Modifier
                                        .size(56.dp)
                                        .clip(cardShape(current, cornerRadius))
                                        .background(MaterialTheme.colorScheme.primary)
                                )
                            }
                        }
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(40.dp))
        }
    }
}

@Composable
private fun SectionHeader(title: String, icon: androidx.compose.ui.graphics.vector.ImageVector) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Black, color = MaterialTheme.colorScheme.primary)
    }
}

@Composable
private fun SingleSelectChips(options: List<String>, selected: String, onSelected: (String) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        options.forEach { option ->
            FilterChip(
                selected = selected == option,
                onClick = { onSelected(option) },
                label = { Text(option.replace("_", " ").split(" ").joinToString(" ") { it.replaceFirstChar { char -> char.uppercase() } }) }
            )
        }
    }
}

@Composable
private fun HueSlider(value: Float, onValueChange: (Float) -> Unit) {
    val gradient = remember {
        Brush.horizontalGradient(
            (0..360).map { Color.hsv(it.toFloat(), 1f, 1f) }
        )
    }
    Box(modifier = Modifier.fillMaxWidth().height(32.dp).clip(CircleShape).background(gradient)) {
        Slider(
            value = value,
            onValueChange = onValueChange,
            valueRange = 0f..360f,
            colors = SliderDefaults.colors(
                thumbColor = Color.White,
                activeTrackColor = Color.Transparent,
                inactiveTrackColor = Color.Transparent
            ),
            modifier = Modifier.fillMaxSize()
        )
    }
}

@Composable
private fun TonalRow(hct: Hct) {
    val tones = listOf(10, 30, 50, 70, 90, 95)
    val palette = remember(hct) { TonalPalette.fromHueAndChroma(hct.hue, hct.chroma) }
    
    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        tones.forEach { tone ->
            Box(
                modifier = Modifier
                    .weight(1f)
                    .height(40.dp)
                    .clip(MaterialTheme.shapes.small)
                    .background(Color(palette.tone(tone)))
            )
        }
    }
}

@Composable
private fun MiniCardPreview(seedColor: String, shapePreset: Float, cornerRadius: Int, opacity: Float) {
    val previewColor = remember(seedColor) { try { Color(seedColor.toColorInt()) } catch (e: Exception) { Color(0xFF6750A4.toInt()) } }
    
    Box(
        modifier = Modifier
            .size(width = 140.dp, height = 100.dp)
            .clip(cardShape(shapePreset, cornerRadius))
            .background(previewColor.copy(alpha = opacity))
            .padding(12.dp)
    ) {
        Column(verticalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxSize()) {
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Box(modifier = Modifier.size(16.dp).background(Color.White.copy(alpha = 0.5f), CircleShape))
                Box(modifier = Modifier.width(32.dp).height(8.dp).background(Color.White.copy(alpha = 0.3f), CircleShape))
            }
            Text("PREVIEW", style = MaterialTheme.typography.labelSmall, color = Color.White, fontWeight = FontWeight.Bold)
            Box(modifier = Modifier.fillMaxWidth().height(20.dp).background(Color.White.copy(alpha = 0.2f), CircleShape))
        }
    }
}
