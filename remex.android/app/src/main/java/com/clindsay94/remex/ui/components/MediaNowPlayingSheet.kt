package com.clindsay94.remex.ui.components

import android.graphics.Bitmap
import android.os.SystemClock
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.basicMarquee
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
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import kotlinx.coroutines.delay

/**
 * Full media controls, opened by tapping [MediaMiniPlayer] (spec 4.4).
 *
 * Large artwork up top, the title and artist directly below it, then the existing
 * [MediaControlSection] (volume/transport) further down, and — only once
 * [MediaPlaybackSnapshot.hasTimeline] is true — a full-width [Slider] seek control with elapsed
 * (`m:ss`) and remaining (`-m:ss`) labels above it.
 *
 * `rememberBottomSheetState`, not the deprecated `rememberModalBottomSheetState`: material3
 * 1.5.0-alpha20+ unified the partial/full-expand states behind one API.
 *
 * The progress row doubles as a seek control (RemEx-vtorl): dragging the [Slider] moves the
 * elapsed label with the thumb, and releasing it fires [onSeek] with the target position. The
 * host confirms it — or doesn't — asynchronously; [onSeek]'s caller owns the optimistic-then-
 * revert bookkeeping, not this composable.
 *
 * TITLE AND ARTIST MOVED OUT OF THE CONTROLS CARD (RemEx-vtorl). [MediaControlSection] draws that
 * card with the user's Remote Commands shape — a "Gem" hexagon today, any expressive shape in
 * general — and that shape's slanted edges clip text pinned to its top/bottom edges. Text that
 * must be readable does not live inside an expressive shape, so this composable renders the title
 * and artist itself, full width, directly under the artwork, and passes `showNowPlaying = false`
 * to [MediaControlSection] so the card no longer draws its own copy.
 *
 * @param shape Kept for API stability only. It no longer reaches [MediaControlSection] — the
 *   controls card now always uses `MaterialTheme.shapes.extraLarge`, which does not clip the
 *   volume hint the way an expressive shape did. The grid no longer hosts the media card, so the
 *   user's shape choice is not lost anywhere it was still visible.
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

            val statusWord =
                    stringResource(
                            when (playback.status) {
                                MediaPlaybackStatus.PLAYING -> R.string.rc_media_state_playing
                                MediaPlaybackStatus.PAUSED -> R.string.rc_media_state_paused
                                MediaPlaybackStatus.STOPPED,
                                MediaPlaybackStatus.NONE,
                                MediaPlaybackStatus.UNKNOWN -> R.string.rc_media_state_idle
                            }
                    )

            Text(
                    text = playback.title ?: statusWord,
                    style = MaterialTheme.typography.titleLargeEmphasized,
                    textAlign = TextAlign.Center,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.fillMaxWidth()
            )

            // ONE line with a marquee, not two-line ellipsis: an "Artist — Album - EP" reading is
            // exactly the shape that used to be clipped mid-word by the shaped card's slanted
            // bottom edge (RemEx-vtorl). Scrolling it keeps the whole string reachable instead of
            // silently dropping the tail.
            //
            // No fillMaxWidth() here: basicMarquee() measures its child with an infinite max
            // width, so a fixed-width parent would make textAlign = Center a no-op (the placeable
            // is always exactly as wide as the text). Without fillMaxWidth, the marquee box sizes
            // to min(intrinsic, available), so the Column's CenterHorizontally alignment centers
            // short artist strings and long ones still fill the width and scroll.
            playback.artist?.let { artist ->
                Text(
                        text = artist,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        // Unbounded: the default of three passes parks the line at its head
                        // afterwards, which is the clipped tail back again for a long sheet visit.
                        modifier = Modifier.basicMarquee(iterations = Int.MAX_VALUE)
                )
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
                // the ticking clock above. Deliberately NOT keyed on `playback`: an unrelated
                // media_state arriving mid-gesture must not wipe an in-flight drag (RemEx-vtorl
                // review) — it is already cleared explicitly in onValueChangeFinished below.
                var dragProgress by remember { mutableStateOf<Float?>(null) }
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
                        // DISABLED, NOT HIDDEN, WHEN THE SESSION WILL NOT SEEK. It is still the
                        // progress readout for a track that is playing perfectly well; taking it
                        // away would remove information to communicate the loss of an action. A
                        // greyed thumb says "this bar does not drag" without the bar jumping to the
                        // user's finger and snapping back 2.5 s later, which is what happened before
                        // canSeek existed.
                        enabled = connected && inputSupported && playback.canSeek,
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
                    shape = MaterialTheme.shapes.extraLarge,
                    onSendKey = onSendKey,
                    showNowPlaying = false
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
