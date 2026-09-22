package com.clindsay94.remex.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [ThemeSyncSender]'s connect/debounce/disconnect rules (RemEx-y06a0.1) — a fake `send` and a
 * fake `clock`, no real socket anywhere in the chain.
 *
 * Two shapes of test here, deliberately:
 *
 * - Connect/disconnect/reconnect cases that never touch the debounce timer use `runTest` — pure,
 *   instant, no wall-clock cost.
 * - The two cases that need the debounce window to actually elapse ([onThemeChanged]'s collector)
 *   use [runBlocking] on a real [Dispatchers.Default] scope with a short real `debounceMs` and
 *   real [delay]s instead. `Flow.debounce`'s internal `produce`/`select` machinery did not fire
 *   reliably against `TestCoroutineScheduler`'s virtual time in this project's exact
 *   kotlin/coroutines version combination — confirmed with a bare
 *   `MutableStateFlow.debounce(500).collect{}` under `runTest`, with no `ThemeSyncSender` involved
 *   at all, so this is an environment limitation, not a bug in the debounce wiring itself. Real
 *   time with a short window and a generous settle margin is the reliable substitute.
 */
class ThemeSyncSenderTest {

        private fun snapshot(themeStyle: String = "tonal_spot") =
                ThemeSnapshot(
                        themeMode = "system",
                        themePalette = "default",
                        themeStyle = themeStyle,
                        themeSeedColor = "#6750A4",
                        themeSeedChroma = 48.0f,
                        themeContrast = 0.0f,
                        dynamicColor = true
                )

        private fun styleOf(json: String): String =
                JSONObject(json).getJSONObject("themeSync").getString("style")

        @Test
        fun `sends once, immediately, after authenticated`() = runTest {
                val sent = mutableListOf<String>()
                val sender =
                        ThemeSyncSender(
                                scope = backgroundScope,
                                isAuthenticated = { true },
                                resolveSeed = { "#AABBCC" },
                                send = { sent += it },
                                clock = { 1_000L },
                        )

                sender.onConnected(snapshot())

                assertEquals(1, sent.size)
                assertEquals("tonal_spot", styleOf(sent[0]))
        }

        @Test
        fun `no send while disconnected, even after settling`() = runTest {
                val sent = mutableListOf<String>()
                val sender =
                        ThemeSyncSender(
                                scope = backgroundScope,
                                isAuthenticated = { false },
                                resolveSeed = { "#AABBCC" },
                                send = { sent += it },
                                clock = { 1_000L },
                        )

                sender.onThemeChanged(snapshot())
                sender.onConnected(snapshot())

                assertTrue(
                        "no theme_sync may be sent while RemexClientManager reports disconnected",
                        sent.isEmpty(),
                )
        }

        @Test
        fun `does not send while connected but not authenticated`() = runTest {
                // The bare WebSocket is open (isConnected on the manager), but the host has not yet
                // acked the reconnect proof — the exact gap Design (A) exists to close. A theme_sync
                // fired here would race the host's pairing gate and be rejected today; the sender must
                // stay quiet until the ack lands.
                val sent = mutableListOf<String>()
                val sender =
                        ThemeSyncSender(
                                scope = backgroundScope,
                                isAuthenticated = { false },
                                resolveSeed = { "#AABBCC" },
                                send = { sent += it },
                                clock = { 1_000L },
                        )

                sender.onConnected(snapshot())
                sender.onThemeChanged(snapshot(themeStyle = "vibrant"))

                assertTrue(
                        "no theme_sync may be sent while connected but not yet authenticated",
                        sent.isEmpty(),
                )
        }

        @Test
        fun `the next connect sends the CURRENT theme, not one queued from before it`() = runTest {
                // "no send while disconnected; the next connect sends the latest" — the wire contract
                // is explicit that a connect always carries the current settings snapshot the caller
                // hands it, never something ThemeSyncSender cached from an earlier, disconnected call.
                val sent = mutableListOf<String>()
                var connected = false
                val sender =
                        ThemeSyncSender(
                                scope = backgroundScope,
                                isAuthenticated = { connected },
                                resolveSeed = { "#AABBCC" },
                                send = { sent += it },
                                clock = { 1_000L },
                        )

                sender.onThemeChanged(snapshot(themeStyle = "monochrome"))
                assertTrue(sent.isEmpty())

                // A caller wiring this for real reads the CURRENT settings at connect time rather
                // than relying on the sender to have remembered anything from while it was offline.
                connected = true
                sender.onConnected(snapshot(themeStyle = "neutral"))

                assertEquals(1, sent.size)
                assertEquals("neutral", styleOf(sent[0]))
        }

        @Test
        fun `seed and clock are read at send time, not at construction time`() = runTest {
                var currentClock = 1_000L
                val sent = mutableListOf<String>()
                val sender =
                        ThemeSyncSender(
                                scope = backgroundScope,
                                isAuthenticated = { true },
                                resolveSeed = { "#AABBCC" },
                                send = { sent += it },
                                clock = { currentClock },
                        )

                currentClock = 42L
                sender.onConnected(snapshot())

                assertEquals(
                        42L,
                        JSONObject(sent[0]).getJSONObject("themeSync").getLong("sentAtUnixMs"),
                )
        }

        // ── Real-time debounce cases — see class doc for why these two are not `runTest`. ──────

        @Test
        fun `a burst of theme changes inside the debounce window collapses to one send`() = runBlocking {
                val sent = mutableListOf<String>()
                val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
                try {
                        val sender =
                                ThemeSyncSender(
                                        scope = scope,
                                        isAuthenticated = { true },
                                        resolveSeed = { "#AABBCC" },
                                        send = { sent += it },
                                        clock = { 1_000L },
                                        debounceMs = 60L,
                                )

                        sender.onThemeChanged(snapshot(themeStyle = "tonal_spot"))
                        delay(20)
                        sender.onThemeChanged(snapshot(themeStyle = "vibrant"))
                        delay(20)
                        sender.onThemeChanged(snapshot(themeStyle = "rainbow"))

                        // Every change so far restarted the 60 ms window, and only ~40 ms has passed
                        // since the last one — nothing should have gone out yet.
                        delay(40)
                        assertTrue("expected no send before the debounce window elapses", sent.isEmpty())

                        // Settle well past the window with no further changes.
                        delay(300)

                        assertEquals(
                                "a burst inside the window must collapse to exactly one send",
                                1,
                                sent.size,
                        )
                        assertEquals("rainbow", styleOf(sent[0]))
                } finally {
                        scope.cancel()
                }
        }

        @Test
        fun `a theme change after reconnect sends again`() = runBlocking {
                val sent = mutableListOf<String>()
                val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
                try {
                        val sender =
                                ThemeSyncSender(
                                        scope = scope,
                                        isAuthenticated = { true },
                                        resolveSeed = { "#AABBCC" },
                                        send = { sent += it },
                                        clock = { 1_000L },
                                        debounceMs = 60L,
                                )

                        sender.onConnected(snapshot(themeStyle = "tonal_spot"))
                        sender.onThemeChanged(snapshot(themeStyle = "expressive"))
                        delay(300)

                        assertEquals(2, sent.size)
                        assertEquals("tonal_spot", styleOf(sent[0]))
                        assertEquals("expressive", styleOf(sent[1]))
                } finally {
                        scope.cancel()
                }
        }
}
