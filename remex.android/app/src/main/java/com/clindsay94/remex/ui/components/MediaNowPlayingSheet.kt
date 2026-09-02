package com.clindsay94.remex.ui.components

import android.graphics.Bitmap
import android.os.SystemClock
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SheetValue
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import kotlinx.coroutines.delay

/**
 * Full media controls, opened by tapping [MediaMiniPlayer] (spec 4.4).
 *
 * Large artwork up top, the existing [MediaControlSection] unchanged below (title/artist/volume/
 * transport), and — only once [MediaPlaybackSnapshot.hasTimeline] is true — a full-width wavy
 * progress bar with elapsed (`m:ss`) and remaining (`-m:ss`) labels above it.
 *
 * `rememberBottomSheetState`, not the deprecated `rememberModalBottomSheetState`: material3
 * 1.5.0-alpha20+ unified the partial/full-expand states behind one API.
 *
 * The progress row doubles as a seek control (RemEx-vtorl): dragging the [Slider] moves the
 * elapsed label with the thumb, and releasing it fires [onSeek] with the target position. The
 * host confirms it — or doesn't — asynchronously; [onSeek]'s caller owns the optimistic-then-
 * revert bookkeeping, not this composable.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MediaNowPlayingSheet(
        connected: Boolean,
        inputSupported: Boolean,
        playback: MediaPlaybackSnapshot,
        artwork: Bitmap?,
        shape: Shape,
        onSendKey: (Int) -> Unit,
        onSeek: (Long) -> Unit,
        onDismiss: () -> Unit
) {
    val sheetState = rememberBottomSheetState(SheetValue.Hidden)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Column(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 8.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Text(
                    text = stringResource(R.string.rc_media_now_playing),
                    style = MaterialTheme.typography.titleLargeEmphasized
            )

            if (artwork != null) {
                Image(
                        bitmap = artwork.asImageBitmap(),
                        contentDescription = stringResource(R.string.rc_media_artwork),
                        contentScale = ContentScale.Crop,
                        modifier = Modifier.size(200.dp).clip(MaterialTheme.shapes.large)
                )
            } else {
                Box(
                        modifier =
                                Modifier.size(200.dp)
                                        .clip(MaterialTheme.shapes.large)
                                        .background(MaterialTheme.colorScheme.secondaryContainer),
                        contentAlignment = Alignment.Center
                ) {
                    Icon(
                            imageVector = Icons.Default.MusicNote,
                            // Decorative: there is no art to claim here, and the title/artist text
                            // already carries the meaning (RemEx-vtorl.5 review).
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onSecondaryContainer,
                            modifier = Modifier.size(64.dp)
                    )
                }
            }

            if (playback.hasTimeline) {
                // Ticks once a second while PLAYING, same rule as MediaMiniPlayer: entirely on the
                // phone's own monotonic clock, never compared against a host timestamp.
                var nowElapsedMs by remember(playback) {
                    mutableLongStateOf(SystemClock.elapsedRealtime())
                }
                LaunchedEffect(playback) {
                    while (playback.status == MediaPlaybackStatus.PLAYING) {
                        delay(1_000L)
                        nowElapsedMs = SystemClock.elapsedRealtime()
                    }
                }
                val durationMs = playback.durationMs ?: 0L
                // Non-null only while the user has a finger on the thumb; the drag value wins over
                // the extrapolated one so the label and thumb follow the gesture instead of fighting
                // the ticking clock above.
                var dragProgress by remember(playback) { mutableStateOf<Float?>(null) }
                val sliderProgress = dragProgress ?: (playback.progressAt(nowElapsedMs) ?: 0f)
                val positionMs =
                        dragProgress?.let { (it * durationMs).toLong() }
                                ?: playback.positionAt(nowElapsedMs) ?: 0L
                val seekDescription = stringResource(R.string.rc_media_seek)

                Slider(
                        value = sliderProgress,
                        onValueChange = { dragProgress = it },
                        onValueChangeFinished = {
                            val target = dragProgress
                            dragProgress = null
                            if (target != null) {
                                onSeek((target * durationMs).toLong())
                            }
                        },
                        valueRange = 0f..1f,
                        enabled = connected && inputSupported,
                        modifier =
                                Modifier.fillMaxWidth().semantics {
                                    contentDescription = seekDescription
                                }
                )
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text(
                            text = formatElapsed(positionMs),
                            style = MaterialTheme.typography.labelMedium
                    )
                    Text(
                            text = formatRemaining((durationMs - positionMs).coerceAtLeast(0L)),
                            style = MaterialTheme.typography.labelMedium
                    )
                }
            }

            MediaControlSection(
                    connected = connected,
                    inputSupported = inputSupported,
                    playback = playback,
                    shape = shape,
                    onSendKey = onSendKey
            )
        }
    }
}

private fun formatElapsed(ms: Long): String {
    val totalSeconds = (ms / 1000L).coerceAtLeast(0L)
    val minutes = totalSeconds / 60L
    val seconds = totalSeconds % 60L
    return "%d:%02d".format(minutes, seconds)
}

private fun formatRemaining(ms: Long): String = "-" + formatElapsed(ms)
