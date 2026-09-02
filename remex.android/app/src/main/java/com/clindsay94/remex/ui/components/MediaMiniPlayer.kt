package com.clindsay94.remex.ui.components

import android.graphics.Bitmap
import android.os.SystemClock
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import com.clindsay94.remex.ui.screens.RemexLinearWavyProgress
import kotlinx.coroutines.delay

/**
 * Height the docked mini-player occupies, shared with [com.clindsay94.remex.ui.screens.RemoteControlScreen]
 * so the floating toolbar's raise and the grid's bottom inset cannot drift out of step with the bar
 * actually drawn here.
 */
internal val MiniPlayerHeight = 72.dp

/**
 * Docked mini-player for the Remote Control screen (spec 4.3): artwork, title/artist, its own
 * play/pause target, and a thin wavy progress bar along the bottom edge while a timeline is known.
 *
 * @param onOpen Tapping the bar (outside the play/pause button) opens [MediaNowPlayingSheet].
 * @param onPlayPause Sends the same play/pause key [MediaControlSection] uses, through `onSendKey`.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun MediaMiniPlayer(
        playback: MediaPlaybackSnapshot,
        artwork: Bitmap?,
        onOpen: () -> Unit,
        onPlayPause: () -> Unit,
        modifier: Modifier = Modifier
) {
    // UNKNOWN has no reading to show a bar about - same reasoning as NowPlayingLine's early return
    // in MediaControlSection (RemEx-nmvz6): a bar with nothing to say would be a claim the phone
    // cannot back up. NONE is a reading ("the PC answered, nothing is playing"), not an absence of
    // one, but a docked bar reading "Nothing playing" for the whole session is not a claim worth
    // making either - it is the "nothing playing" case the handoff acceptance means by "absent".
    AnimatedVisibility(
            visible =
                    playback.status != MediaPlaybackStatus.UNKNOWN &&
                            playback.status != MediaPlaybackStatus.NONE,
            enter =
                    slideInVertically(animationSpec = MaterialTheme.motionScheme.defaultSpatialSpec()) {
                        it
                    } + fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
            exit =
                    slideOutVertically(animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()) {
                        it
                    } + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
            modifier = modifier
    ) {
        // Ticks once a second while PLAYING, entirely on the phone's monotonic clock like
        // MediaPlaybackSnapshot.positionAt itself - never compared against a host timestamp.
        var progress by remember(playback) {
            mutableStateOf(playback.progressAt(SystemClock.elapsedRealtime()))
        }
        LaunchedEffect(playback) {
            while (playback.status == MediaPlaybackStatus.PLAYING) {
                delay(1_000L)
                progress = playback.progressAt(SystemClock.elapsedRealtime())
            }
        }

        Surface(
                modifier = Modifier.fillMaxWidth().height(MiniPlayerHeight),
                color = MaterialTheme.colorScheme.surfaceContainerHigh,
                tonalElevation = 3.dp
        ) {
            Column {
                Row(
                        modifier =
                                Modifier.fillMaxWidth()
                                        .clickable(
                                                onClickLabel =
                                                        stringResource(
                                                                R.string.rc_media_open_now_playing
                                                        ),
                                                onClick = onOpen
                                        )
                                        .padding(horizontal = 16.dp, vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    if (artwork != null) {
                        Image(
                                bitmap = artwork.asImageBitmap(),
                                contentDescription = stringResource(R.string.rc_media_artwork),
                                contentScale = ContentScale.Crop,
                                modifier =
                                        Modifier.size(48.dp).clip(MaterialTheme.shapes.small)
                        )
                    } else {
                        Box(
                                modifier =
                                        Modifier.size(48.dp)
                                                .clip(MaterialTheme.shapes.small)
                                                .background(
                                                        MaterialTheme.colorScheme
                                                                .secondaryContainer
                                                ),
                                contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                    imageVector = Icons.Default.MusicNote,
                                    // Decorative: there is no art to claim here, and the
                                    // title/artist text already carries the meaning (RemEx-vtorl.5
                                    // review).
                                    contentDescription = null,
                                    tint = MaterialTheme.colorScheme.onSecondaryContainer
                            )
                        }
                    }

                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                                text = playback.title ?: statusWord(playback.status),
                                style = MaterialTheme.typography.titleSmallEmphasized,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                        )
                        playback.artist?.let { artist ->
                            Text(
                                    text = artist,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                            )
                        }
                    }

                    IconButton(onClick = onPlayPause) {
                        Icon(
                                imageVector =
                                        if (playback.status == MediaPlaybackStatus.PLAYING) {
                                            Icons.Default.Pause
                                        } else {
                                            Icons.Default.PlayArrow
                                        },
                                contentDescription = stringResource(playPauseLabelRes(playback.status))
                        )
                    }
                }

                if (playback.hasTimeline) {
                    RemexLinearWavyProgress(
                            progress = progress ?: 0f,
                            modifier = Modifier.fillMaxWidth()
                    )
                }
            }
        }
    }
}

/** The status word shown when there is no track title to lead with. Mirrors NowPlayingLine. */
@Composable
private fun statusWord(status: MediaPlaybackStatus): String =
        stringResource(
                when (status) {
                    MediaPlaybackStatus.PLAYING -> R.string.rc_media_state_playing
                    MediaPlaybackStatus.PAUSED -> R.string.rc_media_state_paused
                    MediaPlaybackStatus.STOPPED,
                    MediaPlaybackStatus.NONE,
                    MediaPlaybackStatus.UNKNOWN -> R.string.rc_media_state_idle
                }
        )

/** Same label choice [MediaControlSection]'s play/pause button uses. */
private fun playPauseLabelRes(status: MediaPlaybackStatus): Int =
        when (status) {
            MediaPlaybackStatus.PLAYING -> R.string.rc_media_pause
            MediaPlaybackStatus.PAUSED,
            MediaPlaybackStatus.STOPPED,
            MediaPlaybackStatus.NONE -> R.string.rc_media_play
            MediaPlaybackStatus.UNKNOWN -> R.string.rc_media_play_pause
        }
