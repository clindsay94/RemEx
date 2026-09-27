package com.clindsay94.remex.ui.routines

import androidx.annotation.StringRes
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineMediaActions
import com.clindsay94.remex.routines.model.RoutineNotifyTargets
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import com.clindsay94.remex.routines.model.RoutineSensorDirections
import com.clindsay94.remex.routines.model.RoutineSessionStates
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes

/**
 * The words and the templates of the Routines UI (routines spec 1.1, 4.1, 4.2; RemEx-pp0rt.6).
 *
 * Pure data: no Compose, no Context. Icons live in `RoutineIcons.kt`, so this file (and the rules in
 * `RoutineEditorLogic.kt`) stay JVM-testable.
 *
 * **Adding a trigger family is data, not UI work.** [RoutineTriggerFamilies.offered] decides which
 * triggers the picker lists and which templates the gallery shows. S1 offers `manual` only; each
 * later slice adds its trigger id here, its templates to [RoutineTemplates.all] and its strings.
 */
object RoutineTriggerFamilies {
    /** Triggers the editor's picker offers and the gallery shows templates for, in picker order. */
    // S4 (RemEx-pp0rt.12) added the PC idle and session families, S5 (RemEx-pp0rt.10) the sensor,
    // S2 (RemEx-pp0rt.7) the NFC tag, S3 (RemEx-pp0rt.8) arriving and leaving home.
    val offered: List<String> =
        listOf(
            RoutineTriggerTypes.HOME_ARRIVE,
            RoutineTriggerTypes.HOME_LEAVE,
            RoutineTriggerTypes.NFC_TAP,
            RoutineTriggerTypes.MANUAL,
            RoutineTriggerTypes.PC_SENSOR,
            RoutineTriggerTypes.PC_IDLE,
            RoutineTriggerTypes.PC_SESSION,
        )

    fun isOffered(type: String?): Boolean = type in offered

    /**
     * A trigger with its defaults (spec 4: everything RemEx can guess is filled), so a freshly
     * picked trigger passes the validator: idle 10 minutes, session "locked", a sensor above 85 for
     * 60 s (the sensor itself is chosen from the PC's catalog).
     */
    fun newTrigger(type: String, homeId: String? = null): RoutineTrigger =
        when (type) {
            // The phone's one home (D9); none yet leaves it unset and the editor asks for one.
            RoutineTriggerTypes.HOME_ARRIVE -> RoutineTrigger(type = type, homeId = homeId)
            RoutineTriggerTypes.HOME_LEAVE ->
                RoutineTrigger(type = type, homeId = homeId, leaveDebounceSeconds = RoutineLimits.DEFAULT_LEAVE_DEBOUNCE_SECONDS)
            RoutineTriggerTypes.PC_IDLE -> RoutineTrigger(type = type, idleMinutes = RoutineTriggerText.DEFAULT_IDLE_MINUTES, ignoreWhileMediaPlaying = true)
            RoutineTriggerTypes.PC_SESSION -> RoutineTrigger(type = type, sessionState = RoutineSessionStates.LOCKED)
            RoutineTriggerTypes.PC_SENSOR ->
                RoutineTrigger(
                    type = type,
                    direction = RoutineSensorDirections.ABOVE,
                    threshold = RoutineTriggerText.DEFAULT_SENSOR_LIMIT,
                    sustainSeconds = RoutineLimits.DEFAULT_SUSTAIN_SECONDS,
                )
            else -> RoutineTrigger(type = type)
        }
}

/** Label, editor title and picker supporting text for each trigger (spec 1.1). */
object RoutineTriggerText {
    const val DEFAULT_IDLE_MINUTES = 10

    /** The `pc.sensor` limit a freshly picked trigger starts with (a GPU/CPU temperature in °C). */
    const val DEFAULT_SENSOR_LIMIT = 85.0

    /** Choices for `pc.idle` minutes (1 to 240). */
    val idleChoices: List<Int> = listOf(1, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240)

    /** Choices for the `home.leave` debounce, in seconds (60 to 1800, default 180). */
    val leaveChoices: List<Int> = listOf(60, 180, 300, 600, 900, 1800)

    @StringRes
    fun chip(type: String?): Int =
        when (type) {
            RoutineTriggerTypes.HOME_ARRIVE -> R.string.routines_trigger_arrive_chip
            RoutineTriggerTypes.HOME_LEAVE -> R.string.routines_trigger_leave_chip
            RoutineTriggerTypes.MANUAL -> R.string.routines_trigger_manual_chip
            RoutineTriggerTypes.PC_SENSOR -> R.string.routines_trigger_sensor_chip
            RoutineTriggerTypes.NFC_TAP -> R.string.routines_trigger_nfc_chip
            RoutineTriggerTypes.PC_IDLE -> R.string.routines_trigger_idle_chip
            RoutineTriggerTypes.PC_SESSION -> R.string.routines_trigger_session_chip
            else -> R.string.routines_trigger_unknown
        }

    @StringRes
    fun title(type: String?): Int =
        when (type) {
            RoutineTriggerTypes.HOME_ARRIVE -> R.string.routines_trigger_arrive_title
            RoutineTriggerTypes.HOME_LEAVE -> R.string.routines_trigger_leave_title
            RoutineTriggerTypes.MANUAL -> R.string.routines_trigger_manual_title
            RoutineTriggerTypes.PC_SENSOR -> R.string.routines_trigger_sensor_title
            RoutineTriggerTypes.NFC_TAP -> R.string.routines_trigger_nfc_title
            RoutineTriggerTypes.PC_IDLE -> R.string.routines_trigger_idle_title
            RoutineTriggerTypes.PC_SESSION -> R.string.routines_trigger_session_title
            else -> R.string.routines_trigger_unknown
        }

    @StringRes
    fun supporting(type: String?): Int =
        when (type) {
            RoutineTriggerTypes.HOME_ARRIVE -> R.string.routines_trigger_arrive_supporting
            RoutineTriggerTypes.HOME_LEAVE -> R.string.routines_trigger_leave_supporting
            RoutineTriggerTypes.MANUAL -> R.string.routines_trigger_manual_supporting
            RoutineTriggerTypes.PC_SENSOR -> R.string.routines_trigger_sensor_supporting
            RoutineTriggerTypes.NFC_TAP -> R.string.routines_trigger_nfc_supporting
            RoutineTriggerTypes.PC_IDLE -> R.string.routines_trigger_idle_supporting
            RoutineTriggerTypes.PC_SESSION -> R.string.routines_trigger_session_supporting
            else -> R.string.routines_trigger_unknown
        }

    @StringRes
    fun sensorDirection(direction: String?): Int =
        if (direction == RoutineSensorDirections.BELOW) R.string.routines_trigger_sensor_below else R.string.routines_trigger_sensor_above

    @StringRes
    fun sessionState(state: String?): Int =
        if (state == RoutineSessionStates.UNLOCKED) R.string.routines_trigger_session_unlocked else R.string.routines_trigger_session_locked
}

/** Step vocabulary (spec 1.1). Power verbs reuse Remote Control's `rc_*` labels. */
object RoutineStepText {
    /** The picker / add-step order. */
    val addOrder: List<String> =
        listOf(
            RoutineStepTypes.WAKE,
            RoutineStepTypes.WAIT_ONLINE,
            RoutineStepTypes.DELAY,
            RoutineStepTypes.POWER,
            RoutineStepTypes.LAUNCH_APP,
            RoutineStepTypes.MEDIA,
            RoutineStepTypes.NOTIFY,
        )

    /** Power verbs the picker offers, in Remote Control's order. WAKEONLAN is never offered (D5). */
    val powerVerbs: List<String> =
        listOf(
            RoutinePowerVerbs.LOCK,
            RoutinePowerVerbs.MONITOR_OFF,
            RoutinePowerVerbs.SLEEP,
            RoutinePowerVerbs.HIBERNATE,
            RoutinePowerVerbs.SIGN_OUT,
            RoutinePowerVerbs.SHUTDOWN,
            RoutinePowerVerbs.FORCE_SHUTDOWN,
            RoutinePowerVerbs.RESTART,
            RoutinePowerVerbs.FORCE_RESTART,
            RoutinePowerVerbs.RESTART_TO_UEFI,
        )

    val mediaActions: List<String> =
        listOf(RoutineMediaActions.PLAY_PAUSE, RoutineMediaActions.NEXT, RoutineMediaActions.PREVIOUS)

    @StringRes
    fun title(type: String?): Int =
        when (type) {
            RoutineStepTypes.WAKE -> R.string.routines_step_wake_title
            RoutineStepTypes.WAIT_ONLINE -> R.string.routines_step_wait_online_title
            RoutineStepTypes.DELAY -> R.string.routines_step_delay_title
            RoutineStepTypes.POWER -> R.string.routines_step_power_title
            RoutineStepTypes.LAUNCH_APP -> R.string.routines_step_launch_title
            RoutineStepTypes.MEDIA -> R.string.routines_step_media_title
            RoutineStepTypes.NOTIFY -> R.string.routines_step_notify_title
            else -> R.string.routines_step_unknown
        }

    @StringRes
    fun supporting(type: String?): Int =
        when (type) {
            RoutineStepTypes.WAKE -> R.string.routines_step_wake_supporting
            RoutineStepTypes.WAIT_ONLINE -> R.string.routines_step_wait_online_supporting
            RoutineStepTypes.DELAY -> R.string.routines_step_delay_supporting
            RoutineStepTypes.POWER -> R.string.routines_step_power_supporting
            RoutineStepTypes.LAUNCH_APP -> R.string.routines_step_launch_supporting
            RoutineStepTypes.MEDIA -> R.string.routines_step_media_supporting
            RoutineStepTypes.NOTIFY -> R.string.routines_step_notify_supporting
            else -> R.string.routines_step_unknown
        }

    /**
     * The short chip label for a step when it needs no argument. `launchApp` with a chosen app uses
     * [R.string.routines_step_launch_chip_app] with the app name instead.
     */
    @StringRes
    fun chip(step: RoutineStep?): Int =
        when (step?.type) {
            RoutineStepTypes.WAKE -> R.string.routines_step_wake_chip
            RoutineStepTypes.WAIT_ONLINE -> R.string.routines_step_wait_online_chip
            RoutineStepTypes.DELAY -> R.string.routines_step_delay_chip
            RoutineStepTypes.POWER -> powerVerb(step.verb)
            RoutineStepTypes.LAUNCH_APP -> R.string.routines_step_launch_title
            RoutineStepTypes.MEDIA -> mediaAction(step.mediaAction)
            RoutineStepTypes.NOTIFY -> R.string.routines_step_notify_chip
            else -> R.string.routines_step_unknown
        }

    @StringRes
    fun powerVerb(verb: String?): Int =
        when (verb) {
            RoutinePowerVerbs.SHUTDOWN -> R.string.rc_shutdown
            RoutinePowerVerbs.FORCE_SHUTDOWN -> R.string.rc_force_shutdown
            RoutinePowerVerbs.RESTART -> R.string.rc_restart
            RoutinePowerVerbs.FORCE_RESTART -> R.string.rc_force_restart
            RoutinePowerVerbs.RESTART_TO_UEFI -> R.string.rc_reboot_uefi
            RoutinePowerVerbs.SIGN_OUT -> R.string.routines_power_sign_out
            RoutinePowerVerbs.SLEEP -> R.string.rc_sleep
            RoutinePowerVerbs.HIBERNATE -> R.string.rc_hibernate
            RoutinePowerVerbs.LOCK -> R.string.rc_lock_pc
            RoutinePowerVerbs.MONITOR_OFF -> R.string.rc_monitor_off
            else -> R.string.routines_step_power_title
        }

    @StringRes
    fun mediaAction(action: String?): Int =
        when (action) {
            RoutineMediaActions.NEXT -> R.string.rc_media_next
            RoutineMediaActions.PREVIOUS -> R.string.rc_media_previous
            else -> R.string.rc_media_play_pause
        }

    /**
     * The editor's error tint (spec 1.1 "Destructive classification (UX view)", R-UX-54): the
     * "discards work" set Remote Control already confirms. Sleep and hibernate still count down on the
     * PC ([RoutineStep.isDestructive]) but do not lose work, so they are not tinted.
     */
    fun discardsWork(step: RoutineStep?): Boolean =
        step?.type == RoutineStepTypes.POWER &&
            step.verb in
            setOf(
                RoutinePowerVerbs.SHUTDOWN,
                RoutinePowerVerbs.FORCE_SHUTDOWN,
                RoutinePowerVerbs.RESTART,
                RoutinePowerVerbs.FORCE_RESTART,
                RoutinePowerVerbs.RESTART_TO_UEFI,
                RoutinePowerVerbs.SIGN_OUT,
            )

    /** A new step of [type] with its defaults (spec 4: defaults filled for everything RemEx can guess). */
    fun newStep(type: String, mac: String?): RoutineStep =
        when (type) {
            RoutineStepTypes.WAKE -> RoutineStep(type = type, mac = mac)
            RoutineStepTypes.WAIT_ONLINE -> RoutineStep(type = type, timeoutSeconds = RoutineLimits.DEFAULT_WAIT_ONLINE_SECONDS)
            RoutineStepTypes.DELAY -> RoutineStep(type = type, seconds = 30)
            RoutineStepTypes.POWER -> RoutineStep(type = type, verb = RoutinePowerVerbs.LOCK)
            RoutineStepTypes.LAUNCH_APP -> RoutineStep(type = type)
            RoutineStepTypes.MEDIA -> RoutineStep(type = type, mediaAction = RoutineMediaActions.PLAY_PAUSE)
            RoutineStepTypes.NOTIFY -> RoutineStep(type = type, target = RoutineNotifyTargets.PHONE)
            else -> RoutineStep(type = type)
        }

    /** Choices for the `waitOnline` timeout (30 to 300 s) and the `delay` length (1 to 600 s). */
    val waitOnlineChoices: List<Int> = listOf(30, 60, 90, 120, 180, 240, 300)
    val delayChoices: List<Int> = listOf(5, 10, 15, 30, 45, 60, 90, 120, 180, 300, 450, 600)
}

/** Gallery filter groups (spec A3). Only groups that hold an offered template are shown. */
enum class RoutineTemplateCategory(@StringRes val labelRes: Int) {
    HOME(R.string.routines_category_home),
    GAMING(R.string.routines_category_gaming),
    MEDIA(R.string.routines_category_media),
    FOCUS(R.string.routines_category_focus),
    HEALTH(R.string.routines_category_health),
    PRIVACY(R.string.routines_category_privacy),
    POWER(R.string.routines_category_power),
}

/** The gallery "Needs" tokens (spec 4.2) the offered templates use. */
enum class RoutineRequirement(@StringRes val labelRes: Int) {
    MAC_ADDRESS(R.string.routines_need_mac),
    LAUNCHER_ENTRY(R.string.routines_need_launcher),
    MEDIA_KEYS(R.string.routines_need_media_keys),
    TEMPERATURE_SENSOR(R.string.routines_need_temperature_sensor),
    MEMORY_SENSOR(R.string.routines_need_memory_sensor),
    NFC(R.string.routines_need_nfc),
    HOME_NETWORK(R.string.routines_need_home),
}

/**
 * One step of a template. Localised text (a notify title and body) is resolved when the template is
 * opened, because once a routine exists its text is user data and is never re-localised (R-UX-57).
 */
data class RoutineTemplateStep(
    val step: RoutineStep,
    @StringRes val notifyBodyRes: Int? = null,
)

/**
 * A starter routine (spec 4.1). A notify step's title is the template's own localised name, which
 * is what a message from "Game night" should be headed with. [sensorPreset] is the sensor a health
 * template picks from the PC's catalog when it opens (by kind, then by name).
 */
data class RoutineTemplate(
    val id: String,
    val category: RoutineTemplateCategory,
    @StringRes val nameRes: Int,
    @StringRes val whyRes: Int,
    val trigger: RoutineTrigger,
    val steps: List<RoutineTemplateStep>,
    val needs: List<RoutineRequirement>,
    val sensorPreset: RoutineSensorPreset? = null,
)

object RoutineTemplates {
    private val wait5 = RoutineStep(type = RoutineStepTypes.WAIT_ONLINE, timeoutSeconds = 300)
    private val wait3 = RoutineStep(type = RoutineStepTypes.WAIT_ONLINE, timeoutSeconds = 180)

    /** A health template's trigger: "above" [limit] for [sustainSeconds]; the sensor is filled on open. */
    private fun sensorTrigger(limit: Double, sustainSeconds: Int) =
        RoutineTrigger(
            type = RoutineTriggerTypes.PC_SENSOR,
            direction = RoutineSensorDirections.ABOVE,
            threshold = limit,
            sustainSeconds = sustainSeconds,
        )

    /** Every template this build knows, catalog order (spec 4.1). */
    val all: List<RoutineTemplate> =
        listOf(
            RoutineTemplate(
                id = "tpl.home.wake",
                category = RoutineTemplateCategory.HOME,
                nameRes = R.string.routines_tpl_home_wake_name,
                whyRes = R.string.routines_tpl_home_wake_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.HOME_ARRIVE),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAKE)),
                        RoutineTemplateStep(wait5),
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE),
                            notifyBodyRes = R.string.routines_tpl_home_wake_message,
                        ),
                    ),
                needs = listOf(RoutineRequirement.HOME_NETWORK, RoutineRequirement.MAC_ADDRESS),
            ),
            RoutineTemplate(
                id = "tpl.home.lock",
                category = RoutineTemplateCategory.HOME,
                nameRes = R.string.routines_tpl_home_lock_name,
                whyRes = R.string.routines_tpl_home_lock_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_IDLE, idleMinutes = 5, ignoreWhileMediaPlaying = true),
                steps = listOf(RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.LOCK))),
                needs = emptyList(),
            ),
            RoutineTemplate(
                id = "tpl.home.sleep",
                category = RoutineTemplateCategory.HOME,
                nameRes = R.string.routines_tpl_home_sleep_name,
                whyRes = R.string.routines_tpl_home_sleep_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_SESSION, sessionState = RoutineSessionStates.LOCKED),
                steps = listOf(RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.SLEEP))),
                needs = emptyList(),
            ),
            RoutineTemplate(
                id = "tpl.home.music",
                category = RoutineTemplateCategory.HOME,
                nameRes = R.string.routines_tpl_home_music_name,
                whyRes = R.string.routines_tpl_home_music_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.HOME_ARRIVE),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAIT_ONLINE, timeoutSeconds = 120)),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.MEDIA, mediaAction = RoutineMediaActions.PLAY_PAUSE)),
                    ),
                needs = listOf(RoutineRequirement.HOME_NETWORK, RoutineRequirement.MEDIA_KEYS),
            ),
            RoutineTemplate(
                id = "tpl.game.steam",
                category = RoutineTemplateCategory.GAMING,
                nameRes = R.string.routines_tpl_game_steam_name,
                whyRes = R.string.routines_tpl_game_steam_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.HOME_ARRIVE),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAKE)),
                        RoutineTemplateStep(wait5),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.LAUNCH_APP)),
                    ),
                needs = listOf(RoutineRequirement.HOME_NETWORK, RoutineRequirement.MAC_ADDRESS, RoutineRequirement.LAUNCHER_ENTRY),
            ),
            RoutineTemplate(
                id = "tpl.game.night",
                category = RoutineTemplateCategory.GAMING,
                nameRes = R.string.routines_tpl_game_night_name,
                whyRes = R.string.routines_tpl_game_night_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.MANUAL),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAKE)),
                        RoutineTemplateStep(wait5),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.LAUNCH_APP)),
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE),
                            notifyBodyRes = R.string.routines_tpl_game_night_message,
                        ),
                    ),
                needs = listOf(RoutineRequirement.MAC_ADDRESS, RoutineRequirement.LAUNCHER_ENTRY),
            ),
            RoutineTemplate(
                id = "tpl.media.movie",
                category = RoutineTemplateCategory.MEDIA,
                nameRes = R.string.routines_tpl_media_movie_name,
                whyRes = R.string.routines_tpl_media_movie_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.NFC_TAP),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAKE)),
                        RoutineTemplateStep(wait3),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.LAUNCH_APP)),
                    ),
                needs = listOf(RoutineRequirement.NFC, RoutineRequirement.MAC_ADDRESS, RoutineRequirement.LAUNCHER_ENTRY),
            ),
            RoutineTemplate(
                id = "tpl.media.screenoff",
                category = RoutineTemplateCategory.MEDIA,
                nameRes = R.string.routines_tpl_media_screenoff_name,
                whyRes = R.string.routines_tpl_media_screenoff_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.NFC_TAP),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.MEDIA, mediaAction = RoutineMediaActions.PLAY_PAUSE)),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.MONITOR_OFF)),
                    ),
                needs = listOf(RoutineRequirement.NFC, RoutineRequirement.MEDIA_KEYS),
            ),
            RoutineTemplate(
                id = "tpl.media.next",
                category = RoutineTemplateCategory.MEDIA,
                nameRes = R.string.routines_tpl_media_next_name,
                whyRes = R.string.routines_tpl_media_next_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.MANUAL),
                steps = listOf(RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.MEDIA, mediaAction = RoutineMediaActions.NEXT))),
                needs = listOf(RoutineRequirement.MEDIA_KEYS),
            ),
            RoutineTemplate(
                id = "tpl.work.start",
                category = RoutineTemplateCategory.FOCUS,
                nameRes = R.string.routines_tpl_work_start_name,
                whyRes = R.string.routines_tpl_work_start_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.MANUAL),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAKE)),
                        RoutineTemplateStep(wait5),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.LAUNCH_APP)),
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PC),
                            notifyBodyRes = R.string.routines_tpl_work_start_message,
                        ),
                    ),
                needs = listOf(RoutineRequirement.MAC_ADDRESS, RoutineRequirement.LAUNCHER_ENTRY),
            ),
            RoutineTemplate(
                id = "tpl.work.desk",
                category = RoutineTemplateCategory.FOCUS,
                nameRes = R.string.routines_tpl_work_desk_name,
                whyRes = R.string.routines_tpl_work_desk_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.NFC_TAP),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.WAKE)),
                        RoutineTemplateStep(wait3),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.LAUNCH_APP)),
                    ),
                needs = listOf(RoutineRequirement.NFC, RoutineRequirement.MAC_ADDRESS, RoutineRequirement.LAUNCHER_ENTRY),
            ),
            RoutineTemplate(
                id = "tpl.health.gpu",
                category = RoutineTemplateCategory.HEALTH,
                nameRes = R.string.routines_tpl_health_gpu_name,
                whyRes = R.string.routines_tpl_health_gpu_why,
                trigger = sensorTrigger(limit = 85.0, sustainSeconds = 30),
                steps =
                    listOf(
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE),
                            notifyBodyRes = R.string.routines_tpl_health_gpu_message,
                        ),
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PC),
                            notifyBodyRes = R.string.routines_tpl_health_gpu_message,
                        ),
                    ),
                needs = listOf(RoutineRequirement.TEMPERATURE_SENSOR),
                sensorPreset = RoutineSensorPreset.GPU_TEMP,
            ),
            RoutineTemplate(
                id = "tpl.health.cpu",
                category = RoutineTemplateCategory.HEALTH,
                nameRes = R.string.routines_tpl_health_cpu_name,
                whyRes = R.string.routines_tpl_health_cpu_why,
                trigger = sensorTrigger(limit = 95.0, sustainSeconds = 60),
                steps =
                    listOf(
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE),
                            notifyBodyRes = R.string.routines_tpl_health_cpu_message,
                        ),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.SLEEP)),
                    ),
                needs = listOf(RoutineRequirement.TEMPERATURE_SENSOR),
                sensorPreset = RoutineSensorPreset.CPU_TEMP,
            ),
            RoutineTemplate(
                id = "tpl.health.ram",
                category = RoutineTemplateCategory.HEALTH,
                nameRes = R.string.routines_tpl_health_ram_name,
                whyRes = R.string.routines_tpl_health_ram_why,
                trigger = sensorTrigger(limit = 90.0, sustainSeconds = 120),
                steps =
                    listOf(
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE),
                            notifyBodyRes = R.string.routines_tpl_health_ram_message,
                        ),
                    ),
                needs = listOf(RoutineRequirement.MEMORY_SENSOR),
                sensorPreset = RoutineSensorPreset.RAM_LOAD,
            ),
            RoutineTemplate(
                id = "tpl.priv.tag",
                category = RoutineTemplateCategory.PRIVACY,
                nameRes = R.string.routines_tpl_priv_tag_name,
                whyRes = R.string.routines_tpl_priv_tag_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.NFC_TAP),
                steps =
                    listOf(
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.LOCK)),
                        RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.MONITOR_OFF)),
                    ),
                needs = listOf(RoutineRequirement.NFC),
            ),
            RoutineTemplate(
                id = "tpl.priv.unlock",
                category = RoutineTemplateCategory.PRIVACY,
                nameRes = R.string.routines_tpl_priv_unlock_name,
                whyRes = R.string.routines_tpl_priv_unlock_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_SESSION, sessionState = RoutineSessionStates.UNLOCKED),
                steps =
                    listOf(
                        RoutineTemplateStep(
                            RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE),
                            notifyBodyRes = R.string.routines_tpl_priv_unlock_message,
                        ),
                    ),
                needs = emptyList(),
            ),
            RoutineTemplate(
                id = "tpl.power.sleep",
                category = RoutineTemplateCategory.POWER,
                nameRes = R.string.routines_tpl_power_sleep_name,
                whyRes = R.string.routines_tpl_power_sleep_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_IDLE, idleMinutes = 30, ignoreWhileMediaPlaying = true),
                steps = listOf(RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.SLEEP))),
                needs = emptyList(),
            ),
            RoutineTemplate(
                id = "tpl.power.screen",
                category = RoutineTemplateCategory.POWER,
                nameRes = R.string.routines_tpl_power_screen_name,
                whyRes = R.string.routines_tpl_power_screen_why,
                trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_IDLE, idleMinutes = 10, ignoreWhileMediaPlaying = true),
                steps = listOf(RoutineTemplateStep(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.MONITOR_OFF))),
                needs = emptyList(),
            ),
        )

    /**
     * The spec's featured set for the empty state (spec 1.3, 4.1) in preference order. Ids whose
     * trigger family is not offered yet are skipped and the rest of the gallery fills the gap, so
     * the empty state always shows three cards when three exist.
     */
    private val featuredPreference = listOf("tpl.home.wake", "tpl.power.sleep", "tpl.priv.tag", "tpl.game.night")

    /** Templates the gallery shows: the ones whose trigger this build offers. */
    fun offered(): List<RoutineTemplate> = all.filter { RoutineTriggerFamilies.isOffered(it.trigger.type) }

    /** Up to three featured templates for the empty state (R-UX-07). */
    fun featured(hasNfc: Boolean): List<RoutineTemplate> {
        val offered = offered()
        val preference = if (hasNfc) featuredPreference - "tpl.game.night" else featuredPreference - "tpl.priv.tag"
        val picked = preference.mapNotNull { id -> offered.firstOrNull { it.id == id } }
        return (picked + offered.filterNot { it in picked }).take(3)
    }

    fun byId(id: String?): RoutineTemplate? = all.firstOrNull { it.id == id }

    /** Gallery filter groups that hold at least one offered template, in enum order. */
    fun categories(): List<RoutineTemplateCategory> {
        val used = offered().map { it.category }.toSet()
        return RoutineTemplateCategory.entries.filter { it in used }
    }
}
