package com.clindsay94.remex.ui.screens

import com.clindsay94.remex.data.HomePins
import com.clindsay94.remex.data.HomePinsState
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.routines.RoutineHistoryStore
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import org.json.JSONObject

/** How long the PC has been on, from the host's `uptimeText` (`"{d}d {h}h {m}m"`). */
data class PcUptime(val days: Int, val hours: Int, val minutes: Int)

/** One row of Home's pin sheet: a sensor name, whether it is pinned, and whether it can be. */
data class PinSheetEntry(val name: String, val pinned: Boolean, val canPin: Boolean)

/** A titled run of [PinSheetEntry]s. A null [label] is the group of pinned sensors not reporting now. */
data class PinSheetGroup(val label: String?, val entries: List<PinSheetEntry>)

/** One Recent activity row on Home. */
sealed class RecentActivity {
    abstract val atMillis: Long

    /** The last routine that finished, how it went, and the reason code behind a failure. */
    data class RoutineRan(
            val routineName: String,
            val succeeded: Boolean,
            val reasonCode: String?,
            val reasonArgs: RoutineReasonArgs?,
            override val atMillis: Long,
    ) : RecentActivity()

    /** The last time this phone reached a PC. */
    data class Connected(val pcName: String, override val atMillis: Long) : RecentActivity()
}

/**
 * Home's rules, pure (RemEx-wqo7a.6): what the PC card says, which sensors show, what the pin sheet
 * lists and what Recent activity remembers. The screen only draws what these return.
 */
object HomeLogic {

    /** The PC card's quick actions, in order. Wake is on the connect card, not here. */
    val QUICK_ACTIONS: List<String> = listOf("Lock", "Sleep")

    /** At most this many Recent activity rows. */
    const val MAX_RECENT_ACTIVITY = 3

    private val UPTIME = Regex("""^(\d+)d (\d+)h (\d+)m$""")

    /** The host's uptime text, or null when it is absent or anything other than the expected shape ("N/A"). */
    fun parseUptime(text: String?): PcUptime? {
        val match = UPTIME.matchEntire(text?.trim() ?: return null) ?: return null
        val (d, h, m) = match.destructured
        return runCatching { PcUptime(d.toInt(), h.toInt(), m.toInt()) }.getOrNull()
    }

    /** `uptimeText` from one telemetry payload; null when missing or not JSON. */
    fun uptimeTextOf(telemetryJson: String): String? =
            runCatching { JSONObject(telemetryJson).optString("uptimeText").takeIf { it.isNotBlank() } }.getOrNull()

    /**
     * Whether Home shows [action] for a PC that advertised [powerVerbs]. The Commands grid's own rule,
     * so the two cannot disagree: null (an older PC, or nothing heard yet) shows everything.
     */
    fun isQuickActionOffered(action: String, powerVerbs: List<String>?): Boolean = CommandsLayout.isOffered(action, powerVerbs)

    /** Whether tapping [action] on Home asks first. The Commands grid's policy, read from its one place. */
    fun needsConfirmation(action: String): Boolean = actionDiscardsWork(action)

    /**
     * Whether the disconnected PC card offers Wake (3.0 comb, home-firstrun-card). Not with no paired
     * PC: there is nothing to wake, and the attempt's error sent a new user off to a MAC setting.
     */
    fun connectCardOffersWake(pairedPcCount: Int): Boolean = pairedPcCount > 0

    /**
     * The pinned sensors that are reporting, in pin order. A pinned sensor the PC is not reporting
     * right now is left out of the row (it stays in the sheet); a name matches its first sensor,
     * case-insensitively, like the PC Home.
     */
    fun pinnedTiles(pinned: List<String>, sensors: List<TelemetrySensor>): List<TelemetrySensor> =
            pinned.mapNotNull { name -> sensors.firstOrNull { it.name.equals(name, ignoreCase = true) } }
                    .distinctBy { it.id }

    /**
     * What the pin sheet lists. A PC that keeps the list offers what it can pin (plus anything
     * already pinned, so it can be unpinned); an older PC's phone-local list offers every reporting
     * sensor. Grouped by the sensor's group (its category when it has none), in telemetry order;
     * pinned sensors that are not reporting come last under a null label.
     */
    fun pinSheet(state: HomePinsState, sensors: List<TelemetrySensor>): List<PinSheetGroup> {
        val offered = if (state.syncSupported) state.pinnable else sensors.map { it.name }
        val candidates = HomePins.normalizeNames(offered + state.pinned)

        val groups = LinkedHashMap<String, MutableList<PinSheetEntry>>()
        val listed = HashSet<String>()
        for (sensor in sensors) {
            val name = candidates.firstOrNull { it.equals(sensor.name, ignoreCase = true) } ?: continue
            if (!listed.add(name.lowercase())) continue
            val label = sensor.group.ifBlank { sensor.category }
            groups.getOrPut(label) { ArrayList() } += PinSheetEntry(name, state.isPinned(name), state.canPin(name))
        }
        val notReporting =
                candidates.filter { it.lowercase() !in listed }
                        .map { PinSheetEntry(it, state.isPinned(it), state.canPin(it)) }

        return groups.map { (label, entries) -> PinSheetGroup(label, entries) } +
                if (notReporting.isEmpty()) emptyList() else listOf(PinSheetGroup(null, notReporting))
    }

    /**
     * Recent activity, newest first, at most [MAX_RECENT_ACTIVITY] rows: the last routine run that
     * finished and the last connection. Empty when there is neither, and Home hides the section.
     */
    fun recentActivity(history: List<RoutineRun>, connections: List<KnownPcEntry>): List<RecentActivity> {
        val lastRun =
                history.firstOrNull {
                    it.routineId != RoutineHistoryStore.STORE_EVENTS_ID &&
                            it.outcome != null && it.outcome != RoutineRunOutcomes.RUNNING &&
                            !it.routineName.isNullOrBlank()
                }
        val runRow =
                lastRun?.let {
                    RecentActivity.RoutineRan(
                            routineName = it.routineName.orEmpty(),
                            succeeded = it.outcome == RoutineRunOutcomes.SUCCEEDED,
                            reasonCode = it.reasonCode,
                            reasonArgs = it.reasonArgs,
                            atMillis = it.endedAtUnixMs ?: it.startedAtUnixMs,
                    )
                }
        val connectionRow =
                connections.filter { it.hasEverConnected }
                        .maxByOrNull { it.lastConnectedAtMillis }
                        ?.let { RecentActivity.Connected(it.displayName, it.lastConnectedAtMillis) }

        return listOfNotNull(runRow, connectionRow)
                .filter { it.atMillis > 0L }
                .sortedByDescending { it.atMillis }
                .take(MAX_RECENT_ACTIVITY)
    }

    /**
     * What the PC card calls the connected PC: the Known PCs row's name for its address (nickname,
     * else the machine name it reported), else the machine name in this connection's `host_info`,
     * else the address.
     */
    fun pcName(host: String, rows: List<KnownPcEntry>, hostMachineName: String): String {
        val row = rows.firstOrNull { it.address == host }
        return row?.takeIf { it.isNamed }?.displayName ?: hostMachineName.trim().ifEmpty { host }
    }
}
