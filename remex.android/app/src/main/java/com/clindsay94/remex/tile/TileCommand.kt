package com.clindsay94.remex.tile

import android.util.Log
import com.clindsay94.remex.RemexCoreClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.json.JSONObject

private const val TAG = "TileCommand"

/**
 * Where a tile's command runs after the tile itself is gone (RemEx-66rf).
 *
 * **DELIBERATELY NOT SCOPED TO THE SERVICE.** A [android.service.quicksettings.TileService] is torn
 * down within moments of `onClick`, and `RemexCoreClient.SendCommand` now waits for the PC's real
 * answer, so the coroutine lives for the whole round trip instead of returning at once.
 *
 * What a cancelled scope actually costs is narrower than it first looks, and worth stating precisely:
 * cancellation cannot interrupt the JNI call itself — there is no suspension point inside a native
 * frame — so a command already inside `SendCommandNative` completes regardless. The loss is at
 * suspension points, so it bites hardest where one sits BEFORE the send, as Wake-on-LAN's settings
 * read does. A scope that outlives the service removes the question either way.
 *
 * The work is bounded — `SendCommandAsync` budgets the send and the reply together at 10 seconds and
 * always returns a response (RemEx-66rf had to move that budget to cover the send) — so a
 * process-lifetime scope cannot accumulate. `WidgetDataCache` is precedent for a never-cancelled
 * scope of this kind and documents the same reasoning, though it declares its own function-locally
 * rather than at file level.
 */
private val TileCommandScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

/**
 * Sends a one-shot system command to the PC from a Quick Settings tile.
 *
 * Was seven identical private copies, each calling `SendCommand` straight from `onClick` — which runs
 * on the MAIN THREAD. That was harmless only while the native call returned instantly without
 * waiting for the host; the moment it started waiting for a real answer, every one of them became an
 * ANR on an unreachable PC. Making `SendCommand` a suspend function turned that into seven compiler
 * errors rather than seven crashes in the field.
 *
 * The result is not shown: a tile has nowhere to put one, and giving it somewhere is its own piece of
 * work (RemEx-mug0). It IS logged, though, which it could not be before — the outcome did not exist.
 * "The tile does nothing" with an empty logcat is not a report anybody can act on.
 */
internal fun sendTileCommand(action: String, parameters: JSONObject = JSONObject()) {
    // Always carries a parameters object, where the seven tiles used to omit the field for commands
    // that take none. Not a wire change worth worrying about: RemoteControlViewModel has always sent
    // these same verbs WITH an empty parameters object, so the host has been accepting both forms all
    // along, and it reads the field with a null-safe TryGetValue either way.
    val commandJson =
        JSONObject()
            .apply {
                put("action", action)
                put("parameters", parameters)
            }
            .toString()
    launchTileWork {
        val response = RemexCoreClient.SendCommand(commandJson).getOrNull()
        // runCatching, not a bare JSONObject(...): TileCommandScope has a SupervisorJob but no
        // CoroutineExceptionHandler, and a supervisor stops a failure reaching SIBLINGS rather than
        // swallowing it. A JSONException on an unreadable response would reach the thread's default
        // handler and kill the process, from the one code path here whose job is to log quietly.
        val succeeded =
            response != null &&
                runCatching { JSONObject(response).optBoolean("success", false) }
                    .getOrDefault(false)

        if (!succeeded) {
            Log.w(TAG, "Tile command $action did not succeed: $response")
        }
    }
}

/**
 * Broadcasts a Wake-on-LAN packet from THIS PHONE, for a tile whose whole job is waking a PC that is
 * off (RemEx-wgpm).
 *
 * **NOT THE WAKEONLAN COMMAND VERB, WHICH IS WHAT THIS TILE USED TO SEND.** That verb travels over
 * the control socket and asks the PC to do the broadcasting — so it needed a PC that was already
 * awake and connected, which is the one state in which nothing needs waking. The MAC it sent came
 * from `macAddressFlow`, which falls back to the host's own reported MAC, so the PC was broadcasting
 * a wake packet FOR ITSELF onto its own network: a no-op, reachable only when its target was already
 * on. The tile meanwhile advertises itself as available whenever a MAC is configured, connection or
 * not — which is what the phone-side path actually supports.
 *
 * `WakePc` sends the magic packet from the phone's own interfaces and needs no connection at all,
 * which is what the Dashboard and Remote Control screens have always used.
 *
 * The command verb itself is NOT dead — `PingPongHandler`'s WAKEONLAN case still serves the PC's own
 * Wake-on-LAN UI and the TCP script ingress, both of which legitimately wake a third machine from an
 * always-on PC.
 */
internal fun sendTileWake(macAddress: String, broadcastIp: String, port: Int) {
    launchTileWork {
        val response = RemexCoreClient.WakePc(macAddress, broadcastIp, port).getOrNull()

        // runCatching, not a bare JSONObject(...) — see the identical note on
        // WidgetCommand.reportedSuccess. TileCommandScope has a SupervisorJob and no
        // CoroutineExceptionHandler, and a supervisor stops a failure reaching SIBLINGS rather than
        // swallowing it: a JSONException here would reach the thread's default handler and kill the
        // process, from a path whose only job is to log quietly.
        val sent =
            response != null &&
                runCatching { JSONObject(response).optBoolean("success", false) }
                    .getOrDefault(false)

        // Loggable at all only since RemEx-52n0, which stopped the native side reporting success
        // unconditionally. Before that there was nothing here worth writing down.
        if (!sent) Log.w(TAG, "Tile wake did not leave the phone: $response")
    }
}

/**
 * Runs a tile's fire-and-forget work somewhere it will not be cancelled out from under it.
 *
 * For tiles that must read something before they can send — Wake-on-LAN needs the stored MAC address
 * — so the read and the send share one scope rather than the read completing into a scope that has
 * since been torn down.
 */
internal fun launchTileWork(block: suspend CoroutineScope.() -> Unit) {
    TileCommandScope.launch(block = block)
}
