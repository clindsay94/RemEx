package com.clindsay94.remex.service

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * Exercises [FileHostHandler]'s PC-initiated JSON ops (browse / manage copy-move-mkdir /
 * root-manage / search / metadata / roots+capabilities) against in-memory fakes. Every asserted
 * type-string + field name mirrors `remex.core`, guarding against Kotlin↔C# wire drift (plan WP6).
 */
class FileHostHandlerTest {

    @get:Rule val tmp = TemporaryFolder()

    // ── In-memory fakes ───────────────────────────────────────────────────────

    private class FakeNode(
        override var name: String,
        override val isDirectory: Boolean,
        var content: ByteArray = ByteArray(0),
        val children: LinkedHashMap<String, FakeNode> = LinkedHashMap(),
        var writable: Boolean = true,
        override var mimeType: String? = null,
        var parent: FakeNode? = null,
    ) : FileNode {
        override val length: Long get() = if (isDirectory) 0L else content.size.toLong()
        override val lastModifiedMs: Long = 1000L
        override val canRead: Boolean = true
        override val canWrite: Boolean get() = writable

        override fun listChildren(): List<FileNode> = children.values.toList()

        override fun findChild(name: String): FileNode? = children[name]

        override fun createDirectory(name: String): FileNode? {
            children[name]?.let { return it }
            val n = FakeNode(name, true, parent = this)
            children[name] = n
            return n
        }

        override fun createFile(mimeType: String, name: String): FileNode? {
            val n = FakeNode(name, false, mimeType = mimeType, parent = this)
            children[name] = n
            return n
        }

        override fun delete(): Boolean {
            parent?.children?.remove(name)
            return true
        }

        override fun renameTo(name: String): Boolean {
            val p = parent ?: return false
            p.children.remove(this.name)
            this.name = name
            p.children[name] = this
            return true
        }
    }

    private class FakeFacade(
        val root: FakeNode,
        val roots: List<RootDescriptor>,
        val volumes: List<VolumeDescriptor> = emptyList(),
    ) : FileSystemFacade {
        override fun listRoots() = roots
        override fun listVolumes() = volumes
        override fun resolve(rootId: String, relativePath: String): FileNode? {
            // Unknown roots resolve to nothing. The fake used to ignore rootId and hand back the same
            // tree for anything, which made "was the right root chosen?" untestable — every id looked
            // valid. Production cannot tell either (SafFileSystemFacade just calls fromTreeUri), but a
            // fake that answers for roots the user never shared cannot show a wrong choice at all.
            if (roots.none { it.rootId == rootId }) return null

            var cur: FakeNode = root
            val trimmed = relativePath.trim('/')
            if (trimmed.isNotEmpty()) {
                for (p in trimmed.split('/')) {
                    if (p.isEmpty()) continue
                    cur = cur.children[p] ?: return null
                }
            }
            return cur
        }

        override fun openInput(node: FileNode): InputStream =
            ByteArrayInputStream((node as FakeNode).content)

        override fun openOutput(node: FileNode): OutputStream =
            object : ByteArrayOutputStream() {
                override fun close() {
                    (node as FakeNode).content = toByteArray()
                    super.close()
                }
            }

        override fun loadThumbnailJpeg(node: FileNode, maxDim: Int, maxBytes: Int): ByteArray? = null
    }

    private class FakeRoots(val granted: Boolean, val roots: List<RootDescriptor>) : SharedRootsProvider {
        override fun sharedRoots() = roots
        override fun fullBrowseVolumes(): List<VolumeDescriptor> = emptyList()
        override fun isFullBrowseGranted() = granted
    }

    private class CapturingSender : ControlMessageSender {
        val sent = mutableListOf<String>()
        override fun send(json: String) { sent.add(json) }
        fun last() = JSONObject(sent.last())
    }

    private class NoopChannel : FileFrameChannel {
        override val isOpen = false
        override fun registerSink(transferId: String, sink: FileFrameSink) {}
        override fun unregisterSink(transferId: String) {}
        override fun sendFrame(envelope: FileFrameEnvelope, payload: ByteArray) = false
    }

    private class FakeMutator : RootMutator {
        val removed = mutableListOf<String>()
        override suspend fun removeRoot(rootId: String): Boolean {
            removed.add(rootId)
            return rootId == "root1"
        }
        override suspend fun addRoot(sourceRootId: String, sourceRelativePath: String?) = false
    }

    private fun rootDescriptor() =
        RootDescriptor("root1", "Shared", true, true, true, true, false)

    /** Refusals reported to the UI layer by the handler under test (RemEx-gipu). */
    private val pushRefusals = mutableListOf<PushRefusal>()

    private fun build(
        root: FakeNode,
        granted: Boolean = false,
        pushConsent: PushConsentRegistry = PushConsentRegistry(),
        roots: List<RootDescriptor> = listOf(rootDescriptor()),
    ): Triple<FileHostHandler, CapturingSender, FakeMutator> {
        val provider = FakeRoots(granted, roots)
        val facade = FakeFacade(root, roots)
        val sender = CapturingSender()
        val mutator = FakeMutator()
        val handler =
            FileHostHandler(
                facade = facade,
                rootsProvider = provider,
                sender = sender,
                channel = NoopChannel(),
                rootMutator = mutator,
                stagingDir = tmp.newFolder("staging"),
                scope = CoroutineScope(Dispatchers.Unconfined),
                pushConsent = pushConsent,
                onPushRefused = { pushRefusals.add(it) },
            )
        return Triple(handler, sender, mutator)
    }

    private fun sampleTree(): FakeNode {
        val root = FakeNode("root1", true)
        val docs = FakeNode("Docs", true, parent = root)
        root.children["Docs"] = docs
        val report = FakeNode("report.txt", false, content = "hello".toByteArray(), parent = docs)
        docs.children["report.txt"] = report
        return root
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    @Test
    fun rootsRequest_advertisesCapabilities() = runBlocking {
        val (h, sender, _) = build(sampleTree(), granted = true)
        h.handleControlMessage("""{"type":"file_roots_request"}""")
        val msg = sender.last()
        assertEquals("file_roots_response", msg.getString("type"))
        val payload = msg.getJSONObject("fileRootsResponse")
        assertEquals(1, payload.getJSONArray("roots").length())
        val caps = payload.getJSONObject("fileCapabilities")
        assertEquals(3, caps.getInt("protocol"))
        assertTrue(caps.getBoolean("binary"))
        assertTrue(caps.getBoolean("resume"))
        assertTrue(caps.getBoolean("fullBrowse"))
        // ops must include the new v3 manager operations.
        val ops = caps.getJSONArray("ops")
        val opSet = (0 until ops.length()).map { ops.getString(it) }.toSet()
        assertTrue(opSet.containsAll(listOf("copy", "move", "mkdir", "search")))
    }

    @Test
    fun browse_listsChildren() = runBlocking {
        val (h, sender, _) = build(sampleTree())
        h.handleControlMessage(
            """{"type":"file_browse_request","fileBrowseRequest":{"requestId":"r1","rootId":"root1","relativePath":"Docs"}}"""
        )
        val payload = sender.last().getJSONObject("fileBrowseResponse")
        assertEquals("r1", payload.getString("requestId"))
        val entries = payload.getJSONArray("entries")
        assertEquals(1, entries.length())
        assertEquals("report.txt", entries.getJSONObject(0).getString("name"))
        assertEquals(5L, entries.getJSONObject(0).getLong("sizeBytes"))
    }

    @Test
    fun mkdir_createsFolder() = runBlocking {
        val tree = sampleTree()
        val (h, sender, _) = build(tree)
        h.handleControlMessage(
            """{"type":"file_manage_request","fileManageRequest":{"requestId":"m1","rootId":"root1","relativePath":"Docs","operation":"mkdir","newName":"New Folder"}}"""
        )
        val payload = sender.last().getJSONObject("fileManageResponse")
        assertTrue(payload.getBoolean("success"))
        assertNotNull((tree.children["Docs"] as FakeNode).children["New Folder"])
    }

    @Test
    fun copy_duplicatesFileContent() = runBlocking {
        val tree = sampleTree()
        val (h, sender, _) = build(tree)
        h.handleControlMessage(
            """{"type":"file_manage_request","fileManageRequest":{"requestId":"c1","rootId":"root1","relativePath":"Docs/report.txt","operation":"copy","destinationPath":"Docs/copy.txt"}}"""
        )
        assertTrue(sender.last().getJSONObject("fileManageResponse").getBoolean("success"))
        val docs = tree.children["Docs"] as FakeNode
        assertNotNull(docs.children["copy.txt"])
        assertEquals("hello", String(docs.children["copy.txt"]!!.content))
        // Copy must not remove the source.
        assertNotNull(docs.children["report.txt"])
    }

    @Test
    fun move_copiesThenDeletesSource() = runBlocking {
        val tree = sampleTree()
        val (h, sender, _) = build(tree)
        h.handleControlMessage(
            """{"type":"file_manage_request","fileManageRequest":{"requestId":"mv1","rootId":"root1","relativePath":"Docs/report.txt","operation":"move","destinationPath":"moved.txt"}}"""
        )
        assertTrue(sender.last().getJSONObject("fileManageResponse").getBoolean("success"))
        assertNotNull(tree.children["moved.txt"])
        assertNull((tree.children["Docs"] as FakeNode).children["report.txt"])
    }

    @Test
    fun copy_toExistingWithoutOverwrite_fails() = runBlocking {
        val tree = sampleTree()
        val docs = tree.children["Docs"] as FakeNode
        docs.children["copy.txt"] = FakeNode("copy.txt", false, content = "old".toByteArray(), parent = docs)
        val (h, sender, _) = build(tree)
        h.handleControlMessage(
            """{"type":"file_manage_request","fileManageRequest":{"requestId":"c2","rootId":"root1","relativePath":"Docs/report.txt","operation":"copy","destinationPath":"Docs/copy.txt","overwrite":false}}"""
        )
        assertFalse(sender.last().getJSONObject("fileManageResponse").getBoolean("success"))
    }

    @Test
    fun search_findsMatchingNamesUnderRoot() = runBlocking {
        val (h, sender, _) = build(sampleTree())
        h.handleControlMessage(
            """{"type":"file_search_request","fileSearchRequest":{"requestId":"s1","rootId":"root1","query":"report","maxResults":50}}"""
        )
        val payload = sender.last().getJSONObject("fileSearchResponse")
        assertEquals("s1", payload.getString("requestId"))
        val entries = payload.getJSONArray("entries")
        assertEquals(1, entries.length())
        assertEquals("report.txt", entries.getJSONObject(0).getString("name"))
        assertEquals("Docs/report.txt", entries.getJSONObject(0).getString("relativePath"))
        assertFalse(payload.getBoolean("truncated"))
    }

    @Test
    fun metadata_reportsSizeAndReadOnly() = runBlocking {
        val tree = sampleTree()
        (tree.children["Docs"] as FakeNode).children["report.txt"]!!.writable = false
        val (h, sender, _) = build(tree)
        h.handleControlMessage(
            """{"type":"file_metadata_request","fileMetadataRequest":{"requestId":"md1","rootId":"root1","relativePath":"Docs/report.txt"}}"""
        )
        val payload = sender.last().getJSONObject("fileMetadataResponse")
        assertEquals(5L, payload.getLong("size"))
        assertFalse(payload.getBoolean("isDirectory"))
        assertTrue(payload.getBoolean("readOnly"))
    }

    @Test
    fun rootManageRemove_callsMutator_andReturnsRoots() = runBlocking {
        val (h, sender, mutator) = build(sampleTree())
        h.handleControlMessage(
            """{"type":"file_root_manage_request","fileRootManageRequest":{"requestId":"rm1","operation":"remove","rootId":"root1"}}"""
        )
        assertEquals(listOf("root1"), mutator.removed)
        val payload = sender.last().getJSONObject("fileRootManageResponse")
        assertEquals("rm1", payload.getString("requestId"))
        assertNotNull(payload.getJSONArray("roots"))
    }

    @Test
    fun volumesRequest_whenNotGranted_returnsEmptyAndFalse() = runBlocking {
        val (h, sender, _) = build(sampleTree(), granted = false)
        h.handleControlMessage(
            """{"type":"file_volumes_request","fileVolumesRequest":{"requestId":"v1"}}"""
        )
        val payload = sender.last().getJSONObject("fileVolumesResponse")
        assertEquals(0, payload.getJSONArray("volumes").length())
        assertFalse(payload.getBoolean("fullBrowseGranted"))
    }

    @Test
    fun unknownMessage_isNotConsumed() = runBlocking {
        val (h, _, _) = build(sampleTree())
        assertFalse(h.handleControlMessage("""{"type":"telemetry"}"""))
    }

    // -- Push consent gate (RemEx-z6lh) -------------------------------------------

    /**
     * An UPLOAD-shaped offer. **Not what the PC sends for a push** — see
     * [pushOffer_asThePcActuallySendsIt_isAccepted].
     *
     * `destRoot`/`destRelativePath` are real for an upload: the user picked the destination folder on
     * the phone and the PC echoes it back. A PUSH carries neither, because the PC does not know and
     * does not choose where files land on someone's phone. Using this helper for push tests is what
     * hid RemEx-h1p5 — it supplied a field the sender never sends, so the tests agreed with the
     * handler while both disagreed with production.
     */
    private fun offer(transferId: String, mode: String) = """
        {"type":"file_transfer_offer","fileTransferOffer":{
          "transferId":"$transferId","mode":"$mode","destRoot":"root1",
          "destRelativePath":"Docs","fileName":"pushed.txt","size":5}}
    """.trimIndent()

    /**
     * The defect: a paired PC could skip file_push_offer entirely, invent a transfer id, and land
     * files in a writable shared root with no consent prompt ever shown.
     */
    @Test
    fun pushOffer_withAnIdTheUserNeverAccepted_isRefused() = runBlocking {
        val (h, sender, _) = build(sampleTree(), granted = true)

        h.handleControlMessage(offer("forged-id", FileTransferModes.PUSH))

        val msg = sender.last()
        assertEquals("file_transfer_ready", msg.getString("type"))
        val payload = msg.getJSONObject("fileTransferReady")
        assertFalse(payload.getBoolean("accepted"))
        assertTrue(payload.getString("declineReason").contains("not accepted"))
    }

    @Test
    fun pushDestination_isTheFirstWritableSharedFolder() {
        val roots =
            listOf(
                RootDescriptor("read-only", "Photos", false, false, false, false, false),
                RootDescriptor("writable-1", "Downloads", true, true, true, true, false),
                RootDescriptor("writable-2", "Documents", true, true, true, true, false),
            )

        // Skips the read-only one rather than trying and failing it: a share CAN be read-only, and
        // choosing it would refuse the transfer after the user had already agreed to it.
        assertEquals("writable-1", pushDestinationRoot(roots))
    }

    @Test
    fun pushDestination_isNullWhenNothingWritableIsShared() {
        assertNull(pushDestinationRoot(emptyList()))
        assertNull(
            pushDestinationRoot(
                listOf(RootDescriptor("read-only", "Photos", false, false, false, false, false))
            )
        )
    }

    /**
     * A push offer shaped the way the PC ACTUALLY SENDS IT — no destRoot, no destRelativePath.
     *
     * **THIS IS THE CASE EVERY OTHER PUSH TEST HERE MISSES, AND IT IS WHY THE FEATURE SHIPPED DEAD
     * (RemEx-h1p5).** The [offer] helper hard-codes `destRoot: "root1"`, but
     * `TransferSessionManager.PushFileAsync` sends only transferId, mode, fileName and size. Against
     * the real message `beginHostReceive` hit its `destRoot.isNullOrBlank()` guard and declined every
     * file, so the consent-gated push never moved a byte: the user tapped Allow, the PC received its
     * transfer ids, and each one came back refused.
     *
     * The fixture supplied the missing field, so the tests agreed with the code and both disagreed
     * with production. A fixture that is more generous than the sender is not a test of the sender.
     */
    @Test
    fun pushOffer_asThePcActuallySendsIt_isAccepted() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("pushed.txt", 5)))
        val (h, sender, _) = build(sampleTree(), granted = true, pushConsent = consent)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","fileName":"pushed.txt","size":5}}
            """.trimIndent()
        )

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertTrue(
            "a push carrying no destRoot must be accepted — the phone chooses the folder, and the " +
                "PC has never sent one. Decline reason was: " + payload.optString("declineReason"),
            payload.getBoolean("accepted"),
        )
    }

    /**
     * A granted id carrying a DIFFERENT size than the one the consent prompt showed is refused.
     *
     * Exercised at the handler rather than only on the registry, because only this reaches the
     * `"size"` wire key — every other push test matches its size by coincidence, the `offer()`
     * helper's `"size":5` happening to equal the grants. Reading the wrong key here would bind
     * everything to 0 and go unnoticed. (RemEx-ccqb.)
     */
    @Test
    fun pushOffer_atADifferentSizeThanGranted_isRefused() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("pushed.txt", 5)))
        val (h, sender, _) = build(sampleTree(), granted = true, pushConsent = consent)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","fileName":"pushed.txt","size":5368709120}}
            """.trimIndent()
        )

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertFalse(
            "five gigabytes under a grant for five bytes is the whole bead",
            payload.getBoolean("accepted"),
        )
    }

    /**
     * A negative declared size is refused outright, for every mode.
     *
     * It is reachable — `Size` is a bare long in remex.core with no validator — and everything
     * downstream is written assuming it is not: a negative would switch off the byte ceiling in
     * HostReceiveSession and let the completion check pass, which is an unbounded write into staging.
     */
    @Test
    fun offer_withANegativeSize_isRefused() = runBlocking {
        val (h, sender, _) = build(sampleTree(), granted = true)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"any-id","mode":"upload","destRoot":"root1","fileName":"a.txt","size":-1}}
            """.trimIndent()
        )

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertFalse(payload.getBoolean("accepted"))
        assertTrue(payload.getString("declineReason").contains("negative size"))
    }

    /**
     * A push refused AFTER the user accepted it is reported so somebody can be told (RemEx-gipu).
     *
     * Accepting a file and then receiving nothing, with no explanation anywhere on the phone, is
     * indistinguishable from the app being broken — and it was the literal symptom of RemEx-h1p5,
     * where pushes genuinely were broken, which is why nobody could tell the two states apart.
     */
    @Test
    fun pushRefusedAfterConsent_isReportedWithAnActionableReason() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("pushed.txt", 5)))
        val readOnlyOnly = listOf(RootDescriptor("root1", "Shared", false, false, false, false, false))
        val (h, _, _) =
            build(sampleTree(), granted = true, pushConsent = consent, roots = readOnlyOnly)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","fileName":"pushed.txt","size":5}}
            """.trimIndent()
        )

        assertEquals(listOf(PushRefusal.NoWritableSharedFolder), pushRefusals)
    }

    /**
     * A granted id carrying the WRONG file is reported — it is not the forged case.
     *
     * Review caught the first version calling this "before consent" and staying silent. isGrantedFor
     * fails two ways, and only one of them means nobody agreed to anything: here a grant for this very
     * id exists, so this device accepted something under it and the PC then negotiated another file.
     * From the user's side that is the file they approved never arriving.
     */
    @Test
    fun pushUnderAGrantForAnotherFile_isReported() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("cat.jpg", 5)))
        val (h, _, _) = build(sampleTree(), granted = true, pushConsent = consent)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","fileName":"resume.pdf","size":5}}
            """.trimIndent()
        )

        assertEquals(listOf(PushRefusal.OfferedFileDiffers), pushRefusals)
    }

    /**
     * An offer refused BEFORE consent stays silent.
     *
     * The user never agreed to anything, so there is nothing to explain — and notifying here would
     * hand a paired-but-hostile PC a way to ring somebody's phone at will, by sending offers under
     * transfer ids nobody granted.
     */
    @Test
    fun pushRefusedBeforeConsent_isNotReportedToTheUser() = runBlocking {
        val (h, sender, _) = build(sampleTree(), granted = true)

        h.handleControlMessage(offer("forged-id", FileTransferModes.PUSH))

        assertFalse(sender.last().getJSONObject("fileTransferReady").getBoolean("accepted"))
        assertTrue("a refusal the user never invited must not notify them", pushRefusals.isEmpty())
    }

    /**
     * An UPLOAD decline is not reported either: it is the PC's own operation and the PC already sees
     * the reason. Notifying would put a message on the phone about something nobody there did.
     */
    @Test
    fun uploadDecline_isNotReportedToThePhoneUser() = runBlocking {
        val (h, _, _) = build(sampleTree(), granted = true, roots = emptyList())

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"up-1","mode":"upload","destRoot":"gone","fileName":"a.txt","size":5}}
            """.trimIndent()
        )

        assertTrue(pushRefusals.isEmpty())
    }

    /**
     * A destRoot the PC sends anyway is IGNORED for a push.
     *
     * The honest PC sends none, so nothing legitimate depends on it — which means any push that does
     * carry one came from something trying to choose a folder on this phone. Before RemEx-h1p5 that
     * choice was honoured outright, and `SafFileSystemFacade.resolve` never checks a rootId against
     * the shared list: it just calls `DocumentFile.fromTreeUri`, so a granted push could name ANY tree
     * the app still holds a persisted grant for, shared or not. Ignoring the field is what confines a
     * push to one folder the user is currently sharing.
     */
    @Test
    fun pushOffer_ignoresADestRootThePcTriesToChoose() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("pushed.txt", 5)))
        val (h, sender, _) = build(sampleTree(), granted = true, pushConsent = consent)

        // "not-shared" is NOT among the shared roots — which is the case that matters, since a
        // persisted SAF grant can outlive a share being removed. Honouring it would resolve to
        // nothing and decline; ignoring it lands in the one root actually shared. So the assertion
        // below genuinely distinguishes the two behaviours rather than passing either way.
        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","destRoot":"not-shared",
              "fileName":"pushed.txt","size":5}}
            """.trimIndent()
        )

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertTrue(
            "a push naming its own destRoot must be accepted into the phone's choice, not the PC's. " +
                "Decline reason was: " + payload.optString("declineReason"),
            payload.getBoolean("accepted"),
        )
    }

    /**
     * With nowhere writable shared, the push is refused AND the minted id is handed back.
     *
     * Releasing matters as much as refusing: the id was granted before the reply was sent, so a
     * refusal that kept it would leave the registry authorising a transfer that can never arrive.
     */
    @Test
    fun pushOffer_withNoWritableSharedFolder_isRefusedAndTheGrantIsReleased() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("pushed.txt", 5)))
        val readOnlyOnly = listOf(RootDescriptor("root1", "Shared", false, false, false, false, false))
        val (h, sender, _) =
            build(sampleTree(), granted = true, pushConsent = consent, roots = readOnlyOnly)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","fileName":"pushed.txt","size":5}}
            """.trimIndent()
        )

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertFalse(payload.getBoolean("accepted"))
        assertTrue(
            "the refusal should say what the user can do about it, not name an internal concept. " +
                "Got: " + payload.getString("declineReason"),
            payload.getString("declineReason").contains("shared for writing"),
        )
        assertFalse(
            "a refused push must give its consent id back, or the registry keeps authorising a " +
                "transfer that will never happen",
            consent.isGrantedFor("minted-id", "pushed.txt", 5),
        )
    }

    /** The id the device minted after the user tapped Accept is honoured. */
    @Test
    fun pushOffer_withAConsentedId_isAccepted() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("pushed.txt", 5)))
        val (h, sender, _) = build(sampleTree(), granted = true, pushConsent = consent)

        h.handleControlMessage(offer("minted-id", FileTransferModes.PUSH))

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertTrue(payload.getBoolean("accepted"))
    }

    /**
     * An UPLOAD must NOT be gated. It targets a folder the user already shared for writing, so the
     * share is the consent -- gating it would have broken every ordinary upload, which is the
     * failure a too-broad fix would produce here.
     */
    @Test
    fun uploadOffer_isNotSubjectToPushConsent() = runBlocking {
        val (h, sender, _) = build(sampleTree(), granted = true)

        h.handleControlMessage(offer("no-grant-needed", FileTransferModes.UPLOAD))

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertTrue(payload.getBoolean("accepted"))
    }

    /** A finished push cannot be replayed as a fresh one under the same id. */
    @Test
    fun completingAPush_releasesItsGrant() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("once-only" to GrantedFile("pushed.txt", 5)))
        val (h, _, _) = build(sampleTree(), granted = true, pushConsent = consent)

        h.handleControlMessage(
            """{"type":"file_transfer_complete","fileTransferComplete":{"transferId":"once-only"}}"""
        )

        assertFalse(consent.isGrantedFor("once-only", "pushed.txt", 5))
    }

    /**
     * A granted id carrying a DIFFERENT file name than the one the user saw is refused (RemEx-tutz).
     *
     * The consent prompt names the files (`describePushFiles`), so a grant is an answer about those
     * files. Matching on the transfer id alone made it an answer about a slot: an id minted for one
     * file could be negotiated carrying another, and the phone would accept it without prompting
     * again — the user having agreed to receive something they were never shown.
     *
     * It needs a paired PC to exercise, which is exactly the actor the id check itself exists to
     * constrain (RemEx-z6lh), and it pairs with the overwrite protection from RemEx-h1p5: without
     * both, a swapped name could also land on top of a file already there.
     */
    @Test
    fun pushOffer_underAGrantForADifferentFile_isRefused() = runBlocking {
        val consent = PushConsentRegistry()
        consent.grant(mapOf("minted-id" to GrantedFile("cat.jpg", 5)))
        val (h, sender, _) = build(sampleTree(), granted = true, pushConsent = consent)

        h.handleControlMessage(
            """
            {"type":"file_transfer_offer","fileTransferOffer":{
              "transferId":"minted-id","mode":"push","fileName":"resume.pdf","size":5}}
            """.trimIndent()
        )

        val payload = sender.last().getJSONObject("fileTransferReady")
        assertFalse(
            "an id granted for cat.jpg must not carry resume.pdf",
            payload.getBoolean("accepted"),
        )

        // The grant survives: this offer failed its own check, and discarding the grant on the
        // strength of a message that did not match would let a bad offer cancel a good one.
        assertTrue(consent.isGrantedFor("minted-id", "cat.jpg", 5))
    }
}
