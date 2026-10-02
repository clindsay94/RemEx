package com.clindsay94.remex.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The connected PC's Home pinned sensors, as the phone shows and edits them (RemEx-wqo7a.6).
 *
 * @property pinned the pinned sensor NAMES, in the PC's order.
 * @property pinnable the names the PC can pin durably (sensors with a placed card on its canvas).
 *   Only meaningful while [syncSupported]; an older PC sends none.
 * @property syncSupported the connected PC keeps the list itself, so a change goes to it.
 * @property source where [pinned] came from.
 * @property connected a PC is connected and these lists are its own.
 */
data class HomePinsState(
        val pinned: List<String> = emptyList(),
        val pinnable: List<String> = emptyList(),
        val syncSupported: Boolean = false,
        val source: HomePinsSource = HomePinsSource.LOCAL,
        val connected: Boolean = false
) {
        fun isPinned(name: String): Boolean = HomePins.containsName(pinned, name)

        /**
         * Whether [name] can be pinned. A PC that keeps the list can only pin a sensor it has a card
         * for; an older PC never sees the list, so the phone's own list can hold any sensor.
         */
        fun canPin(name: String): Boolean = !syncSupported || HomePins.containsName(pinnable, name)
}

/** Per-PC persistence for [HomePinsRepository]; [SettingsManager] implements it on DataStore. */
interface HomePinsStore {
        suspend fun load(identity: String): HomePinsCache?

        suspend fun save(identity: String, cache: HomePinsCache)
}

/**
 * Keeps [state] in step with the PC (docs/API_CONTRACTS.md section 9).
 *
 * **THE PC OWNS THE LIST.** A `home_pins_sync` replaces [state] wholesale; a phone edit is applied
 * optimistically and sent as ONE per-sensor delta, and the PC's next sync is the answer (a change it
 * cannot honour comes back as the unchanged list, which undoes the optimistic edit by itself).
 *
 * **AN OLDER PC NEVER HEARS OF ANY OF THIS.** Without `supportsHomePinsSync` nothing is sent, and the
 * list is the phone's own, cached per PC with source `local`. When that PC later updates, its first
 * sync with an EMPTY list seeds it from the local one, once: every local name the PC can pin goes over
 * as a delta, and the cache becomes `host` so the seed never repeats.
 *
 * Driven by [com.clindsay94.remex.RemexClientManager] from four inputs that arrive on separate
 * coroutines, so every entry point takes [mutex] and the ordering problems are handled here:
 * a sync that lands before [onConnected] is held and applied by it; a `host_info` is tagged with its
 * connection epoch so one that wins the race against [onConnected] still counts for that connection.
 * Pure JVM: [send] and [isAuthenticated] are injected, so `runTest` drives it with fakes.
 */
class HomePinsRepository(
        private val send: (String) -> Unit,
        private val isAuthenticated: () -> Boolean
) {
        private val mutex = Mutex()
        private val _state = MutableStateFlow(HomePinsState())
        val state: StateFlow<HomePinsState> = _state.asStateFlow()

        @Volatile private var store: HomePinsStore? = null

        private var connected = false
        private var epoch = NoEpoch
        private var identity: String? = null
        private var lastRevision = NoRevision
        private var pendingSync: HomePinsSyncMessage? = null
        /** The connection [pendingSync] arrived on, or null when the caller could not say. */
        private var pendingSyncEpoch: Long? = null
        private var hostInfoSupport: Pair<Long, Boolean>? = null
        private var cachedSource = HomePinsSource.LOCAL
        private var cachedNames: List<String> = emptyList()
        private var seedChecked = false
        private var syncSeen = false
        private var pendingSeed: List<String> = emptyList()

        /** Set once by `RemexClientManager.initialize`; until then nothing is persisted. */
        fun attachStore(store: HomePinsStore) {
                this.store = store
        }

        /**
         * A connection to the PC with [identity] (null when it has no pin to derive one from) is up.
         * Resets everything that belongs to one connection: the PC's revision counter restarts with
         * each connection, and a host switch must never show the last PC's list as this one's.
         */
        suspend fun onConnected(connectionEpoch: Long, identity: String?) {
                mutex.withLock {
                        connected = true
                        epoch = connectionEpoch
                        this.identity = identity
                        lastRevision = NoRevision
                        seedChecked = false
                        syncSeen = false
                        pendingSeed = emptyList()

                        val cache = identity?.let { store?.load(it) }
                        cachedSource = cache?.source ?: HomePinsSource.LOCAL
                        cachedNames = cache?.names.orEmpty()
                        val supported = hostInfoSupport?.takeIf { it.first == connectionEpoch }?.second == true
                        _state.value =
                                HomePinsState(
                                        pinned = cachedNames,
                                        pinnable = cache?.pinnable.orEmpty(),
                                        syncSupported = supported,
                                        source = cachedSource,
                                        connected = true
                                )
                        val heldEpoch = pendingSyncEpoch
                        pendingSyncEpoch = null
                        pendingSync?.let { held ->
                                pendingSync = null
                                when (heldEpoch) {
                                        // Untagged: it may be a straggler from before this connection,
                                        // so it is shown but is not a baseline the next one is refused
                                        // against.
                                        null -> applySync(held, setsBaseline = false)
                                        // This connection's own first sync, which beat this call here.
                                        connectionEpoch -> applySync(held, setsBaseline = true)
                                        // Another connection's: never this PC's list.
                                        else -> Unit
                                }
                        }
                }
        }

        suspend fun onDisconnected() {
                mutex.withLock {
                        // A sync held for a NEWER connection (it beat both this call and that
                        // connection's onConnected) is that connection's, so it survives.
                        val heldForNewer = pendingSyncEpoch?.let { it > epoch } == true
                        if (!heldForNewer) {
                                pendingSync = null
                                pendingSyncEpoch = null
                        }
                        connected = false
                        epoch = NoEpoch
                        identity = null
                        pendingSeed = emptyList()
                        _state.value = _state.value.copy(connected = false, syncSupported = false)
                }
        }

        /** A `host_info` for the connection with [connectionEpoch]. */
        suspend fun onHostInfo(connectionEpoch: Long, hostInfoJson: String) {
                mutex.withLock {
                        val supported = HomePins.parseSupportsSync(hostInfoJson)
                        hostInfoSupport = connectionEpoch to supported
                        if (connected && connectionEpoch == epoch) {
                                // A PC that already sent a sync on this connection keeps the list,
                                // whatever an earlier host_info said.
                                _state.value = _state.value.copy(syncSupported = supported || syncSeen)
                        }
                }
        }

        /** The host acked this connection's proof: a seed held for it can go now. */
        suspend fun onAuthenticated() {
                mutex.withLock { flushSeed() }
        }

        /**
         * One whole `home_pins_*` envelope from the PC, tagged with the [connectionEpoch] it arrived
         * on (phase 3 review R3). The connection collector and this one run separately, and the
         * connection flow can conflate A -> null -> B into A -> B, so B's first sync can land while
         * this repository still holds A's epoch, revision and identity. Untagged, it was scored
         * against A's revision (and refused if lower) or saved under A's key. Tagged, a sync for a
         * connection not yet announced is held until [onConnected] names that epoch, and one for an
         * older connection is dropped. Null keeps the untagged behaviour for a caller that cannot say.
         */
        suspend fun onSyncMessage(json: String, connectionEpoch: Long? = null) {
                val message = HomePins.parseSync(json) ?: return
                mutex.withLock {
                        if (connected && connectionEpoch != null && connectionEpoch < epoch) return
                        if (!connected || (connectionEpoch != null && connectionEpoch != epoch)) {
                                pendingSync = message
                                pendingSyncEpoch = connectionEpoch
                                return
                        }
                        if (lastRevision != NoRevision && message.revision < lastRevision) return
                        applySync(message, setsBaseline = true)
                }
        }

        /**
         * Pins or unpins [name] from the phone. Returns false when nothing changed: no PC connected, a
         * name the PC would drop, a sensor the PC cannot pin, or a PC that keeps the list but has not
         * acked this connection yet (a change sent then would be refused).
         */
        suspend fun setPinned(name: String, pinned: Boolean): Boolean =
                mutex.withLock {
                        if (!connected || !HomePins.isValidSensorName(name)) return@withLock false
                        val current = _state.value
                        if (current.syncSupported) {
                                if (!isAuthenticated()) return@withLock false
                                if (pinned && !current.canPin(name)) return@withLock false
                                val envelope = HomePins.buildChangeEnvelope(name, pinned) ?: return@withLock false
                                _state.value = current.copy(pinned = HomePins.withPin(current.pinned, name, pinned))
                                send(envelope)
                        } else {
                                val names = HomePins.withPin(current.pinned, name, pinned)
                                cachedSource = HomePinsSource.LOCAL
                                cachedNames = names
                                _state.value = current.copy(pinned = names, source = HomePinsSource.LOCAL)
                                persist(HomePinsCache(names, current.pinnable, HomePinsSource.LOCAL))
                        }
                        true
                }

        private suspend fun applySync(message: HomePinsSyncMessage, setsBaseline: Boolean) {
                if (setsBaseline) lastRevision = message.revision
                syncSeen = true

                var pinned = message.sensorNames
                if (!seedChecked) {
                        seedChecked = true
                        if (pinned.isEmpty() && cachedSource == HomePinsSource.LOCAL && cachedNames.isNotEmpty()) {
                                val seed = cachedNames.filter { HomePins.containsName(message.pinnableSensorNames, it) }
                                if (seed.isNotEmpty()) {
                                        pinned = seed
                                        pendingSeed = seed
                                }
                        }
                }

                cachedSource = HomePinsSource.HOST
                cachedNames = pinned
                _state.value =
                        HomePinsState(
                                pinned = pinned,
                                pinnable = message.pinnableSensorNames,
                                syncSupported = true,
                                source = HomePinsSource.HOST,
                                connected = true
                        )
                persist(HomePinsCache(pinned, message.pinnableSensorNames, HomePinsSource.HOST))
                flushSeed()
        }

        private fun flushSeed() {
                if (pendingSeed.isEmpty() || !connected || !isAuthenticated()) return
                val seed = pendingSeed
                pendingSeed = emptyList()
                seed.forEach { name -> HomePins.buildChangeEnvelope(name, pinned = true)?.let(send) }
        }

        private suspend fun persist(cache: HomePinsCache) {
                val id = identity ?: return
                store?.save(id, cache)
        }

        private companion object {
                const val NoRevision = Long.MIN_VALUE
                const val NoEpoch = Long.MIN_VALUE
        }
}
