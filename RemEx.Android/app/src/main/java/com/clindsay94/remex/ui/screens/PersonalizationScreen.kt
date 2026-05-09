package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.animation.*
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.graphics.toColorInt
import androidx.core.os.LocaleListCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import androidx.compose.foundation.isSystemInDarkTheme
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding
import com.clindsay94.remex.ui.theme.cardShape
import com.clindsay94.remex.ui.theme.colorSchemeFromSeed
import com.clindsay94.remex.ui.theme.materialShapeNames
import com.clindsay94.remex.ui.theme.materialShapesList
import com.google.android.material.color.utilities.Hct
import com.google.android.material.color.utilities.TonalPalette
import kotlin.math.roundToInt

@OptIn(
        ExperimentalMaterial3Api::class,
        ExperimentalLayoutApi::class
)
@Composable
fun PersonalizationScreen(
        viewModel: PersonalizationViewModel = viewModel(),
        showHeader: Boolean = true
) {
    val view = LocalView.current
    val settingsState by viewModel.personalization.collectAsState()
    val scrollBehavior = rememberRemexTopBarScrollBehavior()

    if (settingsState == null) {
        Scaffold(
                modifier =
                        if (showHeader) Modifier.nestedScroll(scrollBehavior.nestedScrollConnection)
                        else Modifier,
                topBar = {
                    if (showHeader) {
                        RemexFlexibleTopBar(
                                title = stringResource(R.string.screen_personalization_title),
                                scrollBehavior = scrollBehavior
                        )
                    }
                }
        ) { innerPadding ->
            Box(
                    modifier = Modifier.fillMaxSize().padding(innerPadding),
                    contentAlignment = Alignment.Center
            ) { CircularProgressIndicator() }
        }
        return
    }

    val settings = settingsState!!

    var themeMode by remember { mutableStateOf(settings.themeMode) }
    var palette by remember { mutableStateOf(settings.themePalette) }
    var themeStyle by remember { mutableStateOf(settings.themeStyle) }
    var seedColor by remember { mutableStateOf(settings.themeSeedColor) }
    var themeSeedChroma by remember { mutableFloatStateOf(settings.themeSeedChroma) }
    var themeContrast by remember { mutableFloatStateOf(settings.themeContrast) }
    var fontFamily by remember { mutableStateOf(settings.fontFamily) }
    var fontScale by remember { mutableFloatStateOf(settings.fontScale) }
    var cornerRadius by remember { mutableIntStateOf(settings.cardCornerRadius) }
    var cardOpacity by remember { mutableFloatStateOf(settings.cardOpacity) }
    var pcCardShapePreset by remember { mutableFloatStateOf(settings.pcCardShapePreset) }
    var telemetryCardShapePreset by remember {
        mutableFloatStateOf(settings.telemetryCardShapePreset)
    }
    var appLauncherCardShapePreset by remember {
        mutableFloatStateOf(settings.appLauncherCardShapePreset)
    }
    var remoteDesktopCardShapePreset by remember {
        mutableFloatStateOf(settings.remoteDesktopCardShapePreset)
    }
    var remoteControlCardShapePreset by remember {
        mutableFloatStateOf(settings.remoteControlCardShapePreset)
    }
    var remoteMouseCardShapePreset by remember {
        mutableFloatStateOf(settings.remoteMouseCardShapePreset)
    }
    var taskManagerCardShapePreset by remember {
        mutableFloatStateOf(settings.taskManagerCardShapePreset)
    }

    LaunchedEffect(
            themeMode,
            palette,
            themeStyle,
            seedColor,
            themeSeedChroma,
            themeContrast,
            fontFamily,
            fontScale,
            cornerRadius,
            cardOpacity,
            pcCardShapePreset,
            telemetryCardShapePreset,
            appLauncherCardShapePreset,
            remoteDesktopCardShapePreset,
            remoteControlCardShapePreset,
            remoteMouseCardShapePreset,
            taskManagerCardShapePreset
    ) {
        viewModel.save(
                themeMode = themeMode,
                themePalette = palette,
                themeStyle = themeStyle,
                themeSeedColor = seedColor,
                themeSeedChroma = themeSeedChroma,
                themeContrast = themeContrast,
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
            modifier =
                    if (showHeader) Modifier.nestedScroll(scrollBehavior.nestedScrollConnection)
                    else Modifier,
            topBar = {
                if (showHeader) {
                    RemexFlexibleTopBar(
                            title = stringResource(R.string.screen_personalization_title),
                            scrollBehavior = scrollBehavior
                    )
                }
            }
    ) { innerPadding ->
        Column(
                modifier =
                        Modifier.fillMaxSize()
                                .padding(innerPadding)
                                .padding(16.dp)
                                .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(20.dp)
        ) {
            Text(
                    text = stringResource(R.string.personalization_appearance_studio),
                    style = MaterialTheme.typography.headlineMedium
            )

            // ═══ Language Settings ═══
            Card(
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                            ),
                    modifier = Modifier.animateContentSize()
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    SectionHeader(
                            stringResource(R.string.personalization_section_language),
                            Icons.Default.Language
                    )

                    Text(
                            stringResource(R.string.personalization_app_language),
                            style = MaterialTheme.typography.labelMedium
                    )

                    val currentLocales = AppCompatDelegate.getApplicationLocales()
                    val currentLangTag =
                            if (currentLocales.isEmpty) "system"
                            else currentLocales[0]?.toLanguageTag() ?: "system"

                    val supportedLanguages =
                            listOf(
                                    "system" to
                                            stringResource(R.string.personalization_system_default),
                                    "en" to "English",
                                    "es" to "Español",
                                    "fr" to "Français",
                                    "hi" to "हिन्दी",
                                    "in" to "Bahasa Indonesia",
                                    "pl" to "Polski",
                                    "pt-BR" to "Português (BR)",
                                    "tr" to "Türkçe",
                                    "uk" to "Українська"
                            )

                    Card(
                            colors =
                                    CardDefaults.cardColors(
                                            containerColor =
                                                    MaterialTheme.colorScheme.tertiaryContainer
                                                            .copy(alpha = 0.5f)
                                    ),
                            modifier = Modifier.fillMaxWidth()
                    ) {
                        Row(
                                modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                        ) {
                            Icon(
                                    Icons.Default.Info,
                                    contentDescription = null,
                                    tint = MaterialTheme.colorScheme.onTertiaryContainer,
                                    modifier = Modifier.size(16.dp)
                            )
                            Text(
                                    stringResource(R.string.personalization_language_note),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onTertiaryContainer
                            )
                        }
                    }

                    var expanded by remember { mutableStateOf(false) }

                    ExposedDropdownMenuBox(
                            expanded = expanded,
                            onExpandedChange = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                expanded = it
                            }
                    ) {
                        OutlinedTextField(
                                value =
                                        supportedLanguages
                                                .find { it.first == currentLangTag }
                                                ?.second
                                                ?: stringResource(
                                                        R.string.personalization_system_default
                                                ),
                                onValueChange = {},
                                readOnly = true,
                                trailingIcon = {
                                    ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded)
                                },
                                modifier =
                                        Modifier.menuAnchor(
                                                        type =
                                                                ExposedDropdownMenuAnchorType
                                                                        .PrimaryNotEditable,
                                                        enabled = true
                                                )
                                                .fillMaxWidth()
                        )
                        ExposedDropdownMenu(
                                expanded = expanded,
                                onDismissRequest = { expanded = false }
                        ) {
                            supportedLanguages.forEach { (tag, name) ->
                                DropdownMenuItem(
                                        text = { Text(name) },
                                        onClick = {
                                            view.performHapticFeedback(
                                                    HapticFeedbackConstants.KEYBOARD_TAP
                                            )
                                            expanded = false
                                            if (tag == "system") {
                                                AppCompatDelegate.setApplicationLocales(
                                                        LocaleListCompat.getEmptyLocaleList()
                                                )
                                            } else {
                                                AppCompatDelegate.setApplicationLocales(
                                                        LocaleListCompat.forLanguageTags(tag)
                                                )
                                            }
                                        }
                                )
                            }
                        }
                    }
                }
            }

            // ═══ Theme Mode & Style ═══
            Card(
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                            ),
                    modifier = Modifier.animateContentSize()
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    SectionHeader(
                            stringResource(R.string.personalization_section_theme),
                            Icons.Default.Tune
                    )

                    Text(
                            stringResource(R.string.personalization_display_mode),
                            style = MaterialTheme.typography.labelMedium
                    )
                    // M3: SegmentedButton for small exclusive-choice groups
                    val displayModeOptions = listOf("system", "light", "dark")
                    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
                        displayModeOptions.forEachIndexed { index, option ->
                            SegmentedButton(
                                    selected = themeMode == option,
                                    onClick = { themeMode = option },
                                    shape =
                                            SegmentedButtonDefaults.itemShape(
                                                    index,
                                                    displayModeOptions.size
                                            ),
                                    label = { Text(option.replaceFirstChar { it.uppercase() }) }
                            )
                        }
                    }

                    Text(
                            stringResource(R.string.personalization_palette_strategy),
                            style = MaterialTheme.typography.labelMedium
                    )
                    SingleSelectChips(
                            options =
                                    listOf(
                                            "tonal_spot",
                                            "expressive",
                                            "fruit_salad",
                                            "rainbow",
                                            "vibrant"
                                    ),
                            selected = themeStyle,
                            onSelected = { themeStyle = it }
                    )
                }
            }

            // ═══ Ultimate Color Studio ═══
            Card(
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                            ),
                    modifier = Modifier.animateContentSize()
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    SectionHeader(
                            stringResource(R.string.personalization_section_color),
                            Icons.Default.Palette
                    )

                    Text(
                            stringResource(R.string.personalization_palette_mode),
                            style = MaterialTheme.typography.labelMedium
                    )
                    // M3: SegmentedButton for 2-item exclusive choices
                    val paletteModeOptions = listOf("default", "custom")
                    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
                        paletteModeOptions.forEachIndexed { index, option ->
                            SegmentedButton(
                                    selected = palette == option,
                                    onClick = { palette = option },
                                    shape =
                                            SegmentedButtonDefaults.itemShape(
                                                    index,
                                                    paletteModeOptions.size
                                            ),
                                    label = { Text(option.replaceFirstChar { it.uppercase() }) }
                            )
                        }
                    }

                    AnimatedVisibility(
                            visible = palette == "custom",
                            enter =
                                    expandVertically(
                                            animationSpec =
                                                    androidx.compose.animation.core.spring(stiffness = androidx.compose.animation.core.Spring.StiffnessMediumLow)
                                    ) +
                                            fadeIn(
                                                    animationSpec =
                                                            androidx.compose.animation.core.tween(durationMillis = 200)
                                            ),
                            exit =
                                    shrinkVertically(
                                            animationSpec =
                                                    androidx.compose.animation.core.spring(stiffness = androidx.compose.animation.core.Spring.StiffnessMediumLow)
                                    ) +
                                            fadeOut(
                                                    animationSpec =
                                                            androidx.compose.animation.core.tween(durationMillis = 200)
                                            )
                    ) {
                        Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            val currentHct =
                                    remember(seedColor, themeSeedChroma) {
                                        try {
                                            val baseHct = Hct.fromInt(seedColor.toColorInt())
                                            Hct.from(
                                                    baseHct.hue,
                                                    themeSeedChroma.toDouble(),
                                                    baseHct.tone
                                            )
                                        } catch (e: Exception) {
                                            Hct.fromInt(0xFF6750A4.toInt())
                                        }
                                    }

                            Spacer(modifier = Modifier.height(8.dp))

                            // Hue Slider
                            Text(
                                    stringResource(
                                            R.string.personalization_hue_label,
                                            currentHct.hue.roundToInt()
                                    ),
                                    style = MaterialTheme.typography.labelSmall
                            )
                            HueSlider(
                                    value = currentHct.hue.toFloat(),
                                    onValueChange = { newHue ->
                                        val updated =
                                                Hct.from(
                                                        newHue.toDouble(),
                                                        currentHct.chroma,
                                                        currentHct.tone
                                                )
                                        seedColor =
                                                String.format(
                                                        "#%06X",
                                                        (0xFFFFFF and updated.toInt())
                                                )
                                    }
                            )

                            // Chroma Slider
                            Text(
                                    stringResource(
                                            R.string.personalization_chroma_label,
                                            themeSeedChroma.roundToInt()
                                    ),
                                    style = MaterialTheme.typography.labelSmall
                            )
                            Slider(
                                    value = themeSeedChroma,
                                    onValueChange = { themeSeedChroma = it },
                                    onValueChangeFinished = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.CLOCK_TICK
                                        )
                                    },
                                    valueRange = 0f..120f
                            )

                            // Contrast Slider
                            Text(
                                    stringResource(
                                            R.string.personalization_contrast_label,
                                            "%.2f".format(themeContrast)
                                    ),
                                    style = MaterialTheme.typography.labelSmall
                            )
                            Slider(
                                    value = themeContrast,
                                    onValueChange = { themeContrast = it },
                                    onValueChangeFinished = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.CLOCK_TICK
                                        )
                                    },
                                    valueRange = -1.0f..1.0f
                            )

                            // Tonal Row
                            Text(
                                    stringResource(R.string.personalization_tonal_range),
                                    style = MaterialTheme.typography.labelSmall
                            )
                            TonalRow(hct = currentHct)

                            // Mini Dashboard Preview
                            Spacer(modifier = Modifier.height(12.dp))
                            Box(
                                    modifier = Modifier.fillMaxWidth(),
                                    contentAlignment = Alignment.Center
                            ) {
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
                                    label = {
                                        Text(stringResource(R.string.personalization_manual_hex))
                                    },
                                    modifier = Modifier.fillMaxWidth(),
                                    singleLine = true,
                                    textStyle =
                                            MaterialTheme.typography.bodyMedium.copy(
                                                    fontWeight = FontWeight.Bold
                                            )
                            )
                        }
                    }
                }
            }

            // ═══ Individual Card Shapes ═══
            Card(
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                            ),
                    modifier = Modifier.animateContentSize()
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                ) {
                    SectionHeader(
                            stringResource(R.string.personalization_section_geometry),
                            Icons.Default.Tune
                    )

                    Text(
                            stringResource(R.string.personalization_corner_base, cornerRadius),
                            style = MaterialTheme.typography.labelMedium
                    )
                    Slider(
                            value = cornerRadius.toFloat(),
                            onValueChange = { cornerRadius = it.toInt() },
                            onValueChangeFinished = {
                                view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                            },
                            valueRange = 4f..36f
                    )

                    Text(
                            stringResource(
                                    R.string.personalization_card_opacity,
                                    (cardOpacity * 100).roundToInt()
                            ),
                            style = MaterialTheme.typography.labelMedium
                    )
                    Slider(
                            value = cardOpacity,
                            onValueChange = { cardOpacity = it },
                            onValueChangeFinished = {
                                view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                            },
                            valueRange = 0.1f..1.0f
                    )

                    val shapeConfigs =
                            listOf(
                                    stringResource(R.string.personalization_shape_telemetry) to
                                            telemetryCardShapePreset to
                                            { v: Float ->
                                                telemetryCardShapePreset = v
                                            },
                                    stringResource(R.string.personalization_shape_app_launcher) to
                                            appLauncherCardShapePreset to
                                            { v: Float ->
                                                appLauncherCardShapePreset = v
                                            },
                                    stringResource(
                                            R.string.personalization_shape_remote_commands
                                    ) to
                                            remoteControlCardShapePreset to
                                            { v: Float ->
                                                remoteControlCardShapePreset = v
                                            },
                                    stringResource(R.string.personalization_shape_remote_mouse) to
                                            remoteMouseCardShapePreset to
                                            { v: Float ->
                                                remoteMouseCardShapePreset = v
                                            },
                                    stringResource(R.string.personalization_shape_task_manager) to
                                            taskManagerCardShapePreset to
                                            { v: Float ->
                                                taskManagerCardShapePreset = v
                                            },
                                    stringResource(R.string.personalization_shape_pc_orb) to
                                            pcCardShapePreset to
                                            { v: Float ->
                                                pcCardShapePreset = v
                                            }
                            )

                    val maxShapes = (materialShapesList.size - 1).toFloat()

                    shapeConfigs.forEach { (config, setter) ->
                        val (label, current) = config
                        val shapeIndex =
                                current.roundToInt().coerceIn(0, materialShapeNames.lastIndex)
                        val shapeName = materialShapeNames[shapeIndex]
                        var lastHapticIndex by remember { mutableIntStateOf(current.toInt()) }
                        // Morph the preview shape smoothly between fixed presets while
                        // the slider value itself stays snapped to integer indices
                        // (so haptics + value semantics still tick at fixed points).
                        val animatedShapePreset by
                                animateFloatAsState(
                                        targetValue = current,
                                        animationSpec =
                                                androidx.compose.animation.core.spring(stiffness = androidx.compose.animation.core.Spring.StiffnessMediumLow),
                                        label = "shape_morph_$label"
                                )
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                        label,
                                        style = MaterialTheme.typography.bodyMedium,
                                        fontWeight = FontWeight.Bold
                                )
                                Text(
                                        stringResource(shapeName),
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.primary,
                                        fontWeight = FontWeight.SemiBold
                                )
                            }
                            Row(
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(16.dp)
                            ) {
                                Slider(
                                        value = current,
                                        onValueChange = { newValue ->
                                            val newIndex = newValue.roundToInt()
                                            if (newIndex != lastHapticIndex) {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.CLOCK_TICK
                                                )
                                                lastHapticIndex = newIndex
                                            }
                                            setter(newIndex.toFloat())
                                        },
                                        valueRange = 0f..maxShapes,
                                        steps = materialShapesList.size - 2,
                                        modifier = Modifier.weight(1f)
                                )
                                Box(
                                        modifier =
                                                Modifier.size(56.dp)
                                                        .clip(
                                                                cardShape(
                                                                        animatedShapePreset,
                                                                        cornerRadius
                                                                )
                                                        )
                                                        .background(
                                                                MaterialTheme.colorScheme.primary
                                                        )
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
    Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Icon(
                icon,
                contentDescription = stringResource(R.string.cd_section_icon),
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(20.dp)
        )
        Text(
                title,
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.Black,
                color = MaterialTheme.colorScheme.primary
        )
    }
}

@Composable
private fun SingleSelectChips(
        options: List<String>,
        selected: String,
        onSelected: (String) -> Unit
) {
    val view = LocalView.current
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        options.forEach { option ->
            FilterChip(
                    selected = selected == option,
                    onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onSelected(option)
                    },
                    label = {
                        Text(
                                option.replace("_", " ").split(" ").joinToString(" ") {
                                    it.replaceFirstChar { char -> char.uppercase() }
                                }
                        )
                    }
            )
        }
    }
}

@Composable
private fun HueSlider(value: Float, onValueChange: (Float) -> Unit) {
    val gradient = remember {
        Brush.horizontalGradient((0..360).map { Color.hsv(it.toFloat(), 1f, 1f) })
    }
    Box(modifier = Modifier.fillMaxWidth().height(32.dp).clip(CircleShape).background(gradient)) {
        Slider(
                value = value,
                onValueChange = onValueChange,
                valueRange = 0f..360f,
                colors =
                        SliderDefaults.colors(
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
            val targetColor = Color(palette.tone(tone))
            val animatedColor by
                    animateColorAsState(
                            targetValue = targetColor,
                            animationSpec = androidx.compose.animation.core.tween(durationMillis = 300),
                            label = "TonalColorAnimation"
                    )
            Box(
                    modifier =
                            Modifier.weight(1f)
                                    .height(40.dp)
                                    .clip(MaterialTheme.shapes.small)
                                    .background(animatedColor)
            )
        }
    }
}

@Composable
private fun MiniCardPreview(
        seedColor: String,
        shapePreset: Float,
        cornerRadius: Int,
        opacity: Float
) {
    val isDark = isSystemInDarkTheme()
    val scheme = remember(seedColor, isDark) {
        try {
            colorSchemeFromSeed(Color(seedColor.toColorInt()), isDark)
        } catch (e: Exception) {
            null
        }
    }
    val containerTarget = scheme?.primaryContainer ?: MaterialTheme.colorScheme.primaryContainer
    val contentColor = scheme?.onPrimaryContainer ?: MaterialTheme.colorScheme.onPrimaryContainer
    val animatedColor by
            animateColorAsState(
                    targetValue = containerTarget,
                    animationSpec = androidx.compose.animation.core.tween(durationMillis = 300),
                    label = "MiniCardColorAnimation"
            )

    val adaptivePadding = calculateAdaptivePadding(shapePreset)

    Box(
            modifier =
                    Modifier.size(width = 140.dp, height = 100.dp)
                            .clip(cardShape(shapePreset, cornerRadius))
                            .background(animatedColor.copy(alpha = opacity))
                            .padding(adaptivePadding)
    ) {
        Column(verticalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxSize()) {
            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Box(
                        modifier =
                                Modifier.size(16.dp)
                                        .background(contentColor.copy(alpha = 0.5f), CircleShape)
                )
                Box(
                        modifier =
                                Modifier.width(32.dp)
                                        .height(8.dp)
                                        .background(contentColor.copy(alpha = 0.3f), CircleShape)
                )
            }
            Text(
                    stringResource(R.string.personalization_preview),
                    style = MaterialTheme.typography.labelSmall,
                    color = contentColor,
                    fontWeight = FontWeight.Bold
            )
            Box(
                    modifier =
                            Modifier.fillMaxWidth()
                                    .height(20.dp)
                                    .background(contentColor.copy(alpha = 0.2f), CircleShape)
            )
        }
    }
}
