package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineRun
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** The phone store document and its sealed storage (spec §6.7, §6.8; RemEx-pp0rt.5). */
class RoutineStoreDocumentTest {

    @Test
    fun `a document round-trips with its routines, pause flag, home and sync state`() {
        val doc =
            RoutineStoreDocument(
                pausedAll = true,
                routines = listOf(manualRoutine(lock()).copy(revision = 3)),
                // Through JSONObject first, so the key order matches what the decoder re-serialises.
                homeJson = JSONObject("""{"id":"b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11","gateways":["192.168.1.1"]}""").toString(),
                hostSync = mapOf(HOST to RoutineHostSync(localRevision = 4, ackedRevision = 2, runCursor = 17, reachableAwayAtUnixMs = 5L)),
                storeResetAtUnixMs = 9L,
            )
        val back = RoutineStoreCodec.decode(RoutineStoreCodec.encode(doc))
        assertEquals(doc, back)
    }

    @Test
    fun `unknown keys at the top level and in sync entries are written back unchanged`() {
        val text =
            """{"schemaVersion":1,"pausedAll":false,"routines":[],"futureKey":{"a":[1,2]},
               "hostSync":{"$HOST":{"localRevision":1,"ackedRevision":1,"runCursor":0,"laterField":"x"}}}"""
        val doc = checkNotNull(RoutineStoreCodec.decode(text))
        val written = JSONObject(RoutineStoreCodec.encode(doc))
        assertEquals(2, written.getJSONObject("futureKey").getJSONArray("a").getInt(1))
        assertEquals("x", written.getJSONObject("hostSync").getJSONObject(HOST).getString("laterField"))
    }

    @Test
    fun `a malformed routine keeps its original JSON through a store round trip`() {
        val raw = """{"id":"6b86b273-ff34-4ce1-ad6b-804eff5a3f57","name":17,"steps":[]}"""
        val text = """{"schemaVersion":1,"pausedAll":false,"routines":[$raw]}"""
        val doc = checkNotNull(RoutineStoreCodec.decode(text))
        assertTrue(doc.routines.single().isMalformed)
        val routineBack = JSONObject(RoutineStoreCodec.encode(doc)).getJSONArray("routines").getJSONObject(0)
        assertEquals(17, routineBack.getInt("name"))
    }

    @Test
    fun `a document that does not parse, or holds a known key of the wrong type, is unreadable rather than defaulted`() {
        assertNull(RoutineStoreCodec.decode("not json"))
        assertNull(RoutineStoreCodec.decode("""{"schemaVersion":1,"pausedAll":"yes","routines":[]}"""))
        assertNull(RoutineStoreCodec.decode("""{"schemaVersion":1,"routines":{}}"""))
        assertNull(RoutineStoreCodec.decode("""{"schemaVersion":1,"hostSync":{"$HOST":{"localRevision":"1"}}}"""))
    }

    @Test
    fun `a newer document is flagged read-only`() {
        val doc = checkNotNull(RoutineStoreCodec.decode("""{"schemaVersion":2,"pausedAll":false,"routines":[]}"""))
        assertTrue(doc.isNewerThanReader)
    }

    @Test
    fun `the store seals the document under its associated data and refuses one it cannot open`() =
        runTest {
            val kv = FakeKeyValueStore()
            val store = RoutineDocumentStore(kv, FakeCipherSource())
            assertEquals(RoutineDocumentLoad.Empty, store.load())

            store.save(RoutineStoreDocument(pausedAll = true))
            val sealed = checkNotNull(kv.map[RoutineDocumentStore.KEY])
            assertFalse("the plain text must not be stored", sealed.contains("pausedAll"))
            assertTrue((store.load() as RoutineDocumentLoad.Loaded).document.pausedAll)

            // Sealed under other associated data: an AEAD would refuse it, and so must the store.
            kv.map[RoutineDocumentStore.KEY] = FakeCipher().seal("{}", "remex_routines/other")
            assertEquals(RoutineDocumentLoad.Unreadable(null), store.load())

            // Opens, but is not a document: the text is offered for "Export raw".
            kv.map[RoutineDocumentStore.KEY] = FakeCipher().seal("garbage", RoutineDocumentStore.ASSOCIATED_DATA)
            assertEquals(RoutineDocumentLoad.Unreadable("garbage"), store.load())
        }

    @Test
    fun `a document over the size cap is refused, never truncated`() =
        runTest {
            val store = RoutineDocumentStore(FakeKeyValueStore(), FakeCipherSource())
            val huge = RoutineStoreDocument(homeJson = JSONObject().put("pad", "x".repeat(RoutineDocumentStore.MAX_BYTES)).toString())
            try {
                store.save(huge)
                fail("expected the cap to refuse the write")
            } catch (_: RoutineDocumentTooLargeException) {
            }
        }

    @Test
    fun `history is stored per routine and an unreadable blob is skipped`() =
        runTest {
            val kv = FakeKeyValueStore()
            val store = RoutineHistoryStore(kv, FakeCipherSource())
            val run = RoutineRun(runId = uuid(), routineId = "r1", outcome = "succeeded", triggeredAtUnixMs = 5)
            store.write("r1", listOf(run))
            store.write("r2", listOf(run.copy(routineId = "r2")))
            kv.map["run/r3"] = "not sealed"
            val all = store.loadAll()
            assertEquals(setOf("r1", "r2"), all.keys)
            assertEquals(RoutineJson.write(run).toString(), RoutineJson.write(all.getValue("r1").single()).toString())

            store.write("r1", emptyList())
            assertFalse(kv.map.containsKey("run/r1"))
        }
}
