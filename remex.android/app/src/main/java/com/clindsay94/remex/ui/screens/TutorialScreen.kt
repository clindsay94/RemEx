package com.clindsay94.remex.ui.screens

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.*
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.util.lerp
import kotlin.math.abs
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.compose.ui.res.stringResource
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.launch
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin

private data class TutorialPage(
    val emoji: String,
    val titleRes: Int,
    val bodyRes: Int,
    val linkLabelRes: Int? = null,
    val linkUrl: String? = null,
    val actionLabelRes: Int? = null,
    val batteryAction: Boolean = false,
    val illustration: TutorialIllustration = TutorialIllustration.NONE
)

private enum class TutorialIllustration {
    NONE,
    MONITOR_PHONE,
    DOWNLOAD,
    QR_CODE,
    DISCOVERY,
    IP_ADDRESS,
    CONNECTION,
    READY
}

private val tutorialPages = listOf(
    TutorialPage(
        emoji = "🖥️",
        titleRes = R.string.tutorial_page1_title,
        bodyRes = R.string.tutorial_page1_body,
        illustration = TutorialIllustration.MONITOR_PHONE
    ),
    TutorialPage(
        emoji = "⚙️",
        titleRes = R.string.tutorial_page2_title,
        bodyRes = R.string.tutorial_page2_body,
        linkLabelRes = R.string.tutorial_page2_link_label,
        linkUrl = "https://github.com/clindsay94/RemEx/releases",
        illustration = TutorialIllustration.DOWNLOAD
    ),
    TutorialPage(
        emoji = "📱",
        titleRes = R.string.tutorial_page_qr_title,
        bodyRes = R.string.tutorial_page_qr_body,
        illustration = TutorialIllustration.QR_CODE
    ),
    TutorialPage(
        emoji = "📡",
        titleRes = R.string.tutorial_page3_title,
        bodyRes = R.string.tutorial_page3_body,
        illustration = TutorialIllustration.DISCOVERY
    ),
    TutorialPage(
        emoji = "🔍",
        titleRes = R.string.tutorial_page4_title,
        bodyRes = R.string.tutorial_page4_body,
        illustration = TutorialIllustration.IP_ADDRESS
    ),
    TutorialPage(
        emoji = "🔌",
        titleRes = R.string.tutorial_page5_title,
        bodyRes = R.string.tutorial_page5_body,
        illustration = TutorialIllustration.CONNECTION
    ),
    TutorialPage(
        emoji = "🚀",
        titleRes = R.string.tutorial_page6_title,
        bodyRes = R.string.tutorial_page6_body,
        illustration = TutorialIllustration.READY
    ),
    TutorialPage(
        emoji = "🔋",
        titleRes = R.string.tutorial_battery_title,
        bodyRes = R.string.tutorial_battery_body,
        actionLabelRes = R.string.tutorial_battery_action,
        batteryAction = true
    )
)

@Composable
fun TutorialScreen(
    onFinished: () -> Unit
) {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()

    TutorialScreenContent(
        onFinished = {
            scope.launch {
                settingsManager.markOnboardingCompleted()
                onFinished()
            }
        }
    )
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun TutorialScreenContent(
    onFinished: () -> Unit
) {
    val scope = rememberCoroutineScope()
    val pagerState = rememberPagerState(pageCount = { tutorialPages.size })
    val isLastPage = pagerState.currentPage == tutorialPages.size - 1
    val motionScheme = MaterialTheme.motionScheme

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
    ) {
        // Skip button — top right
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 16.dp, end = 8.dp),
            horizontalArrangement = Arrangement.End
        ) {
            TextButton(onClick = { onFinished() }) {
                    Text(
                        text = stringResource(R.string.tutorial_skip),
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }

        // Pager content with depth parallax (pages scale + fade as they slide)
        HorizontalPager(
            state = pagerState,
            modifier = Modifier.weight(1f)
        ) { page ->
            val pageOffset =
                (pagerState.currentPage - page) + pagerState.currentPageOffsetFraction
            TutorialPageContent(
                page = tutorialPages[page],
                isFirst = page == 0,
                modifier = Modifier.graphicsLayer {
                    val t = 1f - abs(pageOffset).coerceIn(0f, 1f)
                    val scale = lerp(0.88f, 1f, t)
                    scaleX = scale
                    scaleY = scale
                    alpha = lerp(0.4f, 1f, t)
                }
            )
        }

        // Page indicator dots
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 16.dp),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            repeat(tutorialPages.size) { index ->
                val isSelected = pagerState.currentPage == index

                val width by animateDpAsState(
                    targetValue = if (isSelected) 24.dp else 8.dp,
                    // Spatial spring: the active dot springs out with a subtle overshoot.
                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                    label = "dotWidth"
                )
                val color by animateColorAsState(
                    targetValue = if (isSelected)
                        MaterialTheme.colorScheme.primary
                    else
                        MaterialTheme.colorScheme.outlineVariant,
                    animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
                    label = "dotColor"
                )

                Box(
                    modifier = Modifier
                        .padding(horizontal = 4.dp)
                        .height(8.dp)
                        .width(width)
                        .clip(CircleShape)
                        .background(color)
                )
            }
        }

        // Bottom navigation button
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .windowInsetsPadding(WindowInsets.navigationBars)
                .padding(start = 24.dp, end = 24.dp, bottom = 32.dp),
            horizontalArrangement = Arrangement.Center
        ) {
            AnimatedContent(
                targetState = isLastPage,
                transitionSpec = {
                    val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                    fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                },
                label = "buttonTransition"
            ) { lastPage ->
                if (lastPage) {
                    Button(
                        onClick = { onFinished() },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(stringResource(R.string.tutorial_get_started))
                    }
                } else {
                    Button(
                        onClick = {
                            scope.launch {
                                pagerState.animateScrollToPage(pagerState.currentPage + 1)
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(stringResource(R.string.tutorial_next))
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun TutorialScreenPreview() {
    RemExTheme {
        TutorialScreenContent(onFinished = {})
    }
}

@Preview(showBackground = true)
@Composable
private fun TutorialPageContentPreview() {
    RemExTheme {
        TutorialPageContent(tutorialPages.first())
    }
}

@Composable
private fun TutorialPageContent(
    page: TutorialPage,
    isFirst: Boolean = false,
    modifier: Modifier = Modifier
) {
    val uriHandler = LocalUriHandler.current
    val primary = MaterialTheme.colorScheme.primary
    val secondary = MaterialTheme.colorScheme.secondary
    val onBg = MaterialTheme.colorScheme.onBackground

    // Shared animation for illustrations
    val infiniteTransition = rememberInfiniteTransition(label = "tutorialAnim")
    val pulse by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(2000, easing = FastOutSlowInEasing), RepeatMode.Reverse),
        label = "pulse"
    )
    val rotation by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(tween(8000, easing = LinearEasing), RepeatMode.Restart),
        label = "rotation"
    )

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        when {
            // Page 1 — warm branded welcome: the app's logo in the Flower badge.
            isFirst -> {
                Box(
                    modifier = Modifier
                        .size(140.dp)
                        .clip(RoundedCornerShape(percent = 30))
                        .background(MaterialTheme.colorScheme.primaryContainer),
                    contentAlignment = Alignment.Center
                ) {
                    Image(
                        painter = painterResource(R.drawable.ic_launcher_foreground_vector),
                        contentDescription = null,
                        modifier = Modifier.size(168.dp)
                    )
                }
                Spacer(modifier = Modifier.height(16.dp))
            }
            // Animated line illustration on a soft expressive tonal backdrop.
            page.illustration != TutorialIllustration.NONE -> {
                Box(
                    modifier = Modifier
                        .size(180.dp)
                        .clip(RoundedCornerShape(percent = 35))
                        .background(
                            MaterialTheme.colorScheme.secondaryContainer.copy(alpha = 0.35f)
                        ),
                    contentAlignment = Alignment.Center
                ) {
                    Canvas(modifier = Modifier.size(135.dp)) {
                        drawIllustration(
                            page.illustration, primary, secondary, onBg, pulse, rotation
                        )
                    }
                }
                Spacer(modifier = Modifier.height(16.dp))
            }
            // Emoji pages (e.g. battery) get the same friendly tonal badge.
            else -> {
                Box(
                    modifier = Modifier
                        .size(140.dp)
                        .clip(RoundedCornerShape(percent = 35))
                        .background(
                            MaterialTheme.colorScheme.secondaryContainer.copy(alpha = 0.35f)
                        ),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = page.emoji,
                        style = MaterialTheme.typography.displayLarge
                    )
                }
                Spacer(modifier = Modifier.height(16.dp))
            }
        }

        Text(
            text = stringResource(page.titleRes),
            style = MaterialTheme.typography.headlineMedium,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onBackground,
            textAlign = TextAlign.Center
        )

        Spacer(modifier = Modifier.height(12.dp))

        Text(
            text = stringResource(page.bodyRes),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center
        )

        if (page.batteryAction && page.actionLabelRes != null) {
            val context = LocalContext.current
            Spacer(modifier = Modifier.height(16.dp))
            Button(onClick = {
                val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                intent.data = Uri.parse("package:" + context.packageName)
                context.startActivity(intent)
            }) {
                Text(text = stringResource(page.actionLabelRes))
            }
        }

        if (page.linkLabelRes != null && page.linkUrl != null) {
            Spacer(modifier = Modifier.height(12.dp))

            val linkColor = MaterialTheme.colorScheme.primary
            val linkLabel = stringResource(page.linkLabelRes)
            val linkStyles = TextLinkStyles(
                style = SpanStyle(
                    color = linkColor,
                    fontWeight = FontWeight.SemiBold,
                    textDecoration = TextDecoration.Underline
                )
            )
            val annotatedString = buildAnnotatedString {
                val start = length
                append(linkLabel)
                addLink(
                    url = LinkAnnotation.Url(
                        url = page.linkUrl,
                        styles = linkStyles,
                        linkInteractionListener = { uriHandler.openUri(page.linkUrl) }
                    ),
                    start = start,
                    end = length
                )
            }

            Text(
                text = annotatedString,
                style = MaterialTheme.typography.bodyLarge.copy(textAlign = TextAlign.Center),
            )
        }
    }
}

private fun DrawScope.drawIllustration(
    type: TutorialIllustration,
    primary: Color,
    secondary: Color,
    onBg: Color,
    pulse: Float,
    rotation: Float
) {
    val w = size.width
    val h = size.height
    val cx = w / 2f
    val cy = h / 2f

    when (type) {
        TutorialIllustration.MONITOR_PHONE -> {
            // Monitor
            val monW = w * 0.55f
            val monH = h * 0.38f
            drawRoundRect(
                color = primary,
                topLeft = Offset(cx - monW / 2, cy - monH),
                size = Size(monW, monH),
                cornerRadius = CornerRadius(8f),
                style = Stroke(3f)
            )
            // Monitor stand
            drawLine(primary, Offset(cx, cy), Offset(cx, cy + h * 0.08f), 3f)
            drawLine(primary, Offset(cx - w * 0.1f, cy + h * 0.08f), Offset(cx + w * 0.1f, cy + h * 0.08f), 3f)
            // Phone
            val phoneW = w * 0.18f
            val phoneH = h * 0.32f
            drawRoundRect(
                color = secondary,
                topLeft = Offset(cx + monW / 2 - phoneW / 3, cy - phoneH / 2),
                size = Size(phoneW, phoneH),
                cornerRadius = CornerRadius(6f),
                style = Stroke(2f)
            )
            // Pulsing connection arc
            val arcAlpha = 0.2f + pulse * 0.4f
            drawArc(
                color = primary.copy(alpha = arcAlpha),
                startAngle = -30f,
                sweepAngle = 60f,
                useCenter = false,
                topLeft = Offset(cx - w * 0.35f, cy - h * 0.35f),
                size = Size(w * 0.7f, h * 0.7f),
                style = Stroke(2f)
            )
        }

        TutorialIllustration.DOWNLOAD -> {
            // Arrow pointing down into a tray
            val arrowPath = Path().apply {
                moveTo(cx, cy - h * 0.25f)
                lineTo(cx, cy + h * 0.05f)
            }
            drawPath(arrowPath, primary, style = Stroke(3f, cap = StrokeCap.Round))
            // Arrowhead
            drawLine(primary, Offset(cx - w * 0.08f, cy - h * 0.03f), Offset(cx, cy + h * 0.05f), 3f, cap = StrokeCap.Round)
            drawLine(primary, Offset(cx + w * 0.08f, cy - h * 0.03f), Offset(cx, cy + h * 0.05f), 3f, cap = StrokeCap.Round)
            // Tray
            drawLine(primary, Offset(cx - w * 0.2f, cy + h * 0.05f), Offset(cx - w * 0.2f, cy + h * 0.18f), 3f, cap = StrokeCap.Round)
            drawLine(primary, Offset(cx - w * 0.2f, cy + h * 0.18f), Offset(cx + w * 0.2f, cy + h * 0.18f), 3f, cap = StrokeCap.Round)
            drawLine(primary, Offset(cx + w * 0.2f, cy + h * 0.05f), Offset(cx + w * 0.2f, cy + h * 0.18f), 3f, cap = StrokeCap.Round)
            // Pulsing ring
            drawCircle(
                color = secondary.copy(alpha = 0.15f + pulse * 0.15f),
                radius = w * 0.35f + pulse * w * 0.05f,
                center = Offset(cx, cy),
                style = Stroke(1.5f)
            )
        }

        TutorialIllustration.QR_CODE -> {
            // QR code representation with animated scan line
            val qrSize = w * 0.5f
            val qrLeft = cx - qrSize / 2
            val qrTop = cy - qrSize / 2

            // Outer frame
            drawRoundRect(
                color = primary,
                topLeft = Offset(qrLeft, qrTop),
                size = Size(qrSize, qrSize),
                cornerRadius = CornerRadius(4f),
                style = Stroke(2.5f)
            )

            // QR corner markers
            val markerSize = qrSize * 0.22f
            val corners = listOf(
                Offset(qrLeft + 4f, qrTop + 4f),
                Offset(qrLeft + qrSize - markerSize - 4f, qrTop + 4f),
                Offset(qrLeft + 4f, qrTop + qrSize - markerSize - 4f)
            )
            for (corner in corners) {
                drawRoundRect(color = primary, topLeft = corner, size = Size(markerSize, markerSize), cornerRadius = CornerRadius(2f), style = Stroke(2f))
                drawRoundRect(color = primary, topLeft = Offset(corner.x + markerSize * 0.25f, corner.y + markerSize * 0.25f), size = Size(markerSize * 0.5f, markerSize * 0.5f), cornerRadius = CornerRadius(1f))
            }

            // Inner data dots (simulated)
            val dotSize = qrSize * 0.06f
            val dotsPerRow = 5
            val dotSpacing = (qrSize - markerSize * 2) / (dotsPerRow + 1)
            val startX = qrLeft + markerSize + dotSpacing
            val startY = qrTop + markerSize + dotSpacing
            for (row in 0 until dotsPerRow) {
                for (col in 0 until dotsPerRow) {
                    // Pseudo-random pattern based on position
                    if ((row * 7 + col * 3) % 3 != 0) {
                        drawRoundRect(
                            color = primary.copy(alpha = 0.7f),
                            topLeft = Offset(startX + col * dotSpacing, startY + row * dotSpacing),
                            size = Size(dotSize, dotSize),
                            cornerRadius = CornerRadius(1f)
                        )
                    }
                }
            }

            // Scanning line animation
            val scanY = qrTop + pulse * qrSize
            drawLine(
                color = secondary.copy(alpha = 0.8f),
                start = Offset(qrLeft + 2f, scanY),
                end = Offset(qrLeft + qrSize - 2f, scanY),
                strokeWidth = 2f,
                cap = StrokeCap.Round
            )

            // Phone outline around QR
            val phoneW = qrSize + w * 0.15f
            val phoneH = qrSize + h * 0.2f
            drawRoundRect(
                color = onBg.copy(alpha = 0.3f),
                topLeft = Offset(cx - phoneW / 2, cy - phoneH / 2),
                size = Size(phoneW, phoneH),
                cornerRadius = CornerRadius(12f),
                style = Stroke(1.5f)
            )
        }

        TutorialIllustration.DISCOVERY -> {
            // Radar pulse rings
            val maxRadius = w * 0.38f
            for (i in 0..2) {
                val progress = ((pulse + i * 0.33f) % 1f)
                drawCircle(
                    color = primary.copy(alpha = (1f - progress) * 0.5f),
                    radius = maxRadius * progress,
                    center = Offset(cx, cy),
                    style = Stroke(2f)
                )
            }
            // Center device dot
            drawCircle(color = primary, radius = w * 0.04f, center = Offset(cx, cy))
            // Discovered device dots
            val deviceAngles = listOf(45f, 160f, 280f)
            for (angle in deviceAngles) {
                val rad = Math.toRadians(angle.toDouble() + rotation * 0.1)
                val dx = (cx + maxRadius * 0.7f * cos(rad)).toFloat()
                val dy = (cy + maxRadius * 0.7f * sin(rad)).toFloat()
                drawCircle(color = secondary.copy(alpha = 0.6f + pulse * 0.4f), radius = w * 0.025f, center = Offset(dx, dy))
            }
        }

        TutorialIllustration.IP_ADDRESS -> {
            // Terminal/console representation
            val termW = w * 0.7f
            val termH = h * 0.45f
            val termLeft = cx - termW / 2
            val termTop = cy - termH / 2
            drawRoundRect(
                color = onBg.copy(alpha = 0.15f),
                topLeft = Offset(termLeft, termTop),
                size = Size(termW, termH),
                cornerRadius = CornerRadius(8f)
            )
            // Title bar — use clipRect so bottom stays squared-off
            val titleH = termH * 0.15f
            drawRoundRect(
                color = primary.copy(alpha = 0.3f),
                topLeft = Offset(termLeft, termTop),
                size = Size(termW, titleH + 8f),
                cornerRadius = CornerRadius(8f)
            )
            // Over-draw bottom strip to square it off
            drawRect(
                color = primary.copy(alpha = 0.3f),
                topLeft = Offset(termLeft, termTop + titleH - 2f),
                size = Size(termW, 2f)
            )
            // Blinking cursor
            val cursorAlpha = if (pulse > 0.5f) 0.8f else 0.2f
            drawRect(
                color = primary.copy(alpha = cursorAlpha),
                topLeft = Offset(termLeft + termW * 0.15f, termTop + termH * 0.55f),
                size = Size(termW * 0.03f, termH * 0.12f)
            )
            // Text lines (decorative)
            for (i in 0..2) {
                val lineY = termTop + termH * (0.3f + i * 0.2f)
                val lineW = termW * (0.4f + (i * 0.15f))
                drawLine(
                    color = primary.copy(alpha = 0.4f),
                    start = Offset(termLeft + termW * 0.08f, lineY),
                    end = Offset(termLeft + termW * 0.08f + lineW, lineY),
                    strokeWidth = 2f,
                    cap = StrokeCap.Round
                )
            }
        }

        TutorialIllustration.CONNECTION -> {
            // Two nodes with animated connecting line
            val leftNode = Offset(cx - w * 0.25f, cy)
            val rightNode = Offset(cx + w * 0.25f, cy)

            // Nodes
            drawCircle(color = primary, radius = w * 0.06f, center = leftNode)
            drawCircle(color = secondary, radius = w * 0.06f, center = rightNode)

            // Animated dashed line
            val dashLen = w * 0.03f
            val gap = w * 0.02f
            val total = (rightNode.x - leftNode.x - w * 0.12f)
            val startX = leftNode.x + w * 0.06f
            val offset = (pulse * (dashLen + gap)) % (dashLen + gap)
            var x = startX + offset
            while (x + dashLen < rightNode.x - w * 0.06f) {
                drawLine(
                    color = primary.copy(alpha = 0.7f),
                    start = Offset(x, cy),
                    end = Offset(x + dashLen, cy),
                    strokeWidth = 2f,
                    cap = StrokeCap.Round
                )
                x += dashLen + gap
            }

            // Pulse ring around right node
            drawCircle(
                color = secondary.copy(alpha = 0.3f * (1f - pulse)),
                radius = w * 0.06f + pulse * w * 0.08f,
                center = rightNode,
                style = Stroke(1.5f)
            )
        }

        TutorialIllustration.READY -> {
            // Checkmark with celebration particles
            val checkPath = Path().apply {
                moveTo(cx - w * 0.12f, cy)
                lineTo(cx - w * 0.03f, cy + h * 0.1f)
                lineTo(cx + w * 0.15f, cy - h * 0.12f)
            }
            drawPath(checkPath, primary, style = Stroke(4f, cap = StrokeCap.Round))

            // Celebration particles
            rotate(rotation * 0.5f, pivot = Offset(cx, cy)) {
                for (i in 0..5) {
                    val angle = (i * 60f) * (PI.toFloat() / 180f)
                    val dist = w * 0.28f + pulse * w * 0.05f
                    val px = cx + dist * cos(angle)
                    val py = cy + dist * sin(angle)
                    val particleColor = if (i % 2 == 0) primary else secondary
                    drawCircle(
                        color = particleColor.copy(alpha = 0.4f + pulse * 0.3f),
                        radius = w * 0.015f,
                        center = Offset(px, py)
                    )
                }
            }

            // Outer ring
            drawCircle(
                color = primary.copy(alpha = 0.2f),
                radius = w * 0.35f,
                center = Offset(cx, cy),
                style = Stroke(2f)
            )
        }

        TutorialIllustration.NONE -> { /* no-op */ }
    }
}
