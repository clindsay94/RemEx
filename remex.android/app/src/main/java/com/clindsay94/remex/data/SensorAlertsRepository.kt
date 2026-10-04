package com.clindsay94.remex.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The connected PC's sensor alert rules, as the phone shows and edits them (RemEx-pp4cm.12).
 *
 * @property rules the PC's rules. Empty until [loaded].
 * @property supported the connected PC mirrors its alerts, so a change goes to it. An older PC does not,
 *   and the alert controls stay hidden.
 * @property connected a PC is connected and these rules are its own.
 * @property loaded the PC has sent its rule list on this connection, so [rules] is real rather than "not asked yet".
 */
data class SensorAlertsState(
        val rules: List<SensorAlertRule> = emptyList(),
        val supported: Boolean = false,
        val connected: Boolean = false,
        val loaded: Boolean = false
) {
        /** The rule for [sensorName] (case-insensitive), or null. */
        fun ruleFor(sensorName: String): SensorAlertRule? = rules.firstOrNull { it.sensorName.equals(sensorName, ignoreCase = true) }

        /** Whether an alert can be set, changed or removed right now. */
        val canEdit: Boolean get() = connected && supported
}

/**
 * Keeps [state] in step with the PC (docs/API_CONTRACTS.md section 10).
 *
 * **THE PC OWNS THE RULES.** A `sensor_alert_rules` replaces [state] wholesale; a phone edit is applied
 * optimistically and sent as ONE rule, and the PC's next list is the answer (a change it refuses comes
 * back as the unchanged list, which undoes the optimistic edit by itself). Nothing is persisted: the
 * phone asks for the list each time it connects, so what it shows is never older than the connection.
 *
 * Driven by [com.clindsay94.remex.RemexClientManager] from four inputs that arrive on separate
 * coroutines, so every entry point takes [mutex] and the ordering problems are handled here, the same
 * ones [HomePinsRepository] solves: a list that lands before [onConnected] is held and applied by it;
 * a list is tagged with its connection epoch so one from an older connection is dropped. The list is
 * asked for once per connection, as soon as the PC is known to support alerts AND has acked this
 * connection (a request sent earlier would be refused). Pure JVM: [send] and [isAuthenticated] are
 * injected, so `runTest` drives it with fakes.
 */
class SensorAlertsRepository(
        private val send: (String) -> Unit,
        private val isAuthenticated: () -> Boolean
) {
        private val mutex = Mutex()
        private val _state = MutableStateFlow(SensorAlertsState())
        val state: StateFlow<SensorAlertsState> = _state.asStateFlow()

        private var connected = false
        private var epoch = NoEpoch
        private var lastRevision = NoRevision
        private var requested = false
        private var pendingRules: SensorAlertRulesMessage? = null
        private var pendingEpoch: Long? = null
        private var hostInfoSupport: Pair<Long, Boolean>? = null

        /** A connection to the PC with [connectionEpoch] is up. Resets everything that belongs to one connection. */
        suspend fun onConnected(connectionEpoch: Long) {
                mutex.withLock {
                        connected = true
                        epoch = connectionEpoch
                        lastRevision = NoRevision
                        requested = false
                        val supported = hostInfoSupport?.takeIf { it.first == connectionEpoch }?.second == true
                        _state.value = SensorAlertsState(supported = supported, connected = true)

                        val held = pendingRules
                        val heldEpoch = pendingEpoch
                        pendingRules = null
                        pendingEpoch = null
                        if (held != null && (heldEpoch == null || heldEpoch == connectionEpoch)) applyRules(held)
                        requestRulesIfReady()
                }
        }

        suspend fun onDisconnected() {
                mutex.withLock {
                        // A list held for a NEWER connection beat both this call and that connection's
                        // onConnected, so it survives.
                        if (pendingEpoch?.let { it > epoch } != true) {
                                pendingRules = null
                                pendingEpoch = null
                        }
                        connected = false
                        epoch = NoEpoch
                        requested = false
                        _state.value = SensorAlertsState()
                }
        }

        /** A `host_info` for the connection with [connectionEpoch]. */
        suspend fun onHostInfo(connectionEpoch: Long, hostInfoJson: String) {
                mutex.withLock {
                        val supported = SensorAlerts.parseSupports(hostInfoJson)
                        hostInfoSupport = connectionEpoch to supported
                        if (connected && connectionEpoch == epoch) {
                                _state.value = _state.value.copy(supported = supported)
                                requestRulesIfReady()
                        }
                }
        }

        /** The host acked this connection's proof: the rule list can be asked for now. */
        suspend fun onAuthenticated() {
                mutex.withLock { requestRulesIfReady() }
        }

        /**
         * One whole `sensor_alert_rules` envelope, tagged with the [connectionEpoch] it arrived on. A list
         * for a connection not yet announced is held until [onConnected] names that epoch; one for an older
         * connection, or older than the last on this one, is dropped. Null keeps the untagged behaviour.
         */
        suspend fun onRulesMessage(json: String, connectionEpoch: Long? = null) {
                val message = SensorAlerts.parseRules(json) ?: return
                mutex.withLock {
                        if (connected && connectionEpoch != null && connectionEpoch < epoch) return
                        if (!connected || (connectionEpoch != null && connectionEpoch != epoch)) {
                                pendingRules = message
                                pendingEpoch = connectionEpoch
                                return
                        }
                        if (lastRevision != NoRevision && message.revision < lastRevision) return
                        applyRules(message)
                }
        }

        /** Asks the PC for its rule list again. False when nothing was sent. */
        suspend fun refresh(): Boolean =
                mutex.withLock {
                        if (!_state.value.canEdit || !isAuthenticated()) return@withLock false
                        send(SensorAlerts.buildGetEnvelope())
                        true
                }

        /**
         * Sets (adds or replaces) the rule for [sensorName]. Returns false, sending nothing, when no PC that
         * mirrors alerts is connected and authenticated, or the rule is one the PC would refuse. Otherwise
         * the rule shows at once and the PC's next list confirms or undoes it.
         */
        suspend fun setRule(
                sensorName: String,
                displayName: String,
                unit: String?,
                threshold: Double,
                direction: SensorAlertDirection,
                severity: SensorAlertSeverity
        ): Boolean =
                mutex.withLock {
                        val current = _state.value
                        if (!current.canEdit || !isAuthenticated()) return@withLock false
                        val envelope = SensorAlerts.buildSetEnvelope(sensorName, threshold, direction, severity) ?: return@withLock false
                        val existing = current.ruleFor(sensorName)
                        if (existing == null && current.rules.size >= SensorAlerts.MaxRules) return@withLock false
                        val rule =
                                SensorAlertRule(
                                        sensorName = existing?.sensorName ?: sensorName,
                                        displayName = existing?.displayName ?: displayName,
                                        unit = existing?.unit ?: unit,
                                        currentValue = existing?.currentValue,
                                        threshold = threshold,
                                        direction = direction,
                                        severity = severity
                                )
                        _state.value =
                                current.copy(
                                        rules = current.rules.filterNot { it.sensorName.equals(sensorName, ignoreCase = true) } + rule
                                )
                        send(envelope)
                        true
                }

        /** Removes the rule for [sensorName]. Returns false, sending nothing, when no such change can be made. */
        suspend fun removeRule(sensorName: String): Boolean =
                mutex.withLock {
                        val current = _state.value
                        if (!current.canEdit || !isAuthenticated()) return@withLock false
                        val envelope = SensorAlerts.buildRemoveEnvelope(sensorName) ?: return@withLock false
                        _state.value =
                                current.copy(rules = current.rules.filterNot { it.sensorName.equals(sensorName, ignoreCase = true) })
                        send(envelope)
                        true
                }

        private fun applyRules(message: SensorAlertRulesMessage) {
                lastRevision = message.revision
                // A PC that sends a list mirrors alerts, whatever an earlier host_info said.
                _state.value = _state.value.copy(rules = message.rules, supported = true, connected = true, loaded = true)
        }

        private fun requestRulesIfReady() {
                if (requested || !connected || !_state.value.supported || !isAuthenticated()) return
                requested = true
                send(SensorAlerts.buildGetEnvelope())
        }

        private companion object {
                const val NoRevision = Long.MIN_VALUE
                const val NoEpoch = Long.MIN_VALUE
        }
}
