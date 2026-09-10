package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.remoteBuildIdLabel
import org.junit.Assert.assertEquals
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
}
