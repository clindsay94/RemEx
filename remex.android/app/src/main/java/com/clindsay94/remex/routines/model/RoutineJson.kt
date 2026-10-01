package com.clindsay94.remex.routines.model

import java.math.BigDecimal
import java.math.BigInteger
import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener

/**
 * Reads and writes the routine JSON shape (routines spec §6.6, §7.3, RemEx-pp0rt.3), matching what
 * `RemexJsonSerializerContext` reads and writes on the C# side.
 *
 * **READING IS STRICT ABOUT JSON TYPES AND NEVER THROWS, LIKE THE C# SIDE.** System.Text.Json throws
 * on a value of the wrong JSON type, and the C# lenient converters turn that into a malformed
 * routine (or a null payload) rather than a dropped session. This reader reaches the same verdict
 * the same way: a routine with any wrong-typed field anywhere inside it becomes
 * `Routine(id = <id if readable>, isMalformed = true)`, which [RoutineValidator] rejects with
 * `invalid_field`. `optInt`-style coercion ("5" -> 5, 5.5 -> 5) is exactly what must NOT happen here,
 * because C# would refuse the same document and the phone and the PC would then disagree about
 * whether a routine is valid. The `invalid.invalid_field` fixtures pin it.
 *
 * The rules, per C# field type: `string` takes only a JSON string; `int` only an integer that fits;
 * `long` only an integer that fits; `double` any number; `bool` only true/false. A JSON null is
 * "absent" for a nullable field and malformed for a non-nullable one. Unknown keys are ignored.
 *
 * **WRITING OMITS NULLS AND ALWAYS WRITES NON-NULLABLE PRIMITIVES**, the C# `WhenWritingNull` rule, so
 * the two sides emit the same JSON tree (`wire.*.json` fixtures).
 */
object RoutineJson {

    // ── Reading ──

    /** A whole document, or null when the document itself (not a routine in it) is unreadable. */
    fun parseSet(json: String?): RoutineSet? =
        runCatching { JSONObject(json ?: return null) }.getOrNull()?.let(::readSet)

    fun readSet(obj: JSONObject): RoutineSet? {
        val r = Reader(obj)
        val set = RoutineSet(schemaVersion = r.int("schemaVersion"), routines = readRoutineList(obj, "routines"))
        return if (r.flag.malformed) null else set
    }

    /**
     * A routine list, element by element: an unreadable element becomes a malformed placeholder in
     * its own position (C# `LenientRoutineListConverter`). A value that is not an array reads as null.
     */
    fun readRoutineList(obj: JSONObject, key: String): List<Routine>? {
        if (!obj.has(key) || obj.isNull(key)) return null
        val array = obj.opt(key) as? JSONArray ?: return null
        return (0 until array.length()).map { readRoutine(array.opt(it)) }
    }

    /**
     * One routine; never throws. A malformed routine keeps its original JSON text in
     * [Routine.rawJson] so writing it back reproduces what arrived rather than an empty shell.
     */
    fun readRoutine(value: Any?): Routine {
        val obj = value as? JSONObject ?: return Routine(isMalformed = true, rawJson = rawText(value))
        val flag = Flag()
        val r = Reader(obj, flag)
        val routine =
            Routine(
                id = r.string("id"),
                name = r.string("name"),
                hostIdentity = r.string("hostIdentity"),
                enabled = r.bool("enabled"),
                revision = r.long("revision"),
                appearance = r.obj("appearance")?.let { readAppearance(it, flag) },
                trigger = r.obj("trigger")?.let { readTrigger(it, flag) },
                steps = r.array("steps")?.let { readStepList(it, flag) },
                createdAtUnixMs = r.long("createdAtUnixMs"),
                updatedAtUnixMs = r.long("updatedAtUnixMs"),
            )
        return if (flag.malformed) {
            Routine(id = obj.opt("id") as? String, isMalformed = true, rawJson = obj.toString())
        } else {
            routine
        }
    }

    /** Any JSON value as text: a one-element array serialises it with org.json's own rules. */
    private fun rawText(value: Any?): String =
        JSONArray().put(value ?: JSONObject.NULL).toString().removePrefix("[").removeSuffix("]")

    /** A single step outside a routine (`routine_step_request.step`); never throws. */
    fun readStep(value: Any?): RoutineStep {
        val obj = value as? JSONObject ?: return RoutineStep(isMalformed = true)
        val flag = Flag()
        val step = readStepObject(obj, flag)
        return if (flag.malformed) RoutineStep(type = obj.opt("type") as? String, isMalformed = true) else step
    }

    private fun readAppearance(obj: JSONObject, flag: Flag): RoutineAppearance {
        val r = Reader(obj, flag)
        return RoutineAppearance(icon = r.string("icon"), color = r.string("color"))
    }

    private fun readTrigger(obj: JSONObject, flag: Flag): RoutineTrigger {
        val r = Reader(obj, flag)
        return RoutineTrigger(
            type = r.string("type"),
            homeId = r.string("homeId"),
            leaveDebounceSeconds = r.intOrNull("leaveDebounceSeconds"),
            sensorId = r.string("sensorId"),
            sensorLabel = r.string("sensorLabel"),
            direction = r.string("direction"),
            threshold = r.doubleOrNull("threshold"),
            sustainSeconds = r.intOrNull("sustainSeconds"),
            idleMinutes = r.intOrNull("idleMinutes"),
            ignoreWhileMediaPlaying = r.boolOrNull("ignoreWhileMediaPlaying"),
            sessionState = r.string("sessionState"),
        )
    }

    private fun readStepList(array: JSONArray, flag: Flag): List<RoutineStep?> =
        (0 until array.length()).map { i ->
            if (array.isNull(i)) {
                null
            } else {
                val element = array.opt(i)
                if (element is JSONObject) readStepObject(element, flag) else null.also { flag.malformed = true }
            }
        }

    private fun readStepObject(obj: JSONObject, flag: Flag): RoutineStep {
        val r = Reader(obj, flag)
        return RoutineStep(
            type = r.string("type"),
            mac = r.string("mac"),
            broadcastIp = r.string("broadcastIp"),
            port = r.intOrNull("port"),
            timeoutSeconds = r.intOrNull("timeoutSeconds"),
            seconds = r.intOrNull("seconds"),
            verb = r.string("verb"),
            delaySeconds = r.intOrNull("delaySeconds"),
            appId = r.string("appId"),
            appLabel = r.string("appLabel"),
            mediaAction = r.string("mediaAction"),
            target = r.string("target"),
            title = r.string("title"),
            body = r.string("body"),
        )
    }

    /** One run record; a record with a wrong-typed field reads as null. */
    fun readRun(value: Any?): RoutineRun? {
        val obj = value as? JSONObject ?: return null
        val flag = Flag()
        val r = Reader(obj, flag)
        val run =
            RoutineRun(
                runId = r.string("runId"),
                seq = r.longOrNull("seq"),
                ownerClientId = r.string("ownerClientId"),
                routineId = r.string("routineId"),
                routineName = r.string("routineName"),
                routineRevision = r.long("routineRevision"),
                origin = r.string("origin"),
                hostIdentity = r.string("hostIdentity"),
                source = r.string("source"),
                testRun = r.bool("testRun"),
                sourceDetail = r.obj("sourceDetail")?.let { readSourceDetail(it, flag) },
                triggeredAtUnixMs = r.long("triggeredAtUnixMs"),
                startedAtUnixMs = r.long("startedAtUnixMs"),
                endedAtUnixMs = r.longOrNull("endedAtUnixMs"),
                outcome = r.string("outcome"),
                reasonCode = r.string("reasonCode"),
                reasonArgs = r.obj("reasonArgs")?.let { readReasonArgs(it, flag) },
                cancelledBy = r.string("cancelledBy"),
                attributes = r.stringList("attributes"),
                steps = r.objList("steps") { readRunStep(it, flag) },
                countdown = r.obj("countdown")?.let { readCountdown(it, flag) },
            )
        return if (flag.malformed) null else run
    }

    private fun readSourceDetail(obj: JSONObject, flag: Flag): RoutineRunSourceDetail {
        val r = Reader(obj, flag)
        return RoutineRunSourceDetail(
            sensorName = r.string("sensorName"),
            value = r.doubleOrNull("value"),
            unit = r.string("unit"),
            idleMinutes = r.intOrNull("idleMinutes"),
            sessionState = r.string("sessionState"),
            homeLabel = r.string("homeLabel"),
        )
    }

    private fun readReasonArgs(obj: JSONObject, flag: Flag): RoutineReasonArgs {
        val r = Reader(obj, flag)
        return RoutineReasonArgs(
            pc = r.string("pc"),
            phone = r.string("phone"),
            app = r.string("app"),
            sensor = r.string("sensor"),
            duration = r.string("duration"),
            action = r.string("action"),
            detail = r.string("detail"),
            routine = r.string("routine"),
            date = r.string("date"),
            n = r.string("n"),
        )
    }

    private fun readRunStep(obj: JSONObject, flag: Flag): RoutineRunStep {
        val r = Reader(obj, flag)
        return RoutineRunStep(
            index = r.int("index"),
            kind = r.string("kind"),
            status = r.string("status"),
            startedAtUnixMs = r.longOrNull("startedAtUnixMs"),
            endedAtUnixMs = r.longOrNull("endedAtUnixMs"),
            reasonCode = r.string("reasonCode"),
            reasonArgs = r.obj("reasonArgs")?.let { readReasonArgs(it, flag) },
        )
    }

    private fun readCountdown(obj: JSONObject, flag: Flag): RoutineRunCountdown {
        val r = Reader(obj, flag)
        return RoutineRunCountdown(
            shown = r.bool("shown"),
            startedAtUnixMs = r.long("startedAtUnixMs"),
            cancelledBy = r.string("cancelledBy"),
        )
    }

    // ── Payloads. Each returns null when the payload is unreadable (C# LenientRoutinePayloadConverter). ──

    fun readRoutinesSync(value: Any?): RoutinesSyncPayload? = readPayload(value) { obj, r ->
        RoutinesSyncPayload(
            schemaVersion = r.int("schemaVersion"),
            revision = r.long("revision"),
            paused = r.bool("paused"),
            routines = readRoutineList(obj, "routines"),
            runCursor = r.long("runCursor"),
            forget = r.bool("forget"),
            sentAtUnixMs = r.long("sentAtUnixMs"),
        )
    }

    fun readSyncResult(value: Any?): RoutineSyncResultPayload? = readPayload(value) { _, r ->
        RoutineSyncResultPayload(
            revision = r.long("revision"),
            storedRevision = r.long("storedRevision"),
            status = r.string("status"),
            results = r.objList("results") { item ->
                val ir = Reader(item, r.flag)
                RoutineSyncItemResult(
                    routineId = ir.string("routineId"),
                    accepted = ir.bool("accepted"),
                    reasonCode = ir.string("reasonCode"),
                    detail = ir.string("detail"),
                )
            },
            hostPaused = r.bool("hostPaused"),
            ownerPaused = r.bool("ownerPaused"),
            unsolicited = r.bool("unsolicited"),
            pcDisabled = r.stringList("pcDisabled"),
            ownerSuspended = r.string("ownerSuspended"),
            idleSource = r.string("idleSource"),
            sessionSource = r.string("sessionSource"),
            sensorTrigger = r.bool("sensorTrigger"),
        )
    }

    fun readStepRequest(value: Any?): RoutineStepRequestPayload? = readPayload(value) { obj, r ->
        RoutineStepRequestPayload(
            runId = r.string("runId"),
            routineId = r.string("routineId"),
            routineName = r.string("routineName"),
            triggerType = r.string("triggerType"),
            stepIndex = r.int("stepIndex"),
            step = if (obj.has("step") && !obj.isNull("step")) readStep(obj.opt("step")) else null,
            testRun = r.bool("testRun"),
            source = r.string("source"),
        )
    }

    fun readStepResult(value: Any?): RoutineStepResultPayload? = readPayload(value) { _, r ->
        RoutineStepResultPayload(
            runId = r.string("runId"),
            stepIndex = r.int("stepIndex"),
            outcome = r.string("outcome"),
            reasonCode = r.string("reasonCode"),
            countdownShown = r.bool("countdownShown"),
            cancelledBy = r.string("cancelledBy"),
            detail = r.string("detail"),
        )
    }

    fun readNotify(value: Any?): RoutineNotifyPayload? = readPayload(value) { _, r ->
        RoutineNotifyPayload(
            notifyId = r.string("notifyId"),
            kind = r.string("kind"),
            routineId = r.string("routineId"),
            routineName = r.string("routineName"),
            runId = r.string("runId"),
            title = r.string("title"),
            body = r.string("body"),
            countdownEndsAtUnixMs = r.longOrNull("countdownEndsAtUnixMs"),
            queuedAtUnixMs = r.long("queuedAtUnixMs"),
            expiresAtUnixMs = r.long("expiresAtUnixMs"),
        )
    }

    fun readNotifyAck(value: Any?): RoutineNotifyAckPayload? = readPayload(value) { _, r ->
        RoutineNotifyAckPayload(notifyIds = r.stringList("notifyIds"))
    }

    fun readRunReport(value: Any?): RoutineRunReportPayload? = readPayload(value) { _, r ->
        RoutineRunReportPayload(
            runs = r.objList("runs") { run -> readRun(run) ?: RoutineRun().also { r.flag.malformed = true } },
            more = r.bool("more"),
            live = r.bool("live"),
        )
    }

    fun readCancel(value: Any?): RoutineCancelPayload? = readPayload(value) { _, r ->
        RoutineCancelPayload(runId = r.string("runId"), reason = r.string("reason"))
    }

    fun readRunRequest(value: Any?): RoutineRunRequestPayload? = readPayload(value) { _, r ->
        RoutineRunRequestPayload(
            runId = r.string("runId"),
            routineId = r.string("routineId"),
            testRun = r.bool("testRun"),
            source = r.string("source"),
        )
    }

    private inline fun <T> readPayload(value: Any?, build: (JSONObject, Reader) -> T): T? {
        val obj = value as? JSONObject ?: return null
        val r = Reader(obj)
        val payload = runCatching { build(obj, r) }.getOrNull() ?: return null
        return if (r.flag.malformed) null else payload
    }

    // ── Writing ──

    fun write(set: RoutineSet): JSONObject =
        JSONObject().put("schemaVersion", set.schemaVersion).putList("routines", set.routines, ::writeListElement)

    /**
     * A routine as a list element. A malformed one is written as the JSON it arrived as, which may
     * not be an object at all (a stray `17` in the list round-trips as `17`). Mirrors the C#
     * `LenientRoutineListConverter.Write`.
     */
    private fun writeListElement(routine: Routine): Any = if (routine.isMalformed) rawValue(routine) else write(routine)

    private fun rawValue(routine: Routine): Any {
        val raw =
            checkNotNull(routine.rawJson) {
                "Refusing to write malformed routine '${routine.id}' with no original JSON: it would replace " +
                    "the user's routine with an empty placeholder."
            }
        return JSONTokener(raw).nextValue()
    }

    /**
     * One routine. A malformed routine is NEVER written as its lossy placeholder: its original object
     * is returned unchanged, and one with no original object throws rather than persist silently.
     */
    fun write(routine: Routine): JSONObject =
        if (routine.isMalformed) {
            rawValue(routine) as? JSONObject
                ?: error("Malformed routine '${routine.id}' was not a JSON object; write it through its list.")
        } else {
            writeWellFormed(routine)
        }

    private fun writeWellFormed(routine: Routine): JSONObject =
        JSONObject()
            .putOpt("id", routine.id)
            .putOpt("name", routine.name)
            .putOpt("hostIdentity", routine.hostIdentity)
            .put("enabled", routine.enabled)
            .put("revision", routine.revision)
            .putOpt("appearance", routine.appearance?.let { JSONObject().putOpt("icon", it.icon).putOpt("color", it.color) })
            .putOpt("trigger", routine.trigger?.let(::write))
            .putList("steps", routine.steps) { step -> step?.let(::write) }
            .put("createdAtUnixMs", routine.createdAtUnixMs)
            .put("updatedAtUnixMs", routine.updatedAtUnixMs)

    fun write(trigger: RoutineTrigger): JSONObject =
        JSONObject()
            .putOpt("type", trigger.type)
            .putOpt("homeId", trigger.homeId)
            .putOpt("leaveDebounceSeconds", trigger.leaveDebounceSeconds)
            .putOpt("sensorId", trigger.sensorId)
            .putOpt("sensorLabel", trigger.sensorLabel)
            .putOpt("direction", trigger.direction)
            .putOpt("threshold", trigger.threshold)
            .putOpt("sustainSeconds", trigger.sustainSeconds)
            .putOpt("idleMinutes", trigger.idleMinutes)
            .putOpt("ignoreWhileMediaPlaying", trigger.ignoreWhileMediaPlaying)
            .putOpt("sessionState", trigger.sessionState)

    fun write(step: RoutineStep): JSONObject =
        JSONObject()
            .putOpt("type", step.type)
            .putOpt("mac", step.mac)
            .putOpt("broadcastIp", step.broadcastIp)
            .putOpt("port", step.port)
            .putOpt("timeoutSeconds", step.timeoutSeconds)
            .putOpt("seconds", step.seconds)
            .putOpt("verb", step.verb)
            .putOpt("delaySeconds", step.delaySeconds)
            .putOpt("appId", step.appId)
            .putOpt("appLabel", step.appLabel)
            .putOpt("mediaAction", step.mediaAction)
            .putOpt("target", step.target)
            .putOpt("title", step.title)
            .putOpt("body", step.body)

    fun write(run: RoutineRun): JSONObject =
        JSONObject()
            .putOpt("runId", run.runId)
            .putOpt("seq", run.seq)
            .putOpt("ownerClientId", run.ownerClientId)
            .putOpt("routineId", run.routineId)
            .putOpt("routineName", run.routineName)
            .put("routineRevision", run.routineRevision)
            .putOpt("origin", run.origin)
            .putOpt("hostIdentity", run.hostIdentity)
            .putOpt("source", run.source)
            .put("testRun", run.testRun)
            .putOpt("sourceDetail", run.sourceDetail?.let {
                JSONObject()
                    .putOpt("sensorName", it.sensorName)
                    .putOpt("value", it.value)
                    .putOpt("unit", it.unit)
                    .putOpt("idleMinutes", it.idleMinutes)
                    .putOpt("sessionState", it.sessionState)
                    .putOpt("homeLabel", it.homeLabel)
            })
            .put("triggeredAtUnixMs", run.triggeredAtUnixMs)
            .put("startedAtUnixMs", run.startedAtUnixMs)
            .putOpt("endedAtUnixMs", run.endedAtUnixMs)
            .putOpt("outcome", run.outcome)
            .putOpt("reasonCode", run.reasonCode)
            .putOpt("reasonArgs", run.reasonArgs?.let(::write))
            .putOpt("cancelledBy", run.cancelledBy)
            .putOpt("attributes", run.attributes?.let { JSONArray(it) })
            .putList("steps", run.steps) { step ->
                JSONObject()
                    .put("index", step.index)
                    .putOpt("kind", step.kind)
                    .putOpt("status", step.status)
                    .putOpt("startedAtUnixMs", step.startedAtUnixMs)
                    .putOpt("endedAtUnixMs", step.endedAtUnixMs)
                    .putOpt("reasonCode", step.reasonCode)
                    .putOpt("reasonArgs", step.reasonArgs?.let(::write))
            }
            .putOpt("countdown", run.countdown?.let {
                JSONObject()
                    .put("shown", it.shown)
                    .put("startedAtUnixMs", it.startedAtUnixMs)
                    .putOpt("cancelledBy", it.cancelledBy)
            })

    fun write(args: RoutineReasonArgs): JSONObject =
        JSONObject()
            .putOpt("pc", args.pc)
            .putOpt("phone", args.phone)
            .putOpt("app", args.app)
            .putOpt("sensor", args.sensor)
            .putOpt("duration", args.duration)
            .putOpt("action", args.action)
            .putOpt("detail", args.detail)
            .putOpt("routine", args.routine)
            .putOpt("date", args.date)
            .putOpt("n", args.n)

    fun write(p: RoutinesSyncPayload): JSONObject =
        JSONObject()
            .put("schemaVersion", p.schemaVersion)
            .put("revision", p.revision)
            .put("paused", p.paused)
            .putList("routines", p.routines, ::writeListElement)
            .put("runCursor", p.runCursor)
            .put("forget", p.forget)
            .put("sentAtUnixMs", p.sentAtUnixMs)

    fun write(p: RoutineSyncResultPayload): JSONObject =
        JSONObject()
            .put("revision", p.revision)
            .put("storedRevision", p.storedRevision)
            .putOpt("status", p.status)
            .putList("results", p.results) {
                JSONObject()
                    .putOpt("routineId", it.routineId)
                    .put("accepted", it.accepted)
                    .putOpt("reasonCode", it.reasonCode)
                    .putOpt("detail", it.detail)
            }
            .put("hostPaused", p.hostPaused)
            .put("ownerPaused", p.ownerPaused)
            .put("unsolicited", p.unsolicited)
            .putOpt("pcDisabled", p.pcDisabled?.let { JSONArray(it) })
            .putOpt("ownerSuspended", p.ownerSuspended)
            .putOpt("idleSource", p.idleSource)
            .putOpt("sessionSource", p.sessionSource)
            .put("sensorTrigger", p.sensorTrigger)

    fun write(p: RoutineStepRequestPayload): JSONObject =
        JSONObject()
            .putOpt("runId", p.runId)
            .putOpt("routineId", p.routineId)
            .putOpt("routineName", p.routineName)
            .putOpt("triggerType", p.triggerType)
            .put("stepIndex", p.stepIndex)
            .putOpt("step", p.step?.let(::write))
            .put("testRun", p.testRun)
            .putOpt("source", p.source)

    fun write(p: RoutineStepResultPayload): JSONObject =
        JSONObject()
            .putOpt("runId", p.runId)
            .put("stepIndex", p.stepIndex)
            .putOpt("outcome", p.outcome)
            .putOpt("reasonCode", p.reasonCode)
            .put("countdownShown", p.countdownShown)
            .putOpt("cancelledBy", p.cancelledBy)
            .putOpt("detail", p.detail)

    fun write(p: RoutineNotifyPayload): JSONObject =
        JSONObject()
            .putOpt("notifyId", p.notifyId)
            .putOpt("kind", p.kind)
            .putOpt("routineId", p.routineId)
            .putOpt("routineName", p.routineName)
            .putOpt("runId", p.runId)
            .putOpt("title", p.title)
            .putOpt("body", p.body)
            .putOpt("countdownEndsAtUnixMs", p.countdownEndsAtUnixMs)
            .put("queuedAtUnixMs", p.queuedAtUnixMs)
            .put("expiresAtUnixMs", p.expiresAtUnixMs)

    fun write(p: RoutineNotifyAckPayload): JSONObject =
        JSONObject().putOpt("notifyIds", p.notifyIds?.let { JSONArray(it) })

    fun write(p: RoutineRunReportPayload): JSONObject =
        JSONObject()
            .putList("runs", p.runs) { write(it) }
            .put("more", p.more)
            .put("live", p.live)

    fun write(p: RoutineCancelPayload): JSONObject =
        JSONObject().putOpt("runId", p.runId).putOpt("reason", p.reason)

    fun write(p: RoutineRunRequestPayload): JSONObject =
        JSONObject()
            .putOpt("runId", p.runId)
            .putOpt("routineId", p.routineId)
            .put("testRun", p.testRun)
            .putOpt("source", p.source)

    private inline fun <T> JSONObject.putList(key: String, items: List<T>?, write: (T) -> Any?): JSONObject {
        if (items == null) return this
        val array = JSONArray()
        items.forEach { array.put(write(it) ?: JSONObject.NULL) }
        return put(key, array)
    }

    // ── Strict field reading ──

    internal class Flag {
        var malformed = false
    }

    /** Reads fields of one object, setting [flag] instead of coercing a value of the wrong JSON type. */
    internal class Reader(private val obj: JSONObject, val flag: Flag = Flag()) {

        private fun raw(key: String): Any? = if (!obj.has(key) || obj.isNull(key)) null else obj.opt(key)

        private fun explicitNull(key: String): Boolean = obj.has(key) && obj.isNull(key)

        private fun <T> bad(): T? {
            flag.malformed = true
            return null
        }

        fun string(key: String): String? =
            when (val v = raw(key)) {
                null -> null
                is String -> v
                else -> bad()
            }

        /**
         * A whole number that arrived as a decimal (`9.0`, or `09`, which Android's parser reads as
         * 9.0) is accepted as that integer. Android's org.json writes the double 9.0 back out as `9`,
         * so rejecting it would make a routine malformed before a save and valid after the reload
         * (RemEx-3dvre). A fractional value (`5.5`) is still malformed. Note the C# side is stricter
         * here: System.Text.Json rejects `9.0` for an int.
         */
        private fun wholeNumber(v: Number): Long? =
            try {
                BigDecimal(v.toString()).longValueExact()
            } catch (_: NumberFormatException) {
                null
            } catch (_: ArithmeticException) {
                null
            }

        fun intOrNull(key: String): Int? =
            when (val v = raw(key)) {
                null -> null
                is Int -> v
                is Long -> if (v in Int.MIN_VALUE..Int.MAX_VALUE) v.toInt() else bad()
                is Double, is BigDecimal ->
                    wholeNumber(v as Number)?.takeIf { it in Int.MIN_VALUE..Int.MAX_VALUE }?.toInt() ?: bad()
                else -> bad()
            }

        fun longOrNull(key: String): Long? =
            when (val v = raw(key)) {
                null -> null
                is Int -> v.toLong()
                is Long -> v
                is BigInteger -> if (v.bitLength() < 64) v.toLong() else bad()
                is Double, is BigDecimal -> wholeNumber(v as Number) ?: bad()
                else -> bad()
            }

        fun doubleOrNull(key: String): Double? =
            when (val v = raw(key)) {
                null -> null
                is Number -> v.toDouble().takeIf { it.isFinite() } ?: bad()
                else -> bad()
            }

        fun boolOrNull(key: String): Boolean? =
            when (val v = raw(key)) {
                null -> null
                is Boolean -> v
                else -> bad()
            }

        /** A non-nullable C# `int`: absent is 0, JSON null is malformed. */
        fun int(key: String): Int = if (explicitNull(key)) bad<Int>() ?: 0 else intOrNull(key) ?: 0

        fun long(key: String): Long = if (explicitNull(key)) bad<Long>() ?: 0 else longOrNull(key) ?: 0

        fun bool(key: String): Boolean = if (explicitNull(key)) bad<Boolean>() ?: false else boolOrNull(key) ?: false

        fun obj(key: String): JSONObject? =
            when (val v = raw(key)) {
                null -> null
                is JSONObject -> v
                else -> bad()
            }

        fun array(key: String): JSONArray? =
            when (val v = raw(key)) {
                null -> null
                is JSONArray -> v
                else -> bad()
            }

        // A null ELEMENT in either list kind is malformed, so the enclosing payload is rejected. C#
        // enforces the same rule with NoNullElementsListConverter; the rejected.*.json fixtures pin it.
        fun stringList(key: String): List<String>? {
            val array = array(key) ?: return null
            return (0 until array.length()).mapNotNull { i ->
                val v = array.opt(i)
                if (v is String) v else bad()
            }
        }

        fun <T> objList(key: String, read: (JSONObject) -> T): List<T>? {
            val array = array(key) ?: return null
            return (0 until array.length()).mapNotNull { i ->
                val v = array.opt(i)
                if (v is JSONObject) read(v) else bad()
            }
        }
    }
}
