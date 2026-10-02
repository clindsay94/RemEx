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
                        pendingSync?.let { held ->
                                pendingSync = null
                                // Not a baseline: a held sync may be a straggler from before this
                                // connection, and the next real one must never be refused against it.
                                applySync(held, setsBaseline = false)
                        }
                }
        }

        suspend fun onDisconnected() {
                mutex.withLock {
                        connected = false
                        epoch = NoEpoch
                        identity = null
                        pendingSync = null
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

        /** One whole `home_pins_*` envelope from the PC. */
        suspend fun onSyncMessage(json: String) {
                val message = HomePins.parseSync(json) ?: return
                mutex.withLock {
                        if (!connected) {
                                pendingSync = message
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
