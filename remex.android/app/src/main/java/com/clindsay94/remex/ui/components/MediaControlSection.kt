package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.focusable
import androidx.compose.foundation.indication
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.PressInteraction
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.VolumeDown
import androidx.compose.material.icons.filled.VolumeOff
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.Card
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.minimumInteractiveComponentSize
import androidx.compose.material3.ripple
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Windows virtual-key codes for the media and volume keys (RemEx-hulc).
 *
 * These travel over the EXISTING `keyDown`/`keyUp` input path, so this feature adds no message
 * type and no `protocolVersion` bump — which also means it cannot be silently dropped by the router
 * the way a new client-bound type could be (RemEx-y6x6).
 *
 * WHAT THIS ORIGINALLY CLAIMED, AND WHAT IT COST. It also said "no JNI routing", and that turned out
 * to be the defect rather than the safeguard: with no route of its own, the events went out through
 * `SendMessage`, which routes by TYPE and hands every `desktop_input` to the Remote Desktop client
 * and its `/ws/desktop` socket. This screen has no stream, so that path either discarded the key
 * silently for the rest of the process or started a screen capture on the PC in order to press it.
 * There IS a JNI route now — `RemexCoreClient.SendControlInput` — and it exists so these keys reach
 * the socket this screen is already on (RemEx-035d6).
 *
 * The host halves are already shipped and pinned by tests (RemEx-3cnq): Windows hands the code
 * straight to `SendInput`; Linux translates to `KEY_MUTE`/`KEY_VOLUMEDOWN`/`KEY_VOLUMEUP`/
 * `KEY_NEXTSONG`/`KEY_PREVIOUSSONG`/`KEY_PLAYPAUSE` for the portal and ydotool backends, and to the
 * `XF86Audio*` names for xdotool.
 *
 * PLAY/PAUSE IS THE ONE TO WATCH on X11. `VK_MEDIA_PLAY_PAUSE` is a TOGGLE, so xdotool must send
 * `XF86MediaPlayPause` and not `XF86AudioPlay` — the latter is documented as `KEY_PLAYCD`, "start
 * playing", which would restart a track instead of pausing it. If a device test on CachyOS shows
 * that symptom, that pair is the first place to look.
 */
internal object MediaVirtualKeys {
    const val VOLUME_MUTE = 0xAD
    const val VOLUME_DOWN = 0xAE
    const val VOLUME_UP = 0xAF
    const val MEDIA_NEXT_TRACK = 0xB0
    const val MEDIA_PREV_TRACK = 0xB1
    const val MEDIA_PLAY_PAUSE = 0xB3
}

/** How long a press must be held before it starts repeating. Matches a hardware volume rocker. */
private const val RepeatInitialDelayMs = 400L

/** Gap between repeats once repeating has started. */
private const val RepeatIntervalMs = 150L

/**
 * Volume and media transport controls for the Remote Control screen (RemEx-hulc).
 *
 * TWO SIGNALS GATE THESE BUTTONS, AND BOTH ARE LOAD-BEARING. Every other control on this screen
 * reports its own failure through a snackbar; these do not, because `sendInput` drops events with
 * no error path at all. So the row has to be visibly unavailable rather than merely ineffective —
 * otherwise the user taps, the phone buzzes a confirmation, and nothing happens on the PC.
 *
 * @param connected Whether a host session is live. Required SEPARATELY from [inputSupported]
 *   because `RemexClientManager.hostCapabilities` is a `replay = 1` shared flow that is never reset
 *   on disconnect: the last-known `supportsInputSimulation = true` outlives the connection, so
 *   after a drop the row would stay lit and dead indefinitely.
 * @param inputSupported Whether the host advertises `supportsInputSimulation`.
 * @param playback What the PC reports it is playing. Drives the now-playing line and the play/pause
 *   face; it is NOT a third gate on the row, because not knowing what is playing is no reason to
 *   refuse to send a key.
 * @param showNowPlaying Whether this card renders its own title/artist line (RemEx-vtorl). The
 *   now-playing sheet draws that text itself, full-width and outside this card's expressive shape,
 *   so it passes `false` here; every other caller keeps the default and gets the line as before.
 */
@Composable
fun MediaControlSection(
        connected: Boolean,
        inputSupported: Boolean,
        playback: MediaPlaybackSnapshot,
        shape: Shape,
        onSendKey: (Int) -> Unit,
        modifier: Modifier = Modifier,
        showNowPlaying: Boolean = true
) {
    val enabled = connected && inputSupported
    val playbackStatus = playback.status
    Card(modifier = modifier.fillMaxWidth(), shape = shape) {
        Column(
                modifier = Modifier.fillMaxWidth().padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            if (showNowPlaying) NowPlayingLine(playback)

            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceEvenly,
                    verticalAlignment = Alignment.CenterVertically
            ) {
                MediaButton(
                        icon = Icons.Default.VolumeDown,
                        labelRes = R.string.rc_media_volume_down,
                        virtualKey = MediaVirtualKeys.VOLUME_DOWN,
                        enabled = enabled,
                        repeatable = true,
                        onSendKey = onSendKey
                )
                MediaButton(
                        icon = Icons.Default.VolumeOff,
                        labelRes = R.string.rc_media_mute,
                        virtualKey = MediaVirtualKeys.VOLUME_MUTE,
                        enabled = enabled,
                        // Mute is a TOGGLE, so a repeat would flip it back and forth many times a
                        // second and land on whichever state the finger happened to lift on.
                        repeatable = false,
                        onSendKey = onSendKey
                )
                MediaButton(
                        icon = Icons.Default.VolumeUp,
                        labelRes = R.string.rc_media_volume_up,
                        virtualKey = MediaVirtualKeys.VOLUME_UP,
                        enabled = enabled,
                        repeatable = true,
                        onSendKey = onSendKey
                )
            }

            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceEvenly,
                    verticalAlignment = Alignment.CenterVertically
            ) {
                MediaButton(
                        icon = Icons.Default.SkipPrevious,
                        labelRes = R.string.rc_media_previous,
                        virtualKey = MediaVirtualKeys.MEDIA_PREV_TRACK,
                        enabled = enabled,
                        repeatable = false,
                        onSendKey = onSendKey
                )
                // THE ONE BUTTON HERE THAT REPORTS AS WELL AS ACTS (RemEx-xx6xf). It shows what the
                // next press will DO, which is the transport-control convention everywhere else and
                // the only one that makes a toggle legible: a pause bar means "playing, press to
                // pause". Anything the PC has not told us keeps the triangle.
                MediaButton(
                        icon =
                                if (playbackStatus == MediaPlaybackStatus.PLAYING) Icons.Default.Pause
                                else Icons.Default.PlayArrow,
                        labelRes =
                                when (playbackStatus) {
                                    MediaPlaybackStatus.PLAYING -> R.string.rc_media_pause
                                    // A reading, so the label can be specific: the next press starts
                                    // something.
                                    MediaPlaybackStatus.PAUSED,
                                    MediaPlaybackStatus.STOPPED,
                                    MediaPlaybackStatus.NONE -> R.string.rc_media_play
                                    // NO READING, so neither. "Play or pause" is what a screen reader
                                    // got before this feature existed and is still the only honest
                                    // thing to say about a toggle whose current state is unknown —
                                    // announcing "Play" here would be a claim about the PC.
                                    MediaPlaybackStatus.UNKNOWN -> R.string.rc_media_play_pause
                                },
                        virtualKey = MediaVirtualKeys.MEDIA_PLAY_PAUSE,
                        enabled = enabled,
                        repeatable = false,
                        prominent = true,
                        onSendKey = onSendKey
                )
                MediaButton(
                        icon = Icons.Default.SkipNext,
                        labelRes = R.string.rc_media_next,
                        virtualKey = MediaVirtualKeys.MEDIA_NEXT_TRACK,
                        enabled = enabled,
                        repeatable = false,
                        onSendKey = onSendKey
                )
            }

            // Three states, not two. "This PC is not set up to accept key presses" would be a claim
            // about the user's hardware, and saying it while merely disconnected is a lie they have
            // no way to check — so being offline gets its own line, and connectivity is asked first.
            Text(
                    text =
                            stringResource(
                                    when {
                                        !connected -> R.string.rc_media_disconnected
                                        !inputSupported -> R.string.rc_media_unavailable
                                        else -> R.string.rc_media_hint
                                    }
                            ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

/**
 * What the PC is playing, above the transport row (RemEx-nmvz6).
 *
 * THE METADATA WAS ALREADY ON THE WIRE AND NOTHING DREW IT. `MediaPlaybackState` has carried
 * `Title` and `Artist` since RemEx-xx6xf, and [MediaPlaybackSnapshot] has parsed them, but the
 * screen showed only the play/pause face — so the user could tell the PC was playing and not what.
 *
 * AN UNKNOWN READING RENDERS NOTHING AT ALL, which is the same rule the play/pause face follows and
 * it matters more here. The face has an honest neutral position to fall back to; a text line does
 * not. "Nothing playing" for a reading the phone never received would be a claim about the user's
 * machine, and the whole point of the `unknown` token is that the phone must not make one. So the
 * line is absent before the first `media_state` arrives, absent after a disconnect (the manager
 * resets the snapshot to [MediaPlaybackSnapshot.Unknown]), and absent on a host that cannot read a
 * session — none of which need a separate `connected` gate to achieve.
 *
 * THE TITLE IS THE PRIMARY LINE, NOT THE STATUS WORD. A player that is paused mid-track is far more
 * usefully described by the track than by "Paused", and the transport icon beside it already says
 * which way the toggle will go. The status word appears as the primary line only when there is no
 * track to name — a reading with no metadata, or nothing playing at all.
 *
 * SOURCE APP IS DELIBERATELY NOT SHOWN. The host sends it, but it is a Windows AUMID or an MPRIS bus
 * suffix — `Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic`, not "Groove". Rendering it raw
 * would put an identifier in front of the user and call it a name.
 */
@Composable
private fun NowPlayingLine(playback: MediaPlaybackSnapshot) {
    // NOT a `when` returning early inside the Column: an UNKNOWN reading must contribute no node at
    // all, or the parent's 12.dp `spacedBy` would leave a gap where the line would have been.
    if (playback.status == MediaPlaybackStatus.UNKNOWN) return

    val statusWord =
            stringResource(
                    when (playback.status) {
                        MediaPlaybackStatus.PLAYING -> R.string.rc_media_state_playing
                        MediaPlaybackStatus.PAUSED -> R.string.rc_media_state_paused
                        // STOPPED and NONE are different facts about the PC — a player open and
                        // stopped, versus no player at all — but they read the same to someone
                        // looking at a phone: the next press starts something.
                        else -> R.string.rc_media_state_idle
                    }
            )

    val title = playback.title
    val artist = playback.artist

    Column(
            modifier =
                    Modifier.fillMaxWidth().semantics(mergeDescendants = true) {
                        // BUILT RATHER THAN READ OFF THE SCREEN. The visible primary line drops the
                        // status word whenever there is a title, because the icon carries it; a
                        // screen reader has no icon, so the announcement puts it back.
                        contentDescription =
                                listOfNotNull(statusWord, title, artist).joinToString(". ")
                    },
            verticalArrangement = Arrangement.spacedBy(2.dp)
    ) {
        Text(
                text = title ?: statusWord,
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
        )

        // The second line is the artist when there is one, and the status word when a track is named
        // without one — so a titled reading never loses the playing/paused distinction entirely.
        val secondary = artist ?: title?.let { statusWord }
        if (secondary != null) {
            Text(
                    text = secondary,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
            )
        }
    }
}

/**
 * One media key.
 *
 * The icon is NOT decorative here, unlike the category headers: there is no adjacent text naming
 * the action, so the content description is the only thing a screen reader has to go on
 * (RemEx-xqli).
 *
 * @param prominent Renders as a filled tonal button. Reserved for play/pause, which is the one
 *   control a thumb reaches for without looking.
 */
@Composable
private fun MediaButton(
        icon: ImageVector,
        labelRes: Int,
        virtualKey: Int,
        enabled: Boolean,
        repeatable: Boolean,
        onSendKey: (Int) -> Unit,
        prominent: Boolean = false
) {
    val view = LocalView.current
    val label = stringResource(labelRes)

    val send: () -> Unit = {
        view.hapticCommandSent()
        onSendKey(virtualKey)
    }

    // A REPEATING BUTTON CANNOT ALSO CARRY onClick. `clickable` fires on the UP event, so a
    // press-and-hold would emit every repeat tick AND one more step on lift — the volume would
    // overshoot by exactly one at the end of every hold. So the repeat path owns the gesture
    // outright and supplies its own click semantics; the plain path keeps the stock IconButton,
    // whose ripple, focus and accessibility behaviour are correct for free.
    if (repeatable && enabled) {
        // The ripple has to be driven by hand. A stock IconButton gets press feedback from its own
        // `clickable`, which this button cannot use; without an explicit indication the two volume
        // keys would be the only controls in the row that stay visually dead under a finger.
        val interactionSource = remember { MutableInteractionSource() }

        Box(
                modifier =
                        Modifier.minimumInteractiveComponentSize()
                                .size(40.dp)
                                .clip(CircleShape)
                                .indication(interactionSource, ripple())
                                .repeatWhileHeld(
                                        key = virtualKey,
                                        interactionSource = interactionSource,
                                        onTick = send
                                )
                                // A raw pointerInput is invisible to assistive tech, to focus
                                // traversal AND to the key pipeline, and all three need separate
                                // fixes — a stock IconButton gets the lot from `clickable`.
                                //
                                // `semantics` is what TalkBack and Switch Access act on. `focusable`
                                // puts the node in the Tab order for a Bluetooth keyboard, a tablet
                                // keyboard case or DeX. `onKeyEvent` is what actually ACTIVATES it:
                                // focusable installs a focus target, it does not map Enter/Space,
                                // and the semantics action is consumed by assistive tech rather than
                                // by raw key input. Wiring only the first two is worse than wiring
                                // neither — the button becomes a dead stop in the Tab order, focused
                                // but unusable, instead of being skipped.
                                //
                                // The interactionSource is passed EXPLICITLY: the no-arg overload
                                // emits no FocusInteraction, so the `indication` above would have no
                                // focus state to draw and a keyboard user would see nothing change.
                                // The keyboard action is a single step, since the repeat is only a
                                // convenience for a finger.
                                .focusable(interactionSource = interactionSource)
                                .onKeyEvent { event ->
                                    val activates =
                                            event.key == Key.Enter ||
                                                    event.key == Key.NumPadEnter ||
                                                    event.key == Key.Spacebar ||
                                                    event.key == Key.DirectionCenter
                                    if (activates && event.type == KeyEventType.KeyUp) {
                                        send()
                                        true
                                    } else {
                                        false
                                    }
                                }
                                .semantics(mergeDescendants = true) {
                                    role = Role.Button
                                    onClick(label = label) {
                                        send()
                                        true
                                    }
                                },
                contentAlignment = Alignment.Center
        ) {
            Icon(
                    imageVector = icon,
                    contentDescription = label,
                    // LocalContentColor, not onSurfaceVariant: that is what the stock IconButtons
                    // beside these resolve to inside a Card, and a hardcoded variant made the two
                    // volume keys render visibly dimmer than the mute button between them. The gap
                    // widens under monochrome at contrast 1.0 — exactly where it gets tested.
                    tint = LocalContentColor.current,
                    modifier = Modifier.size(24.dp)
            )
        }
    } else if (prominent) {
        FilledTonalIconButton(onClick = send, enabled = enabled) {
            Icon(imageVector = icon, contentDescription = label, modifier = Modifier.size(24.dp))
        }
    } else {
        IconButton(onClick = send, enabled = enabled) {
            Icon(imageVector = icon, contentDescription = label, modifier = Modifier.size(24.dp))
        }
    }
}

/**
 * Fires [onTick] once per tap, then repeatedly while the finger stays down.
 *
 * THE FIRST TICK IS DELIBERATELY NOT ON TOUCH-DOWN. This section sits inside a scrollable
 * `LazyVerticalGrid`, and a down-triggered send meant that resting a thumb on Volume Down and
 * flicking to reach the Power band changed the PC's volume — an unrequested action on the user's
 * machine, produced by a pure navigation gesture, with nothing to tell them they had caused it.
 * `waitForUpOrCancellation` correctly reports the cancel once the parent claims the drag, but by
 * then the key has already gone out. So a tap fires on UP, exactly as `clickable` would; only a
 * genuine hold fires early, and a hold that has already repeated does not fire again on lift.
 *
 * The loop is launched into the composition's scope and cancelled on lift, on gesture cancellation
 * and on disposal, so a finger dragged off the button — or a recomposition that removes it — cannot
 * leave a coroutine sending volume keys at the PC forever.
 */
private fun Modifier.repeatWhileHeld(
        key: Any,
        interactionSource: MutableInteractionSource,
        onTick: () -> Unit
): Modifier = composed {
    val scope = rememberCoroutineScope()

    pointerInput(key) {
        // awaitEachGesture rather than a hand-rolled `while (true)` inside awaitPointerEventScope:
        // it restarts the block cleanly after every gesture, including a cancelled one.
        awaitEachGesture {
            val down = awaitFirstDown(requireUnconsumed = false)
            val press = PressInteraction.Press(down.position)
            scope.launch { interactionSource.emit(press) }

            // Written by the repeat loop, read in `finally`. Both run on the main dispatcher, so
            // this is an ordinary sequential read rather than a race.
            var repeated = false

            val job =
                    scope.launch {
                        delay(RepeatInitialDelayMs)
                        while (isActive) {
                            repeated = true
                            onTick()
                            delay(RepeatIntervalMs)
                        }
                    }

            // waitForUpOrCancellation returns null when the gesture is cancelled, and can also
            // throw if the whole pointer scope is torn down mid-press. The repeat loop, the tap and
            // the ripple must all be wound up either way, so all three live in `finally` — a
            // stranded loop would keep sending volume keys at the PC, and a stranded press would
            // leave the ripple lit.
            var lifted = false
            try {
                lifted = waitForUpOrCancellation() != null
            } finally {
                job.cancel()

                // A lift with no repeat behind it is a tap: send exactly one. A lift AFTER repeats
                // sends nothing more, or every hold would overshoot by one step at the end. A
                // cancel — the scroll case — sends nothing at all.
                if (lifted && !repeated) onTick()

                scope.launch {
                    interactionSource.emit(
                            if (lifted) PressInteraction.Release(press)
                            else PressInteraction.Cancel(press)
                    )
                }
            }
        }
    }
}
