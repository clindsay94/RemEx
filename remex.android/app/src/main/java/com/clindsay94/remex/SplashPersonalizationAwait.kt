package com.clindsay94.remex

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.firstOrNull
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Waits for [flow]'s first emission, but never longer than [timeoutMs] and never lets an
 * exception from [flow] escape (RemEx-alwfa.1 review, HIGH-1): the splash's exit listener calls
 * this to get personalization before recolouring, and neither "DataStore is slow" nor "DataStore
 * threw" may hang or crash startup — both fall through to `null`, [MainActivity]'s brand-default
 * path.
 *
 * [onFailure] runs exactly once, only when [flow] itself throws (not on a plain timeout — the
 * caller distinguishes that case itself by checking whether the result came back `null`).
 */
internal suspend fun <T> awaitFirstOrNullWithTimeout(
    flow: Flow<T>,
    timeoutMs: Long,
    onFailure: (Throwable) -> Unit = {}
): T? {
    val guarded = flow.catch { e -> onFailure(e) }
    return withTimeoutOrNull(timeoutMs) { guarded.firstOrNull() }
}
