package com.clindsay94.remex.ui

/**
 * The arguments a pairing navigation carries, validated rather than defaulted (RemEx-667p).
 *
 * **THE DEFECT THIS REPLACES IS A PAIR OF SILENT FALLBACKS.** `AppNavigation` reads
 * `arguments?.getString("host") ?: ""` and `?: 5005`, so a malformed navigate lands the user on a
 * pairing screen with an empty host and a made-up port. Nothing fails, nothing is logged, and the
 * screen simply cannot work — the user sees a pairing attempt that times out for no stated reason,
 * which is indistinguishable from their PC being off.
 *
 * A route argument is untrusted input in the same sense a wire field is: it can arrive from a deep
 * link, a saved back stack, or a caller that concatenated it wrongly. So this parses to a result
 * rather than substituting a value.
 */
sealed interface PairingRouteResult {
    /** The route named a usable host and port. */
    data class Valid(val host: String, val port: Int) : PairingRouteResult

    /** The route could not be used, with a developer-facing reason. */
    data class Invalid(val reason: String) : PairingRouteResult
}

object PairingRouteArgs {

    /** Default the app connects on, used when a caller genuinely has no port to offer. */
    const val DefaultPort: Int = 5005

    /**
     * Parses raw route arguments.
     *
     * @param host the host segment, as it came out of the route.
     * @param port the port segment, as text, because that is what a route carries.
     */
    fun parse(host: String?, port: String?): PairingRouteResult {
        val trimmedHost = host?.trim()

        // AN EMPTY HOST IS REFUSED, NOT ACCEPTED AS "". That substitution is the whole bug: it
        // produces a screen that looks operational and cannot possibly succeed.
        if (trimmedHost.isNullOrEmpty()) {
            return PairingRouteResult.Invalid("pairing route carried no host")
        }

        // A HOST CANNOT CONTAIN A PATH SEPARATOR, because the route is "pairing/{host}/{port}" and
        // the host occupies ONE path segment. A slash does not merely look wrong - it shifts every
        // segment after it, so the port is read from the middle of the host and the route stops
        // matching at all. This is the same reasoning as refusing a separator in a file name.
        if (trimmedHost.contains('/') || trimmedHost.contains('\\')) {
            return PairingRouteResult.Invalid("pairing host contains a path separator")
        }

        if (port.isNullOrBlank()) {
            return PairingRouteResult.Invalid("pairing route carried no port")
        }

        // toIntOrNull rather than toInt: a non-numeric segment is a routing bug, not an exception
        // to propagate out of a navigation callback where nothing can catch it.
        val parsedPort = port.trim().toIntOrNull()
            ?: return PairingRouteResult.Invalid("pairing port is not a number: $port")

        // OUT OF RANGE IS REFUSED RATHER THAN CLAMPED. Substituting 5005 for a port the caller got
        // wrong hides the caller's bug and connects the user somewhere they did not ask for - which
        // on a pairing screen means offering a PIN to the wrong machine.
        if (parsedPort !in 1..65535) {
            return PairingRouteResult.Invalid("pairing port out of range: $parsedPort")
        }

        return PairingRouteResult.Valid(trimmedHost, parsedPort)
    }

    /**
     * Builds the route path for a pairing navigation.
     *
     * @return the path, or null when the arguments would not survive the round trip.
     * @remarks
     * Returning null rather than a best-effort string means a caller cannot navigate somewhere that
     * will fail on arrival — the check happens where the caller can still do something about it,
     * rather than on the screen that receives it.
     */
    fun buildPath(routePrefix: String, host: String, port: Int): String? =
        when (parse(host, port.toString())) {
            is PairingRouteResult.Valid -> "$routePrefix/${host.trim()}/$port"
            is PairingRouteResult.Invalid -> null
        }
}
