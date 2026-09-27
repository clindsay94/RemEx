package com.clindsay94.remex.ui.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineNotifyTargets
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The editor's rules (routines spec 1.8; R-UX-10 StepAvailabilityTest, R-UX-11 trigger change,
 * R-UX-55 RoutineValidatorMessagesTest; RemEx-pp0rt.6). One test per rule the S1 editor can meet.
 */
class RoutineEditorRulesTest {
    private val host = "9f2c4be07a1d33e5"
    private val mac = "0A:1B:2C:3D:4E:5F"
    private val app = "3f2504e0-4f89-41d3-9a0c-0305e82c3301"

    private fun draft(vararg steps: RoutineStep, trigger: String? = RoutineTriggerTypes.MANUAL, pc: String? = host): RoutineDraft =
        RoutineDraft(
            base = null,
            name = "",
            hostIdentity = pc,
            enabled = true,
            trigger = trigger?.let { RoutineTrigger(type = it) },
            steps = steps.mapIndexed { i, s -> DraftStep(i + 1L, s) },
        )

    private fun codes(d: RoutineDraft, env: EditorEnvironment = EditorEnvironment()) = RoutineEditorRules.problems(d, env).map { it.code }

    private val lock = RoutineStep(type = RoutineStepTypes.POWER, verb = "LOCK")
    private val shutdown = RoutineStep(type = RoutineStepTypes.POWER, verb = "SHUTDOWN")
    private val sleep = RoutineStep(type = RoutineStepTypes.POWER, verb = "SLEEP")
    private val wake = RoutineStep(type = RoutineStepTypes.WAKE, mac = mac)
    private val waitOnline = RoutineStep(type = RoutineStepTypes.WAIT_ONLINE, timeoutSeconds = 60)
    private val notifyPhone = RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PHONE, title = "Hi")

    // ── R-UX-10: availability ──

    @Test
    fun `wake and waitOnline are phone-only on a routine that runs on the PC`() {
        assertEquals(StepAvailability.PHONE_ONLY, RoutineEditorRules.availability(RoutineStepTypes.WAKE, RoutineTriggerTypes.PC_IDLE, emptyList()))
        assertEquals(StepAvailability.PHONE_ONLY, RoutineEditorRules.availability(RoutineStepTypes.WAIT_ONLINE, RoutineTriggerTypes.PC_SESSION, emptyList()))
        assertEquals(StepAvailability.AVAILABLE, RoutineEditorRules.availability(RoutineStepTypes.POWER, RoutineTriggerTypes.PC_IDLE, emptyList()))
        assertEquals(StepAvailability.AVAILABLE, RoutineEditorRules.availability(RoutineStepTypes.WAKE, RoutineTriggerTypes.MANUAL, emptyList()))
    }

    @Test
    fun `per-kind caps close the tile once reached`() {
        assertEquals(StepAvailability.LIMIT_REACHED, RoutineEditorRules.availability(RoutineStepTypes.WAKE, RoutineTriggerTypes.MANUAL, listOf(RoutineStepTypes.WAKE)))
        assertEquals(StepAvailability.AVAILABLE, RoutineEditorRules.availability(RoutineStepTypes.WAIT_ONLINE, RoutineTriggerTypes.MANUAL, listOf(RoutineStepTypes.WAIT_ONLINE)))
        assertEquals(
            StepAvailability.LIMIT_REACHED,
            RoutineEditorRules.availability(RoutineStepTypes.NOTIFY, RoutineTriggerTypes.MANUAL, List(3) { RoutineStepTypes.NOTIFY }),
        )
        assertEquals(StepAvailability.AVAILABLE, RoutineEditorRules.availability(RoutineStepTypes.DELAY, RoutineTriggerTypes.MANUAL, List(11) { RoutineStepTypes.DELAY }))
    }

    @Test
    fun `the Runs on side follows the trigger`() {
        assertFalse(draft(lock).runsOnPc)
        assertTrue(draft(lock, trigger = RoutineTriggerTypes.PC_IDLE).runsOnPc)
    }

    @Test
    fun `step 13 cannot be added`() {
        val full = draft(*Array(12) { lock })
        assertFalse(full.canAddStep)
        assertEquals(12, full.addStep(lock).steps.size)
        assertEquals(12, full.duplicateStep(0).steps.size)
    }

    // ── R-UX-11: a trigger change that invalidates steps asks first ──

    @Test
    fun `switching to a PC trigger names the phone-only steps it would remove`() {
        val steps = listOf(wake, waitOnline, lock)
        assertEquals(listOf(0, 1), RoutineEditorRules.stepsInvalidatedBy(RoutineTriggerTypes.PC_IDLE, steps))
        assertTrue(RoutineEditorRules.stepsInvalidatedBy(RoutineTriggerTypes.MANUAL, steps).isEmpty())
    }

    // ── R-UX-55: every rule produces its message; warnings never block ──

    @Test
    fun `an empty draft needs a trigger and a step`() {
        val c = codes(draft(trigger = null))
        assertTrue(EditorProblemCode.NO_TRIGGER in c)
        assertTrue(EditorProblemCode.NO_STEPS in c)
    }

    @Test
    fun `no paired PC is an error on the PC chip`() {
        val problems = RoutineEditorRules.problems(draft(lock, pc = null), EditorEnvironment())
        assertEquals(ProblemTarget.Pc, problems.single { it.code == EditorProblemCode.NO_PC }.target)
    }

    @Test
    fun `wake without a MAC, launch without an app and notify without a title are errors on their step`() {
        val problems =
            RoutineEditorRules.problems(
                draft(
                    RoutineStep(type = RoutineStepTypes.WAKE),
                    RoutineStep(type = RoutineStepTypes.LAUNCH_APP),
                    RoutineStep(type = RoutineStepTypes.NOTIFY, target = RoutineNotifyTargets.PC, title = " "),
                ),
                EditorEnvironment(),
            )
        assertEquals(ProblemTarget.Step(0), problems.single { it.code == EditorProblemCode.WAKE_NO_MAC }.target)
        assertEquals(ProblemTarget.Step(1), problems.single { it.code == EditorProblemCode.CHOOSE_APP }.target)
        assertEquals(ProblemTarget.Step(2), problems.single { it.code == EditorProblemCode.NOTIFY_NO_TITLE }.target)
        assertTrue(problems.all { it.kind == ProblemKind.ERROR })
    }

    @Test
    fun `an app missing from a known launcher list is named, an unknown list says nothing`() {
        val step = RoutineStep(type = RoutineStepTypes.LAUNCH_APP, appId = app, appLabel = "Steam")
        val known = RoutineEditorRules.problems(draft(step), EditorEnvironment(launcherAppIds = setOf("00000000-0000-4000-8000-000000000000")))
        assertEquals("Steam", known.single { it.code == EditorProblemCode.APP_MISSING }.app)
        assertTrue(codes(draft(step)).none { it == EditorProblemCode.APP_MISSING })
        assertTrue(codes(draft(step), EditorEnvironment(launcherAppIds = setOf(app.uppercase()))).none { it == EditorProblemCode.APP_MISSING })
    }

    @Test
    fun `a phone-only step on a PC routine says so`() {
        assertTrue(EditorProblemCode.PHONE_ONLY in codes(draft(wake, trigger = RoutineTriggerTypes.PC_IDLE)))
    }

    @Test
    fun `a second wake is refused on the extra step`() {
        val problems = RoutineEditorRules.problems(draft(wake, wake), EditorEnvironment())
        assertEquals(ProblemTarget.Step(1), problems.single { it.code == EditorProblemCode.TOO_MANY_OF_KIND }.target)
    }

    @Test
    fun `a destructive step shows the countdown as info and a second one is an error`() {
        val one = RoutineEditorRules.problems(draft(shutdown), EditorEnvironment())
        assertEquals(ProblemKind.INFO, one.single { it.code == EditorProblemCode.COUNTDOWN }.kind)
        assertTrue(RoutineEditorRules.errors(one).isEmpty())

        val two = RoutineEditorRules.problems(draft(sleep, shutdown), EditorEnvironment())
        assertEquals(ProblemTarget.Step(1), two.single { it.code == EditorProblemCode.TOO_MANY_DESTRUCTIVE }.target)
    }

    @Test
    fun `on a PC routine the step after the power-off step is the one that cannot run`() {
        // The message reads "the step BEFORE it turns off the PC", so it sits on the step after.
        val problems = RoutineEditorRules.problems(draft(sleep, lock, trigger = RoutineTriggerTypes.PC_IDLE), EditorEnvironment())
        assertEquals(ProblemTarget.Step(1), problems.single { it.code == EditorProblemCode.DESTRUCTIVE_NOT_LAST }.target)
        assertEquals(ProblemTarget.Step(0), problems.single { it.code == EditorProblemCode.COUNTDOWN }.target)
        assertTrue(codes(draft(lock, sleep, trigger = RoutineTriggerTypes.PC_IDLE)).none { it == EditorProblemCode.DESTRUCTIVE_NOT_LAST })
    }

    // ── Media keys (spec 4.2) ──

    private val media = RoutineStep(type = RoutineStepTypes.MEDIA, mediaAction = "next")

    @Test
    fun `a media step warns only when the PC definitely refuses key presses`() {
        val refused = RoutineEditorRules.problems(draft(media), EditorEnvironment(mediaKeysSupported = false))
        val warning = refused.single { it.code == EditorProblemCode.MEDIA_KEYS }
        assertEquals(ProblemKind.WARNING, warning.kind)
        assertEquals(ProblemTarget.Step(0), warning.target)
        assertTrue(RoutineEditorRules.errors(refused).isEmpty())
        assertTrue(codes(draft(media), EditorEnvironment(mediaKeysSupported = true)).none { it == EditorProblemCode.MEDIA_KEYS })
        assertTrue(codes(draft(media), EditorEnvironment(mediaKeysSupported = null)).none { it == EditorProblemCode.MEDIA_KEYS })
    }

    @Test
    fun `host_info answers media keys only for the live connection, absent means yes`() {
        assertEquals(false, RoutineMediaKeys.supported("""{"supportsInputSimulation":false}""", sameConnection = true))
        assertEquals(true, RoutineMediaKeys.supported("""{"supportsInputSimulation":true}""", sameConnection = true))
        assertEquals(true, RoutineMediaKeys.supported("""{"hostName":"x"}""", sameConnection = true))
        assertEquals(null, RoutineMediaKeys.supported("""{"supportsInputSimulation":false}""", sameConnection = false))
        assertEquals(null, RoutineMediaKeys.supported(null, sameConnection = true))
        assertEquals(null, RoutineMediaKeys.supported("not json", sameConnection = true))
    }

    // ── R-UX-58: switching drafts asks first ──

    @Test
    fun `switching away from unsaved edits asks, and only then`() {
        val original = draft(lock)
        val edited = original.copy(name = "Changed")
        assertTrue(RoutineEditorSwitch.needsDiscardPrompt(edited, original, sameSource = false))
        assertFalse(RoutineEditorSwitch.needsDiscardPrompt(edited, original, sameSource = true))
        assertFalse(RoutineEditorSwitch.needsDiscardPrompt(original, original, sameSource = false))
        assertFalse(RoutineEditorSwitch.needsDiscardPrompt(null, null, sameSource = false))
        // A reorder is an edit too.
        val reordered = draft(lock, wake).moveStep(1, 0)
        assertTrue(RoutineEditorSwitch.needsDiscardPrompt(reordered, draft(lock, wake), sameSource = false))
    }

    @Test
    fun `a PC step after power-off on a phone routine warns without blocking, and waitOnline clears it`() {
        val warned = RoutineEditorRules.problems(draft(shutdown, lock), EditorEnvironment())
        val warning = warned.single { it.code == EditorProblemCode.AFTER_POWER_OFF }
        assertEquals(ProblemKind.WARNING, warning.kind)
        assertEquals(ProblemTarget.Step(1), warning.target)
        assertTrue(RoutineEditorRules.errors(warned).isEmpty())

        assertTrue(codes(draft(shutdown, wake, waitOnline, lock)).none { it == EditorProblemCode.AFTER_POWER_OFF })
        // A phone-side step after power-off is fine: it does not need the PC.
        assertTrue(codes(draft(shutdown, notifyPhone)).none { it == EditorProblemCode.AFTER_POWER_OFF })
    }

    @Test
    fun `a phone routine over the nine-minute budget is refused`() {
        val long = draft(*Array(2) { RoutineStep(type = RoutineStepTypes.DELAY, seconds = 300) })
        assertTrue(EditorProblemCode.BUDGET_EXCEEDED in codes(long))
        assertTrue(codes(draft(RoutineStep(type = RoutineStepTypes.DELAY, seconds = 300))).none { it == EditorProblemCode.BUDGET_EXCEEDED })
    }

    @Test
    fun `a valid manual routine has no errors`() {
        val valid = draft(wake, waitOnline, RoutineStep(type = RoutineStepTypes.LAUNCH_APP, appId = app, appLabel = "Steam"), notifyPhone)
        assertTrue(RoutineEditorRules.errors(RoutineEditorRules.problems(valid, EditorEnvironment(launcherAppIds = setOf(app)))).isEmpty())
    }

    // ── Name ──

    @Test
    fun `a blank name becomes the auto-name from the trigger and first step, capped at 40`() {
        val pair = { a: String, b: String -> "$a, $b" }
        assertEquals("Tap Run, Wake", RoutineEditorRules.autoName("Tap Run", "Wake", pair, "New routine"))
        assertEquals("Tap Run", RoutineEditorRules.autoName("Tap Run", null, pair, "New routine"))
        assertEquals("New routine", RoutineEditorRules.autoName(null, null, pair, "New routine"))
        assertEquals(40, RoutineEditorRules.autoName("x".repeat(30), "y".repeat(30), pair, "").length)

        val routine = draft(lock).toRoutine("Tap Run, Lock")
        assertEquals("Tap Run, Lock", routine.name)
        assertEquals("Mine", draft(lock).copy(name = "  Mine ").toRoutine("ignored").name)
    }

    @Test
    fun `reordering keeps each step's key so its card keeps its state`() {
        val d = draft(wake, waitOnline, lock)
        val moved = d.moveStep(2, 0)
        assertEquals(listOf(3L, 1L, 2L), moved.steps.map { it.key })
        assertEquals(listOf(RoutineStepTypes.POWER, RoutineStepTypes.WAKE, RoutineStepTypes.WAIT_ONLINE), moved.steps.map { it.step.type })
        assertTrue(d.moveStep(0, 5) === d)
    }

    @Test
    fun `editing keeps id, revision and timestamps from the stored routine`() {
        val stored = Routine(id = "a", name = "Game", hostIdentity = host, revision = 4, createdAtUnixMs = 10, updatedAtUnixMs = 20, trigger = RoutineTrigger(type = "manual"), steps = listOf(lock))
        val out = RoutineDrafts.fromRoutine(stored).copy(name = "Games").toRoutine("x")
        assertEquals("a", out.id)
        assertEquals(4, out.revision)
        assertEquals(10, out.createdAtUnixMs)
        assertEquals("Games", out.name)
        assertFalse(RoutineDrafts.fromRoutine(stored).isNew)
    }
}
