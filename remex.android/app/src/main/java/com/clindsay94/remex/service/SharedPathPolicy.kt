package com.clindsay94.remex.service

/**
 * Decides which root ids and relative paths the PC may name when it asks this phone for a file
 * (RemEx-xt0af).
 *
 * WHY THIS EXISTS NOW. The PC's File Transfer screen can browse this phone, so every request the
 * phone's file host serves can now be driven from the PC. Both resolvers that turn a PC-supplied
 * `rootId` into a SAF document used to call `DocumentFile.fromTreeUri(Uri.parse(rootId))` on ANY
 * string: what kept them inside the shared folders was only that the app happens to hold no other
 * persisted tree grants. That is a fact about today's grants, not a rule, and it stops holding the
 * moment a grant outlives the setting that created it (whole-device browsing turned off, with its
 * persisted URI permission still on the device). The person's "Access from your PC" settings are the
 * consent surface, so the resolvers must check against those settings, not against whatever Android
 * would happen to let them open.
 *
 * Pure and Android-free on purpose, so the rules are unit-tested directly.
 */
object SharedPathPolicy {
    /** The longest single name SAF providers accept (NAME_MAX on ext4/f2fs). */
    const val MAX_SEGMENT_LENGTH = 255

    /** True only when [rootId] is exactly one of the roots the person currently shares. */
    fun isAllowedRoot(rootId: String?, allowedRootIds: Collection<String>): Boolean =
        !rootId.isNullOrBlank() && allowedRootIds.contains(rootId)

    /**
     * Splits a '/'-separated path under a shared root into its names, or returns null when the path
     * must be refused. An empty (or all-slash) path is the root itself and yields an empty list.
     *
     * One leading and trailing '/' run is trimmed, because a root-relative path can legitimately be
     * written either way. After that, every name must be real: an empty name (`a//b`), `.`, `..`, a
     * name containing a backslash or NUL, or one longer than [MAX_SEGMENT_LENGTH] refuses the whole
     * path. `findFile("..")` does not walk up today, but a resolver whose safety depends on what a
     * provider does with a name it should never have been given is not a resolver with a rule.
     */
    fun segments(relativePath: String?): List<String>? {
        val trimmed = (relativePath ?: "").trim('/')
        if (trimmed.isEmpty()) return emptyList()
        val parts = trimmed.split('/')
        for (part in parts) {
            if (!isSafeName(part)) return null
        }
        return parts
    }

    /**
     * The names to walk for a legacy v2 transfer under [rootId], or null when the request must be
     * refused: the root is not one of [sharedRootIds] (the shared folders only — v2 predates
     * whole-device browsing), or [relativePath] fails [segments].
     */
    fun legacySegments(rootId: String?, relativePath: String?, sharedRootIds: Collection<String>): List<String>? {
        if (!isAllowedRoot(rootId, sharedRootIds)) return null
        return segments(relativePath)
    }

    /** True when [name] can be one name in a path under a shared root. */
    fun isSafeName(name: String): Boolean =
        name.isNotBlank() &&
            name != "." &&
            name != ".." &&
            name.length <= MAX_SEGMENT_LENGTH &&
            !name.contains('\\') &&
            !name.contains('/') &&
            name.indexOf('\u0000') < 0
}
