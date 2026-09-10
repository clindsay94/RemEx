package com.clindsay94.remex

import android.content.Context
import android.os.Build
import android.provider.Settings
import android.util.Log

/**
 * What this phone calls itself when it pairs with a PC (RemEx-8m3r).
 *
 * Every device used to send the literal `"Android Client"`. That was invisible while nothing kept
 * the name, but the PC now stores it at pairing and reads it back on every reconnect (RemEx-yzqs),
 * and a Paired Devices card is coming (RemEx-nrsv) — so with the constant, three phones in a
 * household would render as three identical rows next to three unpair buttons.
 *
 * **THE NAME CROSSES THE WIRE ONLY ON `pairing_request`,** which is sent once in a device's life. So
 * whatever is chosen here is what the PC shows until the user re-pairs. That argues for the name the
 * user already recognises rather than the most precise one.
 */
object DeviceName {

    /**
     * Last resort, and deliberately not localized.
     *
     * A brand name is not translated, and translating it would be worse than leaving it: this string
     * is produced on the phone and displayed on a PC that may be running in another language
     * entirely, so a phone-locale sentence would arrive as a foreign one. It is also very nearly
     * unreachable — [Build.MODEL] is populated on every shipping device — and exists so that
     * [forPairing] can promise never to return blank.
     */
    const val FALLBACK: String = "Android"

    /**
     * The name to send, given every source that could supply one.
     *
     * @param userChosen the device name from system settings — what the user typed.
     * @param manufacturer [Build.MANUFACTURER].
     * @param model [Build.MODEL].
     *
     * Pure so it can be tested: `Build` is a platform constant with no value under plain JUnit, and
     * this project has no Robolectric, so a function that read it directly could not be exercised at
     * all.
     *
     * **NEVER RETURNS BLANK, AND THAT IS LOAD-BEARING RATHER THAN TIDY.** `StartPairingNative`
     * rejects an empty client name with `ArgMissing` before it opens a socket, so a blank here does
     * not degrade to an unnamed device — it fails the pairing outright, and the user is told nothing
     * more useful than that something was missing.
     */
    fun choose(userChosen: String?, manufacturer: String?, model: String?): String {
        // The user's own name for the phone wins. "Connor's Pixel" is what they will look for in a
        // list on the PC; "SM-S938B" is not, even though it is the more accurate answer.
        val chosen = userChosen?.trim().orEmpty()
        if (chosen.isNotEmpty()) return chosen

        val make = manufacturer?.trim().orEmpty()
        val device = model?.trim().orEmpty()

        return when {
            device.isEmpty() && make.isEmpty() -> FALLBACK
            device.isEmpty() -> make.capitalizeMake()
            make.isEmpty() -> device
            // Samsung reports MODEL "SM-S938B" with MANUFACTURER "samsung", so joining gives
            // "Samsung SM-S938B" — but Google reports MODEL "Pixel 9 Pro" with MANUFACTURER "Google",
            // where joining would give "Google Google Pixel..." on the OEMs that already prefix it.
            // Re-cases the prefix rather than passing the model through: vivo reports
            // MANUFACTURER "vivo" with MODEL "vivo 1906", and older OnePlus reports "OnePlus" with
            // "ONEPLUS A6013". Returning those verbatim would render one brand lower-case and
            // another shouting, right beside a joined "Samsung SM-S938B" — and shouting is the exact
            // output capitalizeMake exists to avoid.
            device.startsWith(make, ignoreCase = true) ->
                make.capitalizeMake() + device.substring(make.length)
            else -> "${make.capitalizeMake()} $device"
        }
    }

    /**
     * Reads the sources this device actually has.
     *
     * Deliberately thin: everything that can be decided is decided in [choose], because this part
     * cannot be tested without a device.
     *
     * NOT capped here. The PC caps what it stores and what it serves, in one place
     * (`PairedDeviceDisplayName.MaxLength`, in remex.desktop/Services/PairedDeviceDisplayName.cs),
     * and a second cap on this side would be a copy of that number free to drift away from it.
     */
    fun forPairing(context: Context): String =
        choose(userChosenName(context), Build.MANUFACTURER, Build.MODEL)

    /**
     * The name from system settings, or null.
     *
     * Wrapped because this is a read of a global setting on an arbitrary OEM build: it is documented
     * to return null when unset, and a device that throws instead must fall back rather than fail
     * the pairing.
     */
    private fun userChosenName(context: Context): String? =
        runCatching { Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) }
            .onFailure {
                // Logged rather than swallowed silently: if a whole OEM family threw here, every
                // phone in it would quietly report its model instead of the user's name and there
                // would be nothing to explain why.
                Log.w("RemexDeviceName", "Could not read the device name from settings", it)
            }
            .getOrNull()

    /**
     * Title-cases a manufacturer for display.
     *
     * Only the first letter, and only when it is lower case: manufacturers report themselves
     * inconsistently ("samsung", "Google", "OnePlus"), and upper-casing the whole string would turn
     * "OnePlus" into "ONEPLUS" while title-casing every word would turn it into "Oneplus".
     *
     * NO LOCALE ARGUMENT, ON PURPOSE. Kotlin's no-arg [Char.titlecase] uses invariant Unicode rules;
     * the overload taking a Locale is the locale-sensitive one. That distinction is real here rather
     * than theoretical — itel is a shipping manufacturer reporting "itel", so on a Turkish-locale
     * phone the locale-aware overload would produce "İtel". Do not "fix" this by passing a locale.
     */
    private fun String.capitalizeMake(): String =
        replaceFirstChar { if (it.isLowerCase()) it.titlecase() else it.toString() }
}
