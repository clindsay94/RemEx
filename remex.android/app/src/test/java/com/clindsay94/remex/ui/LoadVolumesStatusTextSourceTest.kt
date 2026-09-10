package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins that `loadVolumes()` names the wait it starts (RemEx-c7v4n round 3 review): the phone must say
 * a human is being asked to approve on the PC, not just that something is "loading".
 *
 * [FileTransferViewModel] is an `AndroidViewModel` needing a real `Application`, and this module has
 * no Robolectric, so the direct check — construct it, call `loadVolumes()`, read `statusText` — is not
 * available here. Reading the source is the same technique
 * [com.clindsay94.remex.ui.SendDispatcherDeclarationOrderTest] already uses for exactly this reason.
 */
class LoadVolumesStatusTextSourceTest {

    private fun loadVolumesBody(): String {
        val root = System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let {
                generateSequence(it) { p -> p.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

        val file = File(
            root,
            "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/FileTransferViewModel.kt",
        )
        assertTrue("expected to find FileTransferViewModel.kt at ${file.path}", file.isFile)
        val source = file.readText()

        val start = source.indexOf("fun loadVolumes()")
        assertTrue("expected to find fun loadVolumes() in FileTransferViewModel", start >= 0)
        // The next top-level (4-space-indented) `fun` declaration is the sibling method that follows
        // loadVolumes() in the class body — the same "next fun" boundary technique
        // SendDispatcherDeclarationOrderTest already relies on, good enough without a full brace parser
        // because loadVolumes() is not the last method in the file.
        val nextFun = Regex("""\n {4}fun """).find(source, start + 1)
        assertTrue("expected another method declaration after loadVolumes()", nextFun != null)
        return source.substring(start, nextFun!!.range.first)
    }

    @Test
    fun `loadVolumes sets the full-browse-pending status, naming the PC prompt`() {
        val body = loadVolumesBody()
        assertTrue(
            "loadVolumes() must set R.string.file_manager_full_browse_pending when it sends the " +
                "request, so the phone tells the user a prompt is waiting on their PC (RemEx-c7v4n) " +
                "instead of a generic 'loading' message that could just as well mean nothing is wrong.",
            body.contains("R.string.file_manager_full_browse_pending"),
        )
    }

    @Test
    fun `loadVolumes does not fall back to the old generic loading-drives status`() {
        val body = loadVolumesBody()
        assertFalse(
            "loadVolumes() still references the retired R.string.file_manager_requesting_volumes " +
                "key, which named 'Loading drives…' rather than that a human on the PC has to act.",
            body.contains("R.string.file_manager_requesting_volumes"),
        )
    }
}
