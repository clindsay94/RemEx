package com.clindsay94.remex.ui.screens

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.blur
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlin.math.cos
import kotlin.math.sin

@Composable
fun SplashScreen(
    onFinished: () -> Unit
) {
    val alphaAnim = remember { Animatable(0f) }
    val morphProgress = remember { Animatable(0f) }

    val infiniteTransition = rememberInfiniteTransition(label = "blobs")
    val time by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 2f * Math.PI.toFloat(),
        animationSpec = infiniteRepeatable(
            animation = tween(4000, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "time"
    )

    LaunchedEffect(Unit) {
        alphaAnim.animateTo(1f, tween(1000))
        delay(500)
        morphProgress.animateTo(
            targetValue = 1f,
            animationSpec = tween(2500, easing = FastOutSlowInEasing)
        )
        delay(1000)
        alphaAnim.animateTo(0f, tween(800))
        onFinished()
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background),
        contentAlignment = Alignment.Center
    ) {
        // Metaball layer
        Box(
            modifier = Modifier
                .size(300.dp)
                .graphicsLayer {
                    // This layer combines blur and high contrast to create metaballs
                    alpha = 0.99f // Force separate layer
                }
                .blur(30.dp)
        ) {
            val primaryColor = MaterialTheme.colorScheme.primary
            val secondaryColor = MaterialTheme.colorScheme.secondary
            val tertiaryColor = MaterialTheme.colorScheme.tertiary

            Canvas(modifier = Modifier.fillMaxSize()) {
                val center = Offset(size.width / 2f, size.height / 2f)
                val baseRadius = 60.dp.toPx()

                // Draw 5 morphing blobs
                for (i in 0 until 5) {
                    val angle = (time + i * (2 * Math.PI / 5)).toFloat()
                    val distance = 40.dp.toPx() * (1f - morphProgress.value)
                    val offset = Offset(
                        x = center.x + cos(angle) * distance,
                        y = center.y + sin(angle * 1.3f) * distance
                    )

                    val color = when (i % 3) {
                        0 -> primaryColor
                        1 -> secondaryColor
                        else -> tertiaryColor
                    }

                    drawCircle(
                        color = color,
                        radius = baseRadius * (0.8f + 0.4f * sin(time + i)),
                        center = offset
                    )
                }

                // Center blob that grows to form the background for text
                drawCircle(
                    color = primaryColor,
                    radius = baseRadius * 1.5f * morphProgress.value,
                    center = center
                )
            }
        }

        // Text layer that appears as blobs merge
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.alpha(morphProgress.value * alphaAnim.value)
        ) {
            Text(
                text = "RemEx",
                style = MaterialTheme.typography.displayMedium,
                fontWeight = FontWeight.Black,
                letterSpacing = 4.sp,
                color = MaterialTheme.colorScheme.onPrimary
            )

            Text(
                text = "Remote Execution",
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.Light,
                letterSpacing = 2.sp,
                color = MaterialTheme.colorScheme.onPrimary.copy(alpha = 0.7f)
            )
        }
    }
}
