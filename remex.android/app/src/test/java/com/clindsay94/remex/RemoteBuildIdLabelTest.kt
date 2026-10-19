package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.hostVersionLabel
import com.clindsay94.remex.ui.screens.remoteBuildIdLabel
import com.clindsay94.remex.ui.screens.versionDetailsText
import com.clindsay94.remex.ui.screens.versionsDiffer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the comparable form of the PC's build id on the phone's About screen (RemEx-d9guj), the
 * mirror of the desktop's `AppVersion.NormalizeRemoteBuildId` tests — the two normalizers must
 * agree or the "one screen, two comparable ids" property quietly breaks on one side.
 */
class RemoteBuildIdLabelTest {

    @Test
    fun `a clean sha compares as-is`() = assertEquals("39b0b09", remoteBuildIdLabel("39b0b09"))

    @Test
    fun `a dirty suffix reduces to the bare marker`() {
        // MSBuild hashed that suffix, not Gradle: rendering it beside this phone's own id invites
        // a comparison that reports a difference that is not there.
        assertEquals("39b0b09+", remoteBuildIdLabel("39b0b09+a3f1"))
    }

    @Test
    fun `wire whitespace is tolerated`() = assertEquals("39b0b09+", remoteBuildIdLabel(" 39b0b09+a3f1 "))

    @Test
    fun `an absent id shows nothing rather than unknown`() {
        assertEquals("", remoteBuildIdLabel(""))
        assertEquals("", remoteBuildIdLabel("   "))
        assertEquals("", remoteBuildIdLabel("unknown"))
        assertEquals("", remoteBuildIdLabel("Unknown"))
    }

    @Test
    fun `a suffix with no sha compares as nothing`() = assertEquals("", remoteBuildIdLabel("+a3f1"))

    @Test
    fun `the host's four-part version drops a trailing zero`() {
        assertEquals("3.1.0", hostVersionLabel("3.1.0.0"))
        assertEquals("3.1.0.2", hostVersionLabel("3.1.0.2"))
        assertEquals("3.1.0", hostVersionLabel("3.1.0"))
        assertEquals("", hostVersionLabel(""))
        assertEquals("", hostVersionLabel("  "))
    }

    @Test
    fun `versions differ only on the first three parts`() {
        assertFalse(versionsDiffer("3.1.0", "3.1.0.0"))
        assertFalse(versionsDiffer("3.1.0", "3.1.0.7"))
        assertTrue(versionsDiffer("3.1.0", "3.0.2.0"))
        assertTrue(versionsDiffer("3.1.0", "3.1.1.0"))
    }

    @Test
    fun `an unreadable version never reports a mismatch`() {
        assertFalse(versionsDiffer("3.1.0", "Windows (Native)"))
        assertFalse(versionsDiffer("3.1.0", ""))
        assertFalse(versionsDiffer("", "3.1.0.0"))
        assertFalse(versionsDiffer("3.1", "3.1.0.0"))
    }

    @Test
    fun `copied version details name both sides and skip absent parts`() {
        assertEquals(
            "RemEx Android 3.1.0 (Build abc123)\nRemEx PC 3.1.0 (Build abc123+)",
            versionDetailsText("3.1.0", "abc123", "3.1.0", "abc123+")
        )
        assertEquals("RemEx Android 3.1.0", versionDetailsText("3.1.0", "unknown", "", ""))
    }
}
