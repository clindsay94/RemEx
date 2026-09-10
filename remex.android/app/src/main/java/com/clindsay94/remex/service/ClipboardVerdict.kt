package com.clindsay94.remex.service

import org.json.JSONObject

/**
 * What the shared validator said about a clipboard payload (RemEx-hgqs).
 *
 * [MaxKilobytes] rides on [TooLarge] because the limit is the half of that refusal a person can act
 * on — and because reporting the payload's own size back would be reporting a measurement of their
 * private content.
 */
sealed interface ClipboardVerdict {
    data object Sendable : ClipboardVerdict

    data object Empty : ClipboardVerdict

    data class TooLarge(val maxKilobytes: Int) : ClipboardVerdict

    /** The validator could not answer. **Never send on this.** */
    data object Unavailable : ClipboardVerdict
}

/**
 * Parses the JSON from `ValidateClipboardNative` into a verdict, **failing closed** (RemEx-hgqs).
 *
 * **THE DEFAULT IS REFUSE, AND THE FIRST VERSION OF THIS GOT IT BACKWARDS.** It matched `"empty"`
 * and `"too_large"` and let everything else fall through to the send — so an unrecognised reason, a
 * missing `reason` key, or malformed JSON all sent the payload and told the user it had worked. The
 * vocabulary is four wide, not three: the C# export returns `{"reason":"unavailable",...}` when it
 * throws, and that value existed *specifically* so the phone could recognise a failed validation.
 * Falling through on it defeated the guard rail in the same change that added it.
 *
 * The wire vocabulary is a closed set owned by another language. The only safe default for a value
 * this side does not recognise is to refuse, because the alternative sends an unbounded payload
 * having checked nothing.
 *
 * Extracted from the view model so it can be tested at all: it is pure string and JSON work with no
 * Android dependency, and the fail-open bug above is exactly what one table-driven test catches.
 */
fun clipboardVerdictOf(verdictJson: String?): ClipboardVerdict {
    val json =
            runCatching { JSONObject(verdictJson.orEmpty()) }.getOrNull()
                    ?: return ClipboardVerdict.Unavailable

    return when (json.optString("reason")) {
        "none" -> ClipboardVerdict.Sendable
        "empty" -> ClipboardVerdict.Empty
        "too_large" -> {
            // A limit of "0 KB" is worse than naming no limit at all, so an absent or nonsensical
            // maxBytes is treated as the validator having failed rather than as a limit of zero.
            val kb = json.optInt("maxBytes", 0) / 1024
            if (kb > 0) ClipboardVerdict.TooLarge(kb) else ClipboardVerdict.Unavailable
        }
        else -> ClipboardVerdict.Unavailable
    }
}
