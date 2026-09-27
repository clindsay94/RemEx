package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineCancelPayload
import com.clindsay94.remex.routines.model.RoutineCancelledBy
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineNotifyTargets
import com.clindsay94.remex.routines.model.RoutineOutbound
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunAttributes
import com.clindsay94.remex.routines.model.RoutineRunCountdown
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineRunStep
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepOutcomes
import com.clindsay94.remex.routines.model.RoutineStepRequestPayload
import com.clindsay94.remex.routines.model.RoutineStepResultPayload
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineText
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.routines.model.RoutineValidator
import kotlin.math.min
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Executes one phone run, step by step (routines spec §8.1, §8.2, §8.6, §8.9; RemEx-pp0rt.5).
 *
 * Pure Kotlin against the ports in RoutineRunPorts.kt; [RoutineWorker] is the Android vehicle.
 *
 * The rules it keeps, each pinned by `RoutineRunnerTest`:
 * - **At most once.** Every step is recorded `running` BEFORE it starts. A retried worker that finds
 *   a started step marks the run `interrupted_phone` and stops; it never repeats a step.
 * - **Preconditions at the start and before every step:** the routine still exists and is valid,
 *   it is enabled (except in-app Run and Test), Pause all has not stopped an automatic source, the
 *   PC is still paired, and a host step goes only to the PC RemEx is set to (`pc_not_selected`).
 * - **The 540 s budget** is a hard stop (`budget_exceeded`); the validator already guarantees it
 *   statically, so this only fires when Android or the PC made a wait run long.
 * - **Host steps** are sent as `routine_step_request` and awaited by `(runId, stepIndex)` on a flow
 *   of their own; the SAME request is resent at most twice on transport loss and never after the PC
 *   has answered (it deduplicates by that key, §7.3.3).
 * - **Cancel** during a host step sends `routine_cancel` and keeps listening, because the PC's
 *   answer is the truth about what happened; during a phone step it stops the step at once.
 * - **Test runs** carry `testRun` on every host step; the PC answers `simulated` for a destructive
 *   verb and the run gets the `simulated` attribute. Phone-side steps run for real (§8.9).
 */
class RoutineRunner(
    private val store: RoutineRunStore,
    private val link: RoutineHostLink,
    private val phone: RoutinePhone,
    private val observer: RoutineRunObserver,
    private val clock: RoutineClock,
    private val controls: RoutineRunControls,
) {
    suspend fun execute(ticket: RoutineRunTicket): RoutineRun? {
        val control = controls.obtain(ticket.runId, ticket.routineId)
        control.attached = true
        try {
            return executeAttached(ticket, control)
        } finally {
            control.attached = false
            controls.release(ticket.runId)
        }
    }

    private suspend fun executeAttached(ticket: RoutineRunTicket, control: RoutineRunControl): RoutineRun? {
        val stored = store.findRun(ticket.runId)
        if (stored != null && stored.outcome != RoutineRunOutcomes.RUNNING) return stored

        val routine = store.routine(ticket.routineId)
        val base =
            stored
                ?: routine?.let {
                    RoutineRunRecords.queued(ticket.runId, it, ticket.source, ticket.testRun, ticket.triggeredAtUnixMs, ticket.sourceDetail)
                }
                ?: return null

        // FROM HERE ON A RECORD EXISTS, AND EVERY WAY OUT FINALISES IT. A throw that escaped (a
        // DataStore IOException reading the PC's nickname, a failed persist) used to leave the
        // record `running`, and every later start of the routine was skipped `already_running` until
        // the process died. Now any exception ends the run `internal_error` through the normal
        // finish path; the worker's own catch is only the backstop for a finish that itself fails.
        var exec: Execution? = null
        try {
            if (routine == null) {
                return finishEarly(base, null, RoutineRunOutcomes.CANCELLED, RoutineReasonCodes.CANCELLED_ON_PHONE, RoutineCancelledBy.PHONE)
            }

            // At most once (§8.2): a record that already shows a started step means an earlier
            // attempt of THIS run was stopped part-way. Repeating a step could shut a PC down twice.
            if (stored != null && stored.steps.orEmpty().any { it.status != RoutineStepStatuses.PENDING }) {
                RoutineLog.w("Run ${RoutineLog.id(ticket.runId)} restarted after a step began; recording interrupted_phone.")
                return finishEarly(stored, routine, RoutineRunOutcomes.INTERRUPTED, RoutineReasonCodes.INTERRUPTED_PHONE, null)
            }

            startBlocker(routine, ticket, control)?.let { (outcome, code, by) ->
                return finishEarly(base, routine, outcome, code, by)
            }

            val host = checkNotNull(routine.hostIdentity) { "validated routine has a host identity" }
            val started = Execution(base, routine, routine.steps.orEmpty(), control, host, link.displayName(host), ticket)
            exec = started
            val startedElapsed = clock.elapsedRealtimeMs()
            // The PC's display name rides in reasonArgs from the start, so progress text can say it.
            started.run = started.run.copy(startedAtUnixMs = clock.nowUnixMs(), reasonArgs = started.args(null))
            if (isDeferred(ticket, startedElapsed)) started.attributes += RoutineRunAttributes.DEFERRED_BY_OS
            if (phone.isBackgroundRestricted()) started.attributes += RoutineRunAttributes.BACKGROUND_RESTRICTED
            persist(started)
            notifyObserver { observer.onProgress(started.snapshot(), routine, null) }

            val completed = withTimeoutOrNull(BUDGET_MS) { runSteps(started) }
            if (completed == null) {
                // The budget ran out, possibly mid-countdown: tell the PC before giving up on it.
                withContext(NonCancellable) { sendCancel(started, RoutineCancelKind.USER.wireReason) }
                return finish(started, Ending.Failed(RoutineReasonCodes.BUDGET_EXCEEDED, started.args(null)))
            }
            return finish(started, completed)
        } catch (e: CancellationException) {
            // Stopped from outside. A host step in flight is cancelled on the PC either way: the
            // phone is no longer listening, so it can no longer honour a Cancel for it.
            val kind = control.cancelRequest.value
            exec?.let { current -> withContext(NonCancellable) { sendCancel(current, (kind ?: RoutineCancelKind.USER).wireReason) } }
            // Only a cancel WE requested is recorded as one; anything else (Android stopping the job)
            // leaves the record `running`, and the retry or the sweep reports interrupted_phone.
            if (kind == null) throw e
            return withContext(NonCancellable) {
                val current = exec
                if (current != null) finish(current, Ending.Cancelled(kind.reasonCode, kind.cancelledBy))
                else finishEarly(base, routine, RoutineRunOutcomes.CANCELLED, kind.reasonCode, kind.cancelledBy)
            }
        } catch (e: Exception) {
            RoutineLog.e("Run ${RoutineLog.id(ticket.runId)} failed unexpectedly.", e)
            return withContext(NonCancellable) {
                val current = exec
                if (current != null) {
                    sendCancel(current, RoutineCancelKind.USER.wireReason)
                    finish(current, Ending.Failed(RoutineReasonCodes.INTERNAL_ERROR, current.args(null)))
                } else {
                    finishEarly(base, routine, RoutineRunOutcomes.FAILED, RoutineReasonCodes.INTERNAL_ERROR, null)
                }
            }
        }
    }

    /** Queues `routine_cancel` for the host step in flight, once per step. Never throws. */
    private fun sendCancel(exec: Execution, wireReason: String) {
        val step = exec.inFlightStep ?: return
        if (exec.cancelSentForStep == step) return
        exec.cancelSentForStep = step
        val sent =
            try {
                link.send(RoutineOutbound.cancel(RoutineCancelPayload(exec.runId, wireReason)))
            } catch (e: Exception) {
                RoutineLog.w("Queueing routine_cancel failed.", e)
                false
            }
        RoutineLog.i("Run ${RoutineLog.id(exec.runId)}: routine_cancel for step $step ${if (sent) "sent" else "could not be queued"}.")
    }

    /** Progress and results are presentation: a notification failure must never fail or strand a run. */
    private inline fun notifyObserver(block: () -> Unit) {
        try {
            block()
        } catch (e: Exception) {
            RoutineLog.w("A routine progress notification failed.", e)
        }
    }

    /** Why this run may not start at all, as (outcome, reason code, cancelledBy); null to start. */
    private suspend fun startBlocker(
        routine: Routine,
        ticket: RoutineRunTicket,
        control: RoutineRunControl,
    ): Triple<String, String, String?>? {
        control.cancelRequest.value?.let { return Triple(RoutineRunOutcomes.CANCELLED, it.reasonCode, it.cancelledBy) }
        val verdict = RoutineValidator.validateRoutine(routine)
        if (!verdict.isValid) return Triple(RoutineRunOutcomes.FAILED, verdict.reasonCode, null)
        if (!routine.enabled && ticket.source != RoutineRunSources.MANUAL_APP) {
            return Triple(RoutineRunOutcomes.SKIPPED, RoutineReasonCodes.SKIPPED_DISABLED, null)
        }
        if (RoutineTriggerTypes.isAutomatic(ticket.source) && store.isPausedAll()) {
            return Triple(RoutineRunOutcomes.SKIPPED, RoutineReasonCodes.PAUSED_ON_PHONE, null)
        }
        val host = routine.hostIdentity
        if (host == null || !link.isPaired(host)) return Triple(RoutineRunOutcomes.FAILED, RoutineReasonCodes.PC_NOT_PAIRED, null)
        return null
    }

    private fun isDeferred(ticket: RoutineRunTicket, startedElapsed: Long): Boolean {
        // Elapsed realtime when the trigger happened in this boot; wall time when it did not.
        val lateMs =
            if (ticket.triggeredAtElapsedMs in 1..startedElapsed) startedElapsed - ticket.triggeredAtElapsedMs
            else clock.nowUnixMs() - ticket.triggeredAtUnixMs
        return lateMs > DEFERRED_THRESHOLD_MS
    }

    // ── The step loop ──

    private suspend fun runSteps(exec: Execution): Ending {
        for (index in exec.steps.indices) {
            exec.currentIndex = index
            preconditionBeforeStep(exec)?.let { return it }
            val step = exec.steps[index] ?: return Ending.Failed(RoutineReasonCodes.INVALID_FIELD, null)

            if (RoutineRunRecords.isAfterPowerOff(exec.steps, exec.stepRecords.map { it.status }, index)) {
                exec.markStep(index, RoutineStepStatuses.SKIPPED, RoutineReasonCodes.AFTER_POWER_OFF, exec.args(step), clock.nowUnixMs())
                persist(exec)
                continue
            }

            exec.markStep(index, RoutineStepStatuses.RUNNING, null, null, clock.nowUnixMs(), started = true)
            persist(exec)
            notifyObserver { observer.onProgress(exec.snapshot(), exec.routine, index) }

            val outcome = runStep(exec, index, step)
            val endedAt = clock.nowUnixMs()
            when (outcome) {
                StepOutcome.Succeeded -> exec.markStep(index, RoutineStepStatuses.SUCCEEDED, RoutineReasonCodes.OK, null, endedAt)
                StepOutcome.Simulated -> {
                    exec.markStep(index, RoutineStepStatuses.SIMULATED, RoutineReasonCodes.SIMULATED, exec.args(step), endedAt)
                    exec.attributes += RoutineRunAttributes.SIMULATED
                }
                is StepOutcome.Skipped -> exec.markStep(index, RoutineStepStatuses.SKIPPED, outcome.code, exec.args(step), endedAt)
                is StepOutcome.Failed -> {
                    exec.markStep(index, RoutineStepStatuses.FAILED, outcome.code, outcome.args, endedAt)
                    persist(exec)
                    return Ending.Failed(outcome.code, outcome.args)
                }
                is StepOutcome.Cancelled -> {
                    exec.markStep(index, RoutineStepStatuses.CANCELLED, outcome.code, null, endedAt)
                    persist(exec)
                    return Ending.Cancelled(outcome.code, outcome.cancelledBy)
                }
            }
            persist(exec)
        }
        return Ending.Succeeded
    }

    private suspend fun preconditionBeforeStep(exec: Execution): Ending? {
        exec.control.cancelRequest.value?.let { return Ending.Cancelled(it.reasonCode, it.cancelledBy) }
        val current = store.routine(exec.routineId) ?: return Ending.Cancelled(RoutineReasonCodes.CANCELLED_ON_PHONE, RoutineCancelledBy.PHONE)
        if (!current.enabled && exec.ticket.source != RoutineRunSources.MANUAL_APP) {
            return Ending.Cancelled(RoutineReasonCodes.SKIPPED_DISABLED, RoutineCancelledBy.PHONE)
        }
        if (RoutineTriggerTypes.isAutomatic(exec.ticket.source) && store.isPausedAll()) {
            return Ending.Cancelled(RoutineCancelKind.PAUSE.reasonCode, RoutineCancelKind.PAUSE.cancelledBy)
        }
        if (!link.isPaired(exec.host)) return Ending.Failed(RoutineReasonCodes.PC_NOT_PAIRED, exec.args(null))
        return null
    }

    private suspend fun runStep(exec: Execution, index: Int, step: RoutineStep): StepOutcome =
        when {
            step.type == RoutineStepTypes.WAKE -> wake(exec, step)
            step.type == RoutineStepTypes.WAIT_ONLINE -> waitOnline(exec, step)
            step.type == RoutineStepTypes.DELAY -> delayStep(exec, step)
            step.type == RoutineStepTypes.NOTIFY && step.target == RoutineNotifyTargets.PHONE -> notifyPhone(exec, index, step)
            step.isHostExecuted -> hostStep(exec, index, step)
            else -> StepOutcome.Failed(RoutineReasonCodes.UNSUPPORTED_STEP, null)
        }

    // ── Phone-side steps ──

    private suspend fun wake(exec: Execution, step: RoutineStep): StepOutcome {
        val args = exec.args(step)
        val mac = step.mac?.takeIf { it.isNotBlank() } ?: return StepOutcome.Failed(RoutineReasonCodes.WAKE_NO_MAC, args)
        if (!phone.hasLocalNetworkPermission()) return StepOutcome.Failed(RoutineReasonCodes.PERMISSION_LOCAL_NETWORK, args)
        val broadcastIp = step.broadcastIp ?: RoutineLimits.DEFAULT_BROADCAST_IP
        val port = step.port ?: RoutineLimits.DEFAULT_WAKE_PORT

        // Magic packets are idempotent, so retrying is always safe (§8.2: 5 s each, 2 retries 1 s apart).
        val sent =
            cancellable(exec.control) {
                var ok = false
                for (attempt in 0 until WAKE_ATTEMPTS) {
                    if (attempt > 0) delay(WAKE_RETRY_GAP_MS)
                    ok = withTimeoutOrNull(WAKE_TIMEOUT_MS) { phone.sendWakePacket(mac, broadcastIp, port) } == true
                    if (ok) break
                }
                ok
            } ?: return exec.cancelledOutcome()
        if (!sent) {
            RoutineLog.w("Wake packet for ${RoutineLog.mac(mac)} did not leave the phone.")
            return StepOutcome.Failed(RoutineReasonCodes.WAKE_SEND_FAILED, args)
        }
        exec.wakeSent = true
        return StepOutcome.Succeeded
    }

    private suspend fun waitOnline(exec: Execution, step: RoutineStep): StepOutcome {
        val seconds = step.timeoutSeconds ?: RoutineLimits.DEFAULT_WAIT_ONLINE_SECONDS
        val args = exec.args(step).copy(duration = seconds.toString())
        if (link.selectedHostIdentity() != exec.host) return StepOutcome.Failed(RoutineReasonCodes.PC_NOT_SELECTED, args)
        val online = cancellable(exec.control) { waitUntilOnline(exec.host, seconds * 1000L) } ?: return exec.cancelledOutcome()
        if (online) return StepOutcome.Succeeded
        val code = if (exec.wakeSent) RoutineReasonCodes.WAIT_TIMEOUT else unreachableCode(exec.host)
        return StepOutcome.Failed(code, args)
    }

    /** Connect attempts until the routine's PC is authenticated or [timeoutMs] passes. */
    private suspend fun waitUntilOnline(host: String, timeoutMs: Long): Boolean {
        val deadline = clock.elapsedRealtimeMs() + timeoutMs
        while (true) {
            if (isAuthenticatedTo(host)) return true
            val remaining = deadline - clock.elapsedRealtimeMs()
            if (remaining <= 0) return false
            if (!link.startOneShotConnect()) return false
            withTimeoutOrNull(min(CONNECT_WAIT_MS, remaining)) { link.authenticated.first { it } }
            if (isAuthenticatedTo(host)) return true
            // A refusal can come back at once; pause so the loop does not hammer the PC.
            val left = deadline - clock.elapsedRealtimeMs()
            if (left <= 0) return false
            delay(min(RETRY_PAUSE_MS, left))
        }
    }

    private suspend fun delayStep(exec: Execution, step: RoutineStep): StepOutcome {
        val seconds = step.seconds ?: return StepOutcome.Failed(RoutineReasonCodes.INVALID_FIELD, null)
        cancellable(exec.control) { delay(seconds * 1000L) } ?: return exec.cancelledOutcome()
        return StepOutcome.Succeeded
    }

    private fun notifyPhone(exec: Execution, index: Int, step: RoutineStep): StepOutcome {
        val posted = phone.postMessage(exec.snapshot(), index, RoutineText.sanitize(step.title), RoutineText.sanitize(step.body))
        // Recorded, never a failure: the routine did its job, the phone just could not show it.
        if (!posted) exec.attributes += RoutineReasonCodes.NOTIFY_DENIED_PHONE
        return StepOutcome.Succeeded
    }

    // ── Host-executed steps ──

    private suspend fun hostStep(exec: Execution, index: Int, step: RoutineStep): StepOutcome {
        val args = exec.args(step)
        val host = exec.host
        if (link.selectedHostIdentity() != host) return StepOutcome.Failed(RoutineReasonCodes.PC_NOT_SELECTED, args)
        val connected = ensureConnected(host, exec.control) ?: return exec.cancelledOutcome()
        if (!connected) return StepOutcome.Failed(unreachableCode(host), args)
        // Never falls back to the raw `command` verb on an older PC: that would bypass the countdown.
        if (!link.supportsRoutines(host)) return StepOutcome.Failed(RoutineReasonCodes.PC_TOO_OLD, args)

        val destructive = step.isDestructive
        val request =
            RoutineStepRequestPayload(
                runId = exec.runId,
                routineId = exec.routineId,
                routineName = exec.routine.name?.take(RoutineLimits.MAX_NAME_LENGTH),
                triggerType = if (exec.ticket.testRun) RoutineTriggerTypes.MANUAL else exec.routine.trigger?.type,
                stepIndex = index,
                step = step,
                testRun = exec.ticket.testRun,
                source = exec.ticket.source,
            )
        if (destructive) {
            val sentAt = clock.nowUnixMs()
            exec.countdown = RoutineRunCountdown(shown = false, startedAtUnixMs = sentAt)
            persist(exec)
            notifyObserver { observer.onCountdown(exec.snapshot(), exec.routine, index, sentAt + RoutineLimits.COUNTDOWN_SECONDS * 1000L) }
        }
        val timeoutMs = HOST_STEP_TIMEOUT_MS + if (destructive) RoutineLimits.COUNTDOWN_SECONDS * 1000L else 0L
        // In flight from here until the reply is in hand. Deliberately NOT cleared in a finally: if
        // the budget or a stop cuts the wait short, executeAttached must still see it and send
        // routine_cancel, or a PC countdown carries on after the phone has given up (§8.6).
        exec.inFlightStep = index
        val reply = awaitStepResult(exec, index, RoutineOutbound.stepRequest(request), timeoutMs)
        exec.inFlightStep = null
        return when (reply) {
            HostReply.Timeout -> StepOutcome.Failed(RoutineReasonCodes.STEP_TIMEOUT, args)
            HostReply.TransportLost -> StepOutcome.Failed(RoutineReasonCodes.TRANSPORT_LOST, args)
            is HostReply.Result -> mapResult(exec, reply.payload, args, destructive)
        }
    }

    private fun mapResult(exec: Execution, result: RoutineStepResultPayload, args: RoutineReasonArgs, destructive: Boolean): StepOutcome {
        if (destructive) {
            exec.countdown = exec.countdown?.copy(shown = result.countdownShown, cancelledBy = result.cancelledBy)
            if (!result.countdownShown && result.outcome == RoutineStepOutcomes.SUCCEEDED) {
                exec.attributes += RoutineRunAttributes.COUNTDOWN_UNSEEN
            }
        }
        // Another countdown was already running on the PC: refused on the wire as `failed`, but
        // recorded as a skipped step, like the PC's own history (spec §8.6 "Conflicts").
        if (result.outcome == RoutineStepOutcomes.FAILED && result.reasonCode == RoutineReasonCodes.CONFLICT_COUNTDOWN_ACTIVE) {
            return StepOutcome.Skipped(RoutineReasonCodes.CONFLICT_COUNTDOWN_ACTIVE)
        }
        return when (result.outcome) {
            RoutineStepOutcomes.SUCCEEDED -> StepOutcome.Succeeded
            RoutineStepOutcomes.SIMULATED -> StepOutcome.Simulated
            RoutineStepOutcomes.FAILED ->
                StepOutcome.Failed(
                    result.reasonCode?.takeIf { it.isNotBlank() && it != RoutineReasonCodes.OK } ?: RoutineReasonCodes.INTERNAL_ERROR,
                    args,
                )
            RoutineStepOutcomes.CANCELLED -> {
                val by = result.cancelledBy ?: RoutineCancelledBy.PC
                val code =
                    result.reasonCode?.takeIf { it in CANCEL_CODES }
                        ?: if (by == RoutineCancelledBy.PC) RoutineReasonCodes.CANCELLED_ON_PC else RoutineReasonCodes.CANCELLED_ON_PHONE
                StepOutcome.Cancelled(code, by)
            }
            else -> StepOutcome.Failed(RoutineReasonCodes.INTERNAL_ERROR, args)
        }
    }

    /**
     * Sends [json] and waits for its `routine_step_result`. Subscribes BEFORE sending, so an answer
     * that beats the send's own return cannot be missed.
     */
    private suspend fun awaitStepResult(exec: Execution, index: Int, json: String, timeoutMs: Long): HostReply =
        coroutineScope {
            val inbox = Channel<RoutineStepResultPayload>(Channel.UNLIMITED)
            val listener =
                launch(start = CoroutineStart.UNDISPATCHED) {
                    link.stepResults.collect { if (it.runId == exec.runId && it.stepIndex == index) inbox.send(it) }
                }
            try {
                waitForReply(exec, json, timeoutMs, inbox)
            } finally {
                listener.cancel()
            }
        }

    private suspend fun waitForReply(
        exec: Execution,
        json: String,
        timeoutMs: Long,
        inbox: ReceiveChannel<RoutineStepResultPayload>,
    ): HostReply {
        val deadline = clock.elapsedRealtimeMs() + timeoutMs
        var resends = 0
        var answered = false
        var cancelSent = false
        var lost = !link.send(json)
        while (true) {
            if (lost) {
                // Resend the SAME request only while the PC has said nothing (§8.2): it deduplicates
                // by (runId, stepIndex), so a resend can never run the step twice.
                if (answered || resends >= MAX_RESENDS) return HostReply.TransportLost
                resends++
                if (!reconnect(exec.host)) return HostReply.TransportLost
                lost = !link.send(json)
                continue
            }
            val remaining = deadline - clock.elapsedRealtimeMs()
            if (remaining <= 0) return HostReply.Timeout
            when (val event = withTimeoutOrNull(remaining) { nextEvent(inbox, exec.control, cancelSent) } ?: return HostReply.Timeout) {
                is Event.Reply -> {
                    answered = true
                    // `in_progress` is the PC's dedup answer to a resend: keep waiting for the real one.
                    if (event.payload.outcome != RoutineStepOutcomes.IN_PROGRESS) return HostReply.Result(event.payload)
                }
                is Event.Cancel -> {
                    cancelSent = true
                    sendCancel(exec, event.kind.wireReason)
                }
                Event.Lost -> lost = true
            }
        }
    }

    private suspend fun nextEvent(
        inbox: ReceiveChannel<RoutineStepResultPayload>,
        control: RoutineRunControl,
        cancelSent: Boolean,
    ): Event =
        coroutineScope {
            val lost = async { link.authenticated.first { !it }; Event.Lost }
            val cancel = if (cancelSent) null else async { Event.Cancel(control.cancelRequest.filterNotNull().first()) }
            try {
                select {
                    inbox.onReceive { Event.Reply(it) }
                    lost.onAwait { it }
                    if (cancel != null) cancel.onAwait { it }
                }
            } finally {
                lost.cancel()
                cancel?.cancel()
            }
        }

    // ── Connection helpers ──

    private suspend fun isAuthenticatedTo(host: String): Boolean =
        link.authenticated.value && link.authenticatedHostIdentity() == host

    /** True when connected to [host], false when the attempt failed, null when cancelled. */
    private suspend fun ensureConnected(host: String, control: RoutineRunControl): Boolean? {
        if (isAuthenticatedTo(host)) return true
        return cancellable(control) { reconnect(host) }
    }

    private suspend fun reconnect(host: String): Boolean {
        if (!link.startOneShotConnect()) return false
        withTimeoutOrNull(CONNECT_WAIT_MS) { link.authenticated.first { it } }
        return isAuthenticatedTo(host)
    }

    /** `pc_unreachable_away` only while away from home and never having reached this PC from away. */
    private suspend fun unreachableCode(host: String): String =
        if (phone.isAwayFromHome() && store.reachableAwayAtUnixMs(host) == null) RoutineReasonCodes.PC_UNREACHABLE_AWAY
        else RoutineReasonCodes.PC_UNREACHABLE

    /**
     * Runs [block], abandoning it the moment a cancel is requested (null). A cancellation from
     * OUTSIDE (the budget, or Android stopping the job) is rethrown, never mistaken for the user's.
     */
    private suspend fun <T> cancellable(control: RoutineRunControl, block: suspend CoroutineScope.() -> T): T? =
        coroutineScope {
            if (control.cancelRequest.value != null) return@coroutineScope null
            val work = async(block = block)
            val watcher = launch { control.cancelRequest.first { it != null }; work.cancel() }
            try {
                work.await()
            } catch (e: CancellationException) {
                currentCoroutineContext().ensureActive()
                if (control.cancelRequest.value == null) throw e
                null
            } finally {
                watcher.cancel()
            }
        }

    // ── Finishing ──

    private suspend fun persist(exec: Execution) {
        exec.run = store.recordRun(exec.snapshot())
    }

    private suspend fun finish(exec: Execution, ending: Ending): RoutineRun {
        val now = clock.nowUnixMs()
        val code: String
        val args: RoutineReasonArgs?
        val outcome: String
        var cancelledBy: String? = null
        when (ending) {
            Ending.Succeeded -> {
                outcome = RoutineRunOutcomes.SUCCEEDED
                code = RoutineReasonCodes.OK
                args = exec.args(null)
            }
            is Ending.Failed -> {
                outcome = RoutineRunOutcomes.FAILED
                code = ending.code
                args = ending.args
            }
            is Ending.Cancelled -> {
                outcome = RoutineRunOutcomes.CANCELLED
                code = ending.code
                args = exec.args(null)
                cancelledBy = ending.cancelledBy
            }
        }
        val cancelled = ending is Ending.Cancelled
        exec.stepRecords.replaceAll { step ->
            when (step.status) {
                RoutineStepStatuses.PENDING ->
                    step.copy(status = if (cancelled) RoutineStepStatuses.CANCELLED else RoutineStepStatuses.SKIPPED)
                // Still running at the end: the budget ran out, or a cancel landed mid-step.
                RoutineStepStatuses.RUNNING ->
                    step.copy(
                        status = if (cancelled) RoutineStepStatuses.CANCELLED else RoutineStepStatuses.FAILED,
                        endedAtUnixMs = now,
                        reasonCode = code,
                    )
                else -> step
            }
        }
        exec.run = exec.run.copy(endedAtUnixMs = now, outcome = outcome, reasonCode = code, reasonArgs = args, cancelledBy = cancelledBy)
        val stored = store.recordRun(exec.snapshot())
        RoutineLog.i("Run ${RoutineLog.id(exec.runId)} of ${RoutineLog.name(exec.routine.name)} ended $outcome ($code).")
        notifyObserver { observer.onFinished(stored, exec.routine) }
        return stored
    }

    /** A run that ends before its first step: skipped, refused, cancelled while queued, or interrupted. */
    private suspend fun finishEarly(base: RoutineRun, routine: Routine?, outcome: String, code: String, cancelledBy: String?): RoutineRun {
        val now = clock.nowUnixMs()
        val pendingStatus = if (outcome == RoutineRunOutcomes.CANCELLED) RoutineStepStatuses.CANCELLED else RoutineStepStatuses.SKIPPED
        val steps =
            base.steps.orEmpty().map {
                when (it.status) {
                    RoutineStepStatuses.PENDING -> it.copy(status = pendingStatus)
                    RoutineStepStatuses.RUNNING -> it.copy(status = RoutineStepStatuses.FAILED, reasonCode = code, endedAtUnixMs = now)
                    else -> it
                }
            }
        val final =
            base.copy(
                outcome = outcome,
                reasonCode = code,
                cancelledBy = cancelledBy,
                endedAtUnixMs = now,
                steps = steps,
            )
        val stored = store.recordRun(final)
        notifyObserver { observer.onFinished(stored, routine) }
        return stored
    }

    // ── State of one execution ──

    private class Execution(
        var run: RoutineRun,
        val routine: Routine,
        val steps: List<RoutineStep?>,
        val control: RoutineRunControl,
        val host: String,
        val pcName: String?,
        val ticket: RoutineRunTicket,
    ) {
        val runId: String get() = ticket.runId
        val routineId: String get() = ticket.routineId
        val stepRecords: MutableList<RoutineRunStep> =
            run.steps.orEmpty().takeIf { it.size == steps.size }?.toMutableList()
                ?: RoutineRunRecords.pendingSteps(steps).toMutableList()
        val attributes: LinkedHashSet<String> = LinkedHashSet(run.attributes.orEmpty())
        var countdown: RoutineRunCountdown? = run.countdown
        var wakeSent = false
        var currentIndex = -1

        /** The host step whose `routine_step_request` is out and unanswered, else null. */
        var inFlightStep: Int? = null
        var cancelSentForStep: Int? = null

        fun args(step: RoutineStep?): RoutineReasonArgs =
            RoutineReasonArgs(pc = pcName, action = RoutineActionTokens.of(step), app = step?.appLabel)

        fun cancelledOutcome(): StepOutcome {
            val kind = control.cancelRequest.value ?: RoutineCancelKind.USER
            return StepOutcome.Cancelled(kind.reasonCode, kind.cancelledBy)
        }

        fun markStep(index: Int, status: String, code: String?, args: RoutineReasonArgs?, nowUnixMs: Long, started: Boolean = false) {
            val current = stepRecords[index]
            stepRecords[index] =
                if (started) {
                    current.copy(status = status, startedAtUnixMs = nowUnixMs)
                } else {
                    current.copy(status = status, endedAtUnixMs = nowUnixMs, reasonCode = code, reasonArgs = args)
                }
        }

        fun snapshot(): RoutineRun = run.copy(steps = stepRecords.toList(), attributes = attributes.toList(), countdown = countdown)
    }

    private sealed interface Ending {
        data object Succeeded : Ending

        data class Failed(val code: String, val args: RoutineReasonArgs?) : Ending

        data class Cancelled(val code: String, val cancelledBy: String) : Ending
    }

    private sealed interface StepOutcome {
        data object Succeeded : StepOutcome

        data object Simulated : StepOutcome

        /** Not carried out, and the run goes on (a PC countdown conflict). */
        data class Skipped(val code: String) : StepOutcome

        data class Failed(val code: String, val args: RoutineReasonArgs?) : StepOutcome

        data class Cancelled(val code: String, val cancelledBy: String) : StepOutcome
    }

    private sealed interface HostReply {
        data class Result(val payload: RoutineStepResultPayload) : HostReply

        data object Timeout : HostReply

        data object TransportLost : HostReply
    }

    private sealed interface Event {
        data class Reply(val payload: RoutineStepResultPayload) : Event

        data class Cancel(val kind: RoutineCancelKind) : Event

        data object Lost : Event
    }

    companion object {
        /** §8.2: a hard stop inside Android's 10-minute job window. */
        const val BUDGET_MS = RoutineLimits.MAX_PHONE_RUN_BUDGET_SECONDS * 1000L
        const val DEFERRED_THRESHOLD_MS = 60_000L
        const val WAKE_ATTEMPTS = 3
        const val WAKE_TIMEOUT_MS = 5_000L
        const val WAKE_RETRY_GAP_MS = 1_000L
        const val CONNECT_WAIT_MS = 10_000L
        const val RETRY_PAUSE_MS = 2_000L
        const val HOST_STEP_TIMEOUT_MS = 30_000L
        const val MAX_RESENDS = 2

        private val CANCEL_CODES =
            setOf(
                RoutineReasonCodes.CANCELLED_ON_PC,
                RoutineReasonCodes.CANCELLED_ON_PHONE,
                RoutineReasonCodes.PAUSED_ON_PC,
                RoutineReasonCodes.PAUSED_ON_PHONE,
            )
    }
}
