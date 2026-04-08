package com.clindsay94.remex.ui.screens

import androidx.compose.animation.core.*
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.drawIntoCanvas
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.theme.materialShapesList
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.random.Random

private class ShapeActor(
    val color: Color,
    val initialSize: Float,
) {
    val pos = Animatable(Offset.Zero, Offset.VectorConverter)
    val rotation = Animatable(Random.nextFloat() * 360f)
    val morphProgress = Animatable(0f)
    val currentSize = Animatable(initialSize)

    var shapeIndex by mutableStateOf(0)
    var targetShapeIndex by mutableStateOf(0)

    var velX = Random.nextFloat() * 10f - 5f
    var velY = Random.nextFloat() * 10f - 5f
    var rotVel = Random.nextFloat() * 5f - 2.5f
}

@Composable
fun SplashScreen(
    onFinished: () -> Unit
) {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }

    val alphaAnim = remember { Animatable(0f) }
    val settleProgress = remember { Animatable(0f) }
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current

    val primary = MaterialTheme.colorScheme.primary
    val secondary = MaterialTheme.colorScheme.secondary
    val tertiary = MaterialTheme.colorScheme.tertiary
    val error = MaterialTheme.colorScheme.error

    // Shared flag so tap-to-skip and the normal animation path don't both call onFinished
    var finishing by remember { mutableStateOf(false) }

    suspend fun finishSplash() {
        if (finishing) return
        finishing = true
        settingsManager.markSplashShown()
        alphaAnim.animateTo(0f, tween(600))
        onFinished()
    }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            // Tap anywhere to skip — uses detectTapGestures for a single
            // onTap callback instead of a raw event loop (review fix).
            .pointerInput(Unit) {
                detectTapGestures {
                    scope.launch { finishSplash() }
                }
            },
        contentAlignment = Alignment.Center
    ) {
        val widthPx = with(density) { maxWidth.toPx() }
        val heightPx = with(density) { maxHeight.toPx() }
        val center = Offset(widthPx / 2f, heightPx / 2f)

        val actors = remember(widthPx, heightPx) {
            List(12) { _ ->
                ShapeActor(
                    color = listOf(primary, secondary, tertiary, error).random().copy(alpha = 0.85f),
                    initialSize = Random.nextFloat() * 80f + 80f
                ).apply {
                    pos.updateBounds(Offset(0f, 0f), Offset(widthPx, heightPx))
                    scope.launch {
                        pos.snapTo(Offset(Random.nextFloat() * widthPx, Random.nextFloat() * heightPx))
                    }
                    shapeIndex = (0 until materialShapesList.size).random()
                    targetShapeIndex = (0 until materialShapesList.size).random()
                }
            }
        }

        LaunchedEffect(widthPx, heightPx) {
            alphaAnim.animateTo(1f, tween(800))

            // Bounce actors for 2.5s (reduced from 4s)
            val startTime = System.currentTimeMillis()
            while (System.currentTimeMillis() - startTime < 2500 && !finishing) {
                actors.forEach { actor ->
                    var newX = actor.pos.value.x + actor.velX
                    var newY = actor.pos.value.y + actor.velY

                    if (newX < 0 || newX > widthPx) {
                        actor.velX *= -1
                        newX = newX.coerceIn(0f, widthPx)
                    }
                    if (newY < 0 || newY > heightPx) {
                        actor.velY *= -1
                        newY = newY.coerceIn(0f, heightPx)
                    }

                    actor.pos.snapTo(Offset(newX, newY))
                    actor.rotation.snapTo(actor.rotation.value + actor.rotVel)

                    if (Random.nextFloat() < 0.03f && !actor.morphProgress.isRunning) {
                        scope.launch {
                            actor.targetShapeIndex = (0 until materialShapesList.size).random()
                            actor.morphProgress.animateTo(1f, tween(600, easing = FastOutSlowInEasing))
                            actor.shapeIndex = actor.targetShapeIndex
                            actor.morphProgress.snapTo(0f)
                        }
                    }
                }
                delay(16)
            }

            if (!finishing) {
                // Settle with spring
                settleProgress.animateTo(
                    targetValue = 1f,
                    animationSpec = spring(
                        dampingRatio = Spring.DampingRatioMediumBouncy,
                        stiffness = Spring.StiffnessLow
                    )
                )
                delay(1000)
                finishSplash()
            }
        }

        androidx.compose.foundation.Canvas(modifier = Modifier.fillMaxSize().alpha(alphaAnim.value)) {
            actors.forEach { actor ->
                val currentPos = if (settleProgress.value > 0.01f) {
                    Offset(
                        x = lerp(actor.pos.value.x, center.x, settleProgress.value),
                        y = lerp(actor.pos.value.y, center.y, settleProgress.value)
                    )
                } else {
                    actor.pos.value
                }

                val currentSize = if (settleProgress.value > 0.01f) {
                    lerp(actor.currentSize.value, 220.dp.toPx(), settleProgress.value)
                } else {
                    actor.currentSize.value
                }

                val currentRotation = if (settleProgress.value > 0.01f) {
                    lerp(actor.rotation.value, 0f, settleProgress.value)
                } else {
                    actor.rotation.value
                }

                val startPoly = materialShapesList[actor.shapeIndex]
                val endPoly = materialShapesList[actor.targetShapeIndex]

                drawIntoCanvas { canvas ->
                    val nativeCanvas = canvas.nativeCanvas
                    val checkpoint = nativeCanvas.save()

                    nativeCanvas.translate(currentPos.x, currentPos.y)
                    nativeCanvas.rotate(currentRotation)
                    nativeCanvas.scale(currentSize, currentSize)

                    val composePath = androidx.compose.ui.graphics.Path()
                    val morphObj = androidx.graphics.shapes.Morph(startPoly, endPoly)
                    morphObj.asComposePath(actor.morphProgress.value, composePath)

                    canvas.drawPath(composePath, androidx.compose.ui.graphics.Paint().apply {
                        color = actor.color
                        alpha = if (settleProgress.value > 0.6f) (1f - settleProgress.value) * 2.5f.coerceIn(0f, 1f) else 1f
                    })

                    nativeCanvas.restoreToCount(checkpoint)
                }
            }

            if (settleProgress.value > 0.1f) {
                drawIntoCanvas { canvas ->
                    val poly = materialShapesList.first()
                    val composePath = androidx.compose.ui.graphics.Path()
                    val morph = androidx.graphics.shapes.Morph(poly, poly)
                    morph.asComposePath(0f, composePath)

                    val checkpoint = canvas.nativeCanvas.save()
                    canvas.nativeCanvas.translate(center.x, center.y)

                    val s = 260.dp.toPx() * settleProgress.value
                    canvas.nativeCanvas.scale(s, s)

                    canvas.drawPath(composePath, androidx.compose.ui.graphics.Paint().apply {
                        color = primary
                        alpha = settleProgress.value
                    })
                    canvas.nativeCanvas.restoreToCount(checkpoint)
                }
            }
        }

        // Branding text
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.alpha(settleProgress.value * alphaAnim.value)
        ) {
            Text(
                text = stringResource(R.string.splash_app_name),
                style = MaterialTheme.typography.displayMedium,
                fontWeight = FontWeight.Black,
                letterSpacing = 4.sp,
                color = MaterialTheme.colorScheme.onBackground
            )
            Text(
                text = stringResource(R.string.splash_tagline),
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.Light,
                letterSpacing = 2.sp,
                color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.7f)
            )
        }
    }
}

private fun lerp(start: Float, stop: Float, fraction: Float): Float {
    return start + fraction * (stop - start)
}

private fun androidx.graphics.shapes.Morph.asComposePath(
    progress: Float,
    targetPath: androidx.compose.ui.graphics.Path
): androidx.compose.ui.graphics.Path {
    targetPath.reset()
    var first = true
    this.forEachCubic(progress) { bezier ->
        if (first) {
            targetPath.moveTo(bezier.anchor0X, bezier.anchor0Y)
            first = false
        }
        targetPath.cubicTo(
            bezier.control0X, bezier.control0Y,
            bezier.control1X, bezier.control1Y,
            bezier.anchor1X, bezier.anchor1Y
        )
    }
    targetPath.close()
    return targetPath
}
