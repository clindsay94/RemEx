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

    /** Files whose sizes differ from each other, so a size read from the wrong entry shows up. */
    private fun files(vararg names: String) =
        JSONArray().apply {
            names.forEachIndexed { i, name ->
                put(org.json.JSONObject().apply { put("name", name); put("size", 100L + i) })
            }
        }

    @Test
    fun `each minted id is bound to its own file, in order`() {
        val minted = mintPushGrants(files("cat.jpg", "dog.jpg", "bird.jpg"))

        // THE ALIGNMENT ASSERTION. A single-file offer cannot tell "bound to its own entry" apart
        // from "bound to whatever entry happened to be first", so it takes three to say anything —
        // and the sizes differ per entry so a name read from index i with a size from index 0 fails
        // here rather than passing on the name alone.
        assertEquals(
            listOf(
                GrantedFile("cat.jpg", 100L),
                GrantedFile("dog.jpg", 101L),
                GrantedFile("bird.jpg", 102L),
            ),
            minted.values.toList(),
        )
    }

    @Test
    fun `a grant refuses its own file at a different size`() {
        // The other half of RemEx-ccqb. The prompt shows a size as well as a name, so a PC that
        // offers holiday.jpg at 1 KB and then negotiates the same id and name carrying five
        // gigabytes is sending something the user was never asked about.
        val registry = PushConsentRegistry()
        registry.grant(mapOf("id-1" to GrantedFile("holiday.jpg", 1024)))

        assertTrue(registry.isGrantedFor("id-1", "holiday.jpg", 1024))
        assertFalse(registry.isGrantedFor("id-1", "holiday.jpg", 5_368_709_120L))
        assertFalse("smaller is still not what was agreed", registry.isGrantedFor("id-1", "holiday.jpg", 0))
    }

    @Test
    fun `an offer that states no size cannot be matched at all`() {
        // optLong's default is -1 here rather than 0, so an absent size fails closed: a real transfer
        // offer carries a non-negative size, and 0 is a legitimate one (an empty file).
        val noSize = JSONArray().apply { put(org.json.JSONObject().apply { put("name", "a.txt") }) }
        val minted = mintPushGrants(noSize)

        assertEquals(-1L, minted.values.single().size)

        val registry = PushConsentRegistry()
        registry.grant(minted)
        val id = minted.keys.single()
        assertFalse(registry.isGrantedFor(id, "a.txt", 0))
        assertFalse(registry.isGrantedFor(id, "a.txt", 100))

        // THE ASSERTION THE SENTINEL'S OWN ARGUMENT IMPLIES, and it failed before the -1 grants were
        // dropped: remex.core declares Size as a bare long with no validator, so a peer can simply
        // STATE -1 in both messages and match a sentinel that was only ever meant to mean "absent".
        assertFalse("a stated -1 must not match an unstated one", registry.isGrantedFor(id, "a.txt", -1))
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
        assertTrue(minted.values.all { it.name.isEmpty() })

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
        registry.grant(mapOf("id-1" to GrantedFile("cat.jpg", 1024)))

        assertTrue(registry.isGrantedFor("id-1", "cat.jpg", 1024))
        assertFalse("the whole point of the bead", registry.isGrantedFor("id-1", "resume.pdf", 1024))
        assertFalse("an id nobody granted", registry.isGrantedFor("id-2", "cat.jpg", 1024))
    }

    @Test
    fun `matching is exact`() {
        val registry = PushConsentRegistry()
        registry.grant(mapOf("id-1" to GrantedFile("Photo.JPG", 1024)))

        // Case and whitespace cannot legitimately differ - both copies come from one PC-side local
        // passed unmodified to both messages - so any difference is a crafted offer, not a quirk.
        assertFalse(registry.isGrantedFor("id-1", "photo.jpg", 1024))
        assertFalse(registry.isGrantedFor("id-1", "Photo.JPG ", 1024))
    }

    @Test
    fun `the oldest grant is evicted first at capacity`() {
        val registry = PushConsentRegistry(capacity = 2)
        registry.grant(mapOf("old" to GrantedFile("a.txt", 1)))
        registry.grant(mapOf("mid" to GrantedFile("b.txt", 1)))
        registry.grant(mapOf("new" to GrantedFile("c.txt", 1)))

        assertEquals(2, registry.size)
        assertFalse("the oldest should go", registry.isGrantedFor("old", "a.txt", 1))
        assertTrue(registry.isGrantedFor("mid", "b.txt", 1))
        assertTrue(registry.isGrantedFor("new", "c.txt", 1))
    }

    @Test
    fun `releasing a grant withdraws it`() {
        val registry = PushConsentRegistry()
        registry.grant(mapOf("id-1" to GrantedFile("cat.jpg", 1024)))
        registry.release("id-1")

        assertFalse(registry.isGrantedFor("id-1", "cat.jpg", 1024))
        // Idempotent: handleComplete and the decline paths can both release the same id.
        registry.release("id-1")
    }
}
