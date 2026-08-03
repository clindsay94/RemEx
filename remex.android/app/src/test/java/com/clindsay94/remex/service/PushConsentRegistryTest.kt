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

    /**
     * The shape the CHANGELOG promises: eight real camera photos, all named.
     *
     * Uses a genuine filename shape (Pixel's IMG_yyyyMMdd_HHmmss.jpg, 23 characters), not a short
     * synthetic one. An earlier version used "photo-1.jpg" at 11 characters, which passed at a budget
     * of 110 and so pinned nothing about whether 240 is right — it would have stayed green while the
     * promise it exists to protect quietly broke.
     */
    @Test
    fun `an ordinary eight-photo share names every file`() {
        val eight = (1..8).map { "IMG_20260801_1435%02d.jpg".format(it) }
        eight.forEach { assertEquals(23, it.length) }

        val joined = joinOfferedNames(eight)

        eight.forEach { assertTrue("$it should be named, got: $joined", it in joined) }
        assertFalse("nothing was hidden, so there is nothing to count", "+" in joined)
    }

    /**
     * A blank name is shown as an empty entry, not silently dropped.
     *
     * Load-bearing for the cross-platform equivalence: optString("name") yields "" for a missing or
     * JSON-null name, and the PC must render that the same way. It is also the case a later tidy-up
     * is most likely to "fix" on one side only.
     */
    @Test
    fun `a blank name is an empty entry rather than a disappearance`() {
        assertEquals("a.txt, , b.txt", joinOfferedNames(listOf("a.txt", "", "b.txt")))
    }

    /**
     * The budget itself, so a change here fails HERE with a message naming the other platform —
     * rather than surfacing as "expected 9, actual 11" in the overflow tests, which tells the next
     * person nothing about the C# constant they also have to move.
     */
    @Test
    fun `the name budget matches the PC's`() {
        assertEquals(
            "OFFERED_NAMES_BUDGET must equal FileTransferHandler.OfferedNamesBudget in " +
                "remex.agent, or the two ends of one protocol describe the same offer differently",
            240,
            OFFERED_NAMES_BUDGET,
        )
    }

    @Test
    fun `an offer too long to show states how many are hidden`() {
        // Long names blow the character budget after a handful, which is the point: the budget is on
        // what a person can actually read, not on an arbitrary file count.
        val many = (1..40).map { "a-rather-long-holiday-photo-file-name-$it.jpeg" }

        val joined = joinOfferedNames(many)

        assertTrue("the remainder must be a number, not a bare ellipsis: $joined", Regex(", \\+\\d+$").containsMatchIn(joined))
        assertFalse("the old unquantified elision should be gone", "…" in joined)

        // The count must be exactly what was left out, or it is a new way of misleading.
        val shown = many.count { it in joined }
        val claimed = Regex(", \\+(\\d+)$").find(joined)!!.groupValues[1].toInt()
        assertEquals(many.size, shown + claimed)
    }

    @Test
    fun `a single name is never truncated, however long`() {
        // A half-written file name is worse than a long one: the user cannot tell what they are
        // approving, and the name is exactly what the grant binds.
        val monster = "x".repeat(OFFERED_NAMES_BUDGET * 2) + ".bin"

        assertEquals(monster, joinOfferedNames(listOf(monster)))
    }

    @Test
    fun `an empty offer describes nothing`() {
        assertEquals("", joinOfferedNames(emptyList()))
    }

    /**
     * The exact text, so the phone and the PC cannot drift apart silently.
     *
     * `OfferedNamesDescriptionTests` asserts these identical strings from the C# side. Both describe
     * ONE protocol to two people, and nothing else in either suite would notice a divergence —
     * each half only ever checks its own output against its own expectations.
     */
    @Test
    fun `the exact same text as the PC produces`() {
        val name = "a".repeat(25)
        assertEquals("$name, $name", joinOfferedNames(listOf(name, name)))
    }

    @Test
    fun `the overflow text is exactly what the PC produces`() {
        // Ten 25-character names: nine fit inside the 240-character budget (241 once the separators
        // are counted), the tenth does not, and the remainder is stated as a number.
        val names = List(10) { "b".repeat(25) }

        val joined = joinOfferedNames(names)

        assertTrue("expected a trailing count, got: $joined", joined.endsWith(", +1"))
        assertEquals(9, joined.split(", ").count { it.length == 25 })
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
