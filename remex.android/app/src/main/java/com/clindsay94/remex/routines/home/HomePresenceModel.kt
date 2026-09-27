package com.clindsay94.remex.routines.home

import org.json.JSONArray
import org.json.JSONObject

/** Presence of the phone at its home (spec §8.3.1 "Presence state"). */
enum class PresenceState { UNKNOWN, HOME, AWAY }

/**
 * The persisted presence of one home. Wall-clock times: they must survive the process and a reboot,
 * and every rule compares them with minutes, not milliseconds.
 *
 * @property changedAtUnixMs the last transition, for the 300 s opposite-transition gap.
 * @property homeSinceUnixMs when the phone last became HOME; a leave closer than 300 s to it is a flap.
 * @property awaySinceUnixMs while HOME (and after): when home stopped matching. Set only from HOME,
 *   so a phone that starts away never fires a leave.
 * @property arriveSinceUnixMs while AWAY: when home started matching (the 10 s settle).
 * @property firedLeave the leave routines already handled in this away episode.
 */
data class PresenceRecord(
    val state: PresenceState = PresenceState.UNKNOWN,
    val changedAtUnixMs: Long = 0,
    val homeSinceUnixMs: Long? = null,
    val awaySinceUnixMs: Long? = null,
    val arriveSinceUnixMs: Long? = null,
    val firedLeave: Set<String> = emptySet(),
)

/** The phone's one home (spec §6.6, D9): the displayable facts, their tokens, and the presence state. */
data class Home(
    val id: String,
    val capturedWithHostIdentity: String,
    val capturedAtUnixMs: Long,
    val facts: HomeFacts,
    val tokens: HomeTokens,
    val presence: PresenceRecord = PresenceRecord(),
)

/**
 * The `home` object of the routine store document (it is kept verbatim there as `homeJson`). The id
 * sits at the top level, where the validator reads it. A home that does not read back is null, and
 * `home.*` routines then show "Set up your home network": never a guessed default.
 */
object HomeCodec {
    fun encode(home: Home): String =
        JSONObject()
            .put("id", home.id)
            .put("capturedWithHostIdentity", home.capturedWithHostIdentity)
            .put("capturedAtUnixMs", home.capturedAtUnixMs)
            .put("gateways", JSONArray(home.facts.gateways))
            .put("prefixes", JSONArray(home.facts.prefixes))
            .put("dnsServers", JSONArray(home.facts.dnsServers))
            .putOpt("domain", home.facts.domain)
            .putOpt("dhcpServer", home.facts.dhcpServer)
            .putOpt("ipv6Prefix48", home.facts.ipv6Prefix48)
            .put(
                "tokens",
                JSONObject()
                    .put("gateways", JSONArray(home.tokens.gateways.sorted()))
                    .put("prefixes", JSONArray(home.tokens.prefixes.sorted()))
                    .put("secondary", JSONArray(home.tokens.secondary.sorted())),
            )
            .put(
                "presence",
                JSONObject()
                    .put("state", home.presence.state.name)
                    .put("changedAtUnixMs", home.presence.changedAtUnixMs)
                    .putOpt("homeSinceUnixMs", home.presence.homeSinceUnixMs)
                    .putOpt("awaySinceUnixMs", home.presence.awaySinceUnixMs)
                    .putOpt("arriveSinceUnixMs", home.presence.arriveSinceUnixMs)
                    .put("firedLeave", JSONArray(home.presence.firedLeave.sorted())),
            )
            .toString()

    fun decode(json: String?): Home? =
        runCatching {
            val obj = JSONObject(json ?: return null)
            val tokens = obj.getJSONObject("tokens")
            val presence = obj.optJSONObject("presence")
            Home(
                id = obj.getString("id").takeIf { it.isNotBlank() } ?: return null,
                capturedWithHostIdentity = obj.getString("capturedWithHostIdentity"),
                capturedAtUnixMs = obj.getLong("capturedAtUnixMs"),
                facts =
                    HomeFacts(
                        gateways = strings(obj.getJSONArray("gateways")),
                        prefixes = strings(obj.getJSONArray("prefixes")),
                        dnsServers = strings(obj.optJSONArray("dnsServers")),
                        domain = obj.optString("domain").takeIf { obj.has("domain") && it.isNotBlank() },
                        dhcpServer = obj.optString("dhcpServer").takeIf { obj.has("dhcpServer") && it.isNotBlank() },
                        ipv6Prefix48 = obj.optString("ipv6Prefix48").takeIf { obj.has("ipv6Prefix48") && it.isNotBlank() },
                    ),
                tokens =
                    HomeTokens(
                        gateways = strings(tokens.getJSONArray("gateways")).toSet(),
                        prefixes = strings(tokens.getJSONArray("prefixes")).toSet(),
                        secondary = strings(tokens.optJSONArray("secondary")).toSet(),
                    ),
                presence = presence?.let(::readPresence) ?: PresenceRecord(),
            ).takeIf { it.tokens.gateways.isNotEmpty() && it.tokens.prefixes.isNotEmpty() }
        }.getOrNull()

    private fun readPresence(obj: JSONObject): PresenceRecord =
        PresenceRecord(
            state = runCatching { PresenceState.valueOf(obj.getString("state")) }.getOrDefault(PresenceState.UNKNOWN),
            changedAtUnixMs = obj.optLong("changedAtUnixMs", 0L),
            homeSinceUnixMs = obj.optLongOrNull("homeSinceUnixMs"),
            awaySinceUnixMs = obj.optLongOrNull("awaySinceUnixMs"),
            arriveSinceUnixMs = obj.optLongOrNull("arriveSinceUnixMs"),
            firedLeave = strings(obj.optJSONArray("firedLeave")).toSet(),
        )

    private fun JSONObject.optLongOrNull(key: String): Long? = if (has(key) && !isNull(key)) optLong(key) else null

    private fun strings(array: JSONArray?): List<String> =
        if (array == null) emptyList() else (0 until array.length()).mapNotNull { array.optString(it).takeIf(String::isNotBlank) }
}

/** What a presence evaluation asks the engine to do. */
sealed interface PresenceEvent {
    /** Start every enabled `home.arrive` routine, or record `flap_suppressed` for each when [suppressed]. */
    data class Arrive(val suppressed: Boolean) : PresenceEvent

    /** Start one `home.leave` routine, or record `flap_suppressed` for it. */
    data class Leave(val routineId: String, val suppressed: Boolean) : PresenceEvent
}

data class PresenceStep(val presence: PresenceRecord, val events: List<PresenceEvent>, val recheckAtUnixMs: Long?)

/**
 * The presence rules (spec §8.3.1, T9, T20, R-SEC-14):
 * - From UNKNOWN nothing ever fires: a reboot or a first capture at home must not wake the PC, and a
 *   phone that starts away has not "left".
 * - Arrive: AWAY to HOME once home has matched for 10 s (a re-check at the deadline confirms it).
 * - Leave: each `home.leave` routine fires once home has not matched for its own debounce; any match
 *   in between resets it. The state itself turns AWAY at the shortest debounce.
 * - Flaps: a transition closer than 300 s to the opposite one does not fire; it is recorded as
 *   `flap_suppressed` instead.
 */
object PresenceStateMachine {
    const val ARRIVE_SETTLE_MS = 10_000L
    const val FLAP_GAP_MS = 300_000L

    /**
     * @param matches whether any current network matches home.
     * @param leaveDebounceSeconds each enabled `home.leave` routine's debounce, by routine id.
     */
    fun step(presence: PresenceRecord, matches: Boolean, nowUnixMs: Long, leaveDebounceSeconds: Map<String, Int>, defaultLeaveSeconds: Int): PresenceStep =
        when (presence.state) {
            // Leaving UNKNOWN is learning the state, not a transition: it stamps no flap window, so
            // the first real arrive or leave after it is judged on its own.
            PresenceState.UNKNOWN ->
                if (matches) {
                    PresenceStep(PresenceRecord(PresenceState.HOME, changedAtUnixMs = 0, homeSinceUnixMs = 0), emptyList(), null)
                } else {
                    PresenceStep(PresenceRecord(PresenceState.AWAY, changedAtUnixMs = 0), emptyList(), null)
                }
            PresenceState.HOME ->
                if (matches) {
                    PresenceStep(presence.copy(awaySinceUnixMs = null, arriveSinceUnixMs = null, firedLeave = emptySet()), emptyList(), null)
                } else {
                    leaving(presence.copy(awaySinceUnixMs = presence.awaySinceUnixMs ?: nowUnixMs), nowUnixMs, leaveDebounceSeconds, defaultLeaveSeconds)
                }
            PresenceState.AWAY ->
                if (matches) arriving(presence, nowUnixMs) else leaving(presence.copy(arriveSinceUnixMs = null), nowUnixMs, leaveDebounceSeconds, defaultLeaveSeconds)
        }

    private fun arriving(presence: PresenceRecord, now: Long): PresenceStep {
        val since = presence.arriveSinceUnixMs ?: now
        if (now - since < ARRIVE_SETTLE_MS) {
            return PresenceStep(presence.copy(arriveSinceUnixMs = since), emptyList(), since + ARRIVE_SETTLE_MS)
        }
        val suppressed = now - presence.changedAtUnixMs < FLAP_GAP_MS
        val next = PresenceRecord(PresenceState.HOME, now, homeSinceUnixMs = now)
        return PresenceStep(next, listOf(PresenceEvent.Arrive(suppressed)), null)
    }

    private fun leaving(presence: PresenceRecord, now: Long, debounces: Map<String, Int>, defaultSeconds: Int): PresenceStep {
        val awaySince = presence.awaySinceUnixMs ?: return PresenceStep(presence, emptyList(), null)
        val deadlines = debounces.mapValues { (_, seconds) -> awaySince + seconds * 1000L }
        val stateDeadline = awaySince + (debounces.values.minOrNull() ?: defaultSeconds) * 1000L
        val events = ArrayList<PresenceEvent>()
        var next = presence
        if (next.state == PresenceState.HOME && now >= stateDeadline) {
            next = next.copy(state = PresenceState.AWAY, changedAtUnixMs = now)
        }
        val due = deadlines.filter { (id, at) -> now >= at && id !in next.firedLeave }.keys.sorted()
        if (due.isNotEmpty()) {
            val homeSince = presence.homeSinceUnixMs ?: 0L
            due.forEach { events += PresenceEvent.Leave(it, suppressed = now - homeSince < FLAP_GAP_MS) }
            next = next.copy(firedLeave = next.firedLeave + due)
        }
        val pending = deadlines.filter { (id, _) -> id !in next.firedLeave }.values + listOfNotNull(stateDeadline.takeIf { next.state == PresenceState.HOME })
        return PresenceStep(next, events, pending.filter { it > now }.minOrNull())
    }
}
