package com.clindsay94.remex.ui.screens

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.platform.LocalContext
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.clindsay94.remex.security.TransportTrust
import com.clindsay94.remex.ui.routines.findActivity

/**
 * Which runtime permissions a connection or a discovery needs, and when a refusal is fatal.
 *
 * Pure, so the rules are tested on the JVM. They used to live inside ConnectionScreen alone, which is
 * why Home's Connect and a scanned QR code skipped them and failed on Android 17 with nothing asked
 * (3.0 comb, qr-home-no-perms).
 */
object ConnectPermissions {
    /** Android 17's permission for talking to devices on the local network. */
    const val ACCESS_LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"

    /** First SDK where the local-network permission exists to be asked for. */
    const val LOCAL_NETWORK_REQUEST_SDK = 36

    /** First SDK where refusing it actually blocks LAN sockets, so a refusal is worth stopping on. */
    const val LOCAL_NETWORK_ENFORCED_SDK = 37

    /**
     * What connecting needs. A loopback or VPN/Tailscale address is not on the local network, so the
     * LAN permissions are neither asked for nor treated as blocking there - only POST_NOTIFICATIONS,
     * for the keepalive service. That is what lets a Tailscale connection go ahead on a phone that
     * has declined local-network access.
     */
    fun forConnect(needsLan: Boolean, sdkInt: Int): List<String> = buildList {
        add(Manifest.permission.POST_NOTIFICATIONS)
        if (needsLan) add(Manifest.permission.NEARBY_WIFI_DEVICES)
        if (needsLan && sdkInt >= LOCAL_NETWORK_REQUEST_SDK) add(ACCESS_LOCAL_NETWORK)
    }

    /** What "Discover automatically" needs: discovery is local-network by definition. */
    fun forDiscovery(sdkInt: Int): List<String> = buildList {
        add(Manifest.permission.NEARBY_WIFI_DEVICES)
        if (sdkInt >= LOCAL_NETWORK_REQUEST_SDK) add(ACCESS_LOCAL_NETWORK)
    }

    /**
     * Whether the answer to a permission request means the attempt cannot work. Only a refused
     * local-network grant, and only where the attempt needs the LAN on a version that enforces it;
     * a refused notification permission costs the keepalive notice, not the connection.
     */
    fun isLocalNetworkRefused(results: Map<String, Boolean>, needsLan: Boolean, sdkInt: Int): Boolean =
        needsLan && sdkInt >= LOCAL_NETWORK_ENFORCED_SDK && results[ACCESS_LOCAL_NETWORK] == false
}

/** The attempt waiting on the system permission dialog. */
internal class PendingPermissionAttempt(val needsLan: Boolean, val proceed: () -> Unit)

/** Holds at most one waiting attempt; a newer one replaces it. */
internal class PendingPermissionSlot {
    private var pending: PendingPermissionAttempt? = null

    fun put(attempt: PendingPermissionAttempt) {
        pending = attempt
    }

    /** The waiting attempt, handed out once. */
    fun take(): PendingPermissionAttempt? = pending.also { pending = null }
}

/**
 * One gate in front of every way the app starts a connection: the Connection screen's cards and
 * form, Home's Connect, and a scanned QR code (3.0 comb, qr-home-no-perms). Each runs its attempt
 * through [connect], which asks for whatever is missing first and resumes the attempt itself once
 * the answer comes back - so the attempt carries its own PIN and target instead of re-reading a
 * form that may have changed.
 */
class ConnectPermissionGate internal constructor(
    private val context: Context,
    private val slot: PendingPermissionSlot,
    private val request: (Array<String>) -> Unit,
) {
    /** Runs [attempt] once the permissions connecting to [host] needs are granted. */
    fun connect(host: String, attempt: () -> Unit) {
        val needsLan = TransportTrust.requiresLocalNetworkAccess(host.trim())
        run(ConnectPermissions.forConnect(needsLan, Build.VERSION.SDK_INT), needsLan, attempt)
    }

    /** Runs [attempt] once local-network discovery is allowed. */
    fun discover(attempt: () -> Unit) {
        run(ConnectPermissions.forDiscovery(Build.VERSION.SDK_INT), needsLan = true, attempt)
    }

    private fun run(permissions: List<String>, needsLan: Boolean, attempt: () -> Unit) {
        val missing =
            permissions.any {
                ContextCompat.checkSelfPermission(context, it) != PackageManager.PERMISSION_GRANTED
            }
        if (!missing) {
            attempt()
            return
        }
        slot.put(PendingPermissionAttempt(needsLan, attempt))
        request(permissions.toTypedArray())
    }
}

/**
 * The [ConnectPermissionGate] for this screen.
 *
 * [onLocalNetworkRefused] runs instead of the attempt when the local-network permission was refused
 * where it is needed; `permanently` is true when Android will not ask again, so only the app's
 * settings page can change it.
 */
@Composable
fun rememberConnectPermissionGate(onLocalNetworkRefused: (permanently: Boolean) -> Unit): ConnectPermissionGate {
    val context = LocalContext.current
    val onRefused by rememberUpdatedState(onLocalNetworkRefused)
    val slot = remember { PendingPermissionSlot() }
    val launcher =
        rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { results ->
            val attempt = slot.take() ?: return@rememberLauncherForActivityResult
            if (ConnectPermissions.isLocalNetworkRefused(results, attempt.needsLan, Build.VERSION.SDK_INT)) {
                // Straight after a refusal, "no rationale" means Android has stopped asking.
                val activity = context.findActivity()
                val permanently =
                    activity != null &&
                        !ActivityCompat.shouldShowRequestPermissionRationale(
                            activity,
                            ConnectPermissions.ACCESS_LOCAL_NETWORK
                        )
                onRefused(permanently)
                return@rememberLauncherForActivityResult
            }
            attempt.proceed()
        }
    return remember(context, slot, launcher) { ConnectPermissionGate(context, slot) { launcher.launch(it) } }
}

/** Opens this app's page in the system settings, where a permission Android stopped asking for lives. */
fun openAppPermissionSettings(context: Context) {
    val intent =
        Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    runCatching { context.startActivity(intent) }
        .onFailure { android.util.Log.w("ConnectPermissionGate", "No app settings screen on this phone", it) }
}
