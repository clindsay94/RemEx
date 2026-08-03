package com.clindsay94.remex.service

import org.json.JSONArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the MINT side of the push consent binding (RemEx-tutz).
 *
 * `FileHostHandlerTest` proves the handler refuses a grant used for the wrong file, but every test
 * there builds the id→name map by hand. Nothing exercised the loop that actually produces it, and
 * its failure modes are invisible from outside: read the wrong JSON key and every name is `""`; read
 * a fixed index and every id carries the first file's name. Either turns the name check into
 * theatre — and worse, refuses EVERY push — while the whole suite stays green.
 */
class PushConsentRegistryTest {

    private fun files(vararg names: String) =
        JSONArray().apply {
            names.forEach { put(org.json.JSONObject().apply { put("name", it); put("size", 1) }) }
        }

    @Test
    fun `each minted id is bound to its own file, in order`() {
        val minted = mintPushGrants(files("cat.jpg", "dog.jpg", "bird.jpg"))

        // THE ALIGNMENT ASSERTION. A single-file offer cannot tell "bound to its own name" apart from
        // "bound to whatever name happened to be first", so it takes three to say anything.
        assertEquals(listOf("cat.jpg", "dog.jpg", "bird.jpg"), minted.values.toList())
    }

    @Test
    fun `ids are unique, one per offered file`() {
        val minted = mintPushGrants(files("a.txt", "b.txt", "c.txt", "d.txt"))

        assertEquals(4, minted.size)
        assertEquals("ids must be distinct, or two files share one grant", 4, minted.keys.toSet().size)
        assertTrue("ids should not be blank", minted.keys.none { it.isBlank() })
    }

    @Test
    fun `an entry with no usable name binds nothing that can be used`() {
        // The PC validates names before offering, so this is a malformed peer rather than a normal
        // case. It must fail closed: a blank binding is refused by grant, and even if it were stored
        // only an offer carrying a blank fileName could match it — which beginHostReceive rejects.
        val malformed = JSONArray().apply { put(org.json.JSONObject()); put("not-an-object") }
        val minted = mintPushGrants(malformed)

        assertEquals("an id is still minted per array slot, to keep index alignment", 2, minted.size)
        assertTrue(minted.values.all { it.isEmpty() })

        val registry = PushConsentRegistry()
        registry.grant(minted)
        assertEquals("blank names must not occupy the registry", 0, registry.size)
    }

    @Test
    fun `an empty offer mints nothing`() {
        assertTrue(mintPushGrants(JSONArray()).isEmpty())
    }

    @Test
    fun `a grant authorises its own file and refuses another`() {
        val registry = PushConsentRegistry()
        registry.grant(mapOf("id-1" to "cat.jpg"))

        assertTrue(registry.isGrantedFor("id-1", "cat.jpg"))
        assertFalse("the whole point of the bead", registry.isGrantedFor("id-1", "resume.pdf"))
        assertFalse("an id nobody granted", registry.isGrantedFor("id-2", "cat.jpg"))
    }

    @Test
    fun `matching is exact`() {
        val registry = PushConsentRegistry()
        registry.grant(mapOf("id-1" to "Photo.JPG"))

        // Case and whitespace cannot legitimately differ - both copies come from one PC-side local
        // passed unmodified to both messages - so any difference is a crafted offer, not a quirk.
        assertFalse(registry.isGrantedFor("id-1", "photo.jpg"))
        assertFalse(registry.isGrantedFor("id-1", "Photo.JPG "))
    }

    @Test
    fun `the oldest grant is evicted first at capacity`() {
        val registry = PushConsentRegistry(capacity = 2)
        registry.grant(mapOf("old" to "a.txt"))
        registry.grant(mapOf("mid" to "b.txt"))
        registry.grant(mapOf("new" to "c.txt"))

        assertEquals(2, registry.size)
        assertFalse("the oldest should go", registry.isGrantedFor("old", "a.txt"))
        assertTrue(registry.isGrantedFor("mid", "b.txt"))
        assertTrue(registry.isGrantedFor("new", "c.txt"))
    }

    @Test
    fun `releasing a grant withdraws it`() {
        val registry = PushConsentRegistry()
        registry.grant(mapOf("id-1" to "cat.jpg"))
        registry.release("id-1")

        assertFalse(registry.isGrantedFor("id-1", "cat.jpg"))
        // Idempotent: handleComplete and the decline paths can both release the same id.
        registry.release("id-1")
    }
}
