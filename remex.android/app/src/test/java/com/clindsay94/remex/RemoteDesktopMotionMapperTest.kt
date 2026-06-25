package com.clindsay94.remex

import android.view.MotionEvent
import com.clindsay94.remex.ui.screens.PointerDeviceKind
import com.clindsay94.remex.ui.screens.PointerPhase
import com.clindsay94.remex.ui.screens.PointerToolKind
import com.clindsay94.remex.ui.screens.RemoteDesktopMotionMapper
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * JVM unit tests for [RemoteDesktopMotionMapper].
 *
 * These tests exercise the pure mapping helpers ([phaseFromAction], [toolKindFromInt],
 * [buttonMaskFromState]) which accept raw integer constants from [MotionEvent] and return protocol
 * types. No Android runtime or Mockito is required.
 */
class RemoteDesktopMotionMapperTest {

    // ── Phase mapping ──────────────────────────────────────────────────────────

    @Test
    fun `ACTION_DOWN maps to ContactStart`() {
        assertEquals(
                PointerPhase.ContactStart,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_DOWN)
        )
    }

    @Test
    fun `ACTION_POINTER_DOWN maps to ContactStart`() {
        assertEquals(
                PointerPhase.ContactStart,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_POINTER_DOWN)
        )
    }

    @Test
    fun `ACTION_MOVE maps to ContactMove`() {
        assertEquals(
                PointerPhase.ContactMove,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_MOVE)
        )
    }

    @Test
    fun `ACTION_UP maps to ContactEnd`() {
        assertEquals(
                PointerPhase.ContactEnd,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_UP)
        )
    }

    @Test
    fun `ACTION_POINTER_UP maps to ContactEnd`() {
        assertEquals(
                PointerPhase.ContactEnd,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_POINTER_UP)
        )
    }

    @Test
    fun `ACTION_CANCEL maps to ContactEnd`() {
        assertEquals(
                PointerPhase.ContactEnd,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_CANCEL)
        )
    }

    @Test
    fun `ACTION_BUTTON_PRESS maps to ButtonPress`() {
        assertEquals(
                PointerPhase.ButtonPress,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_BUTTON_PRESS)
        )
    }

    @Test
    fun `ACTION_BUTTON_RELEASE maps to ButtonRelease`() {
        assertEquals(
                PointerPhase.ButtonRelease,
                RemoteDesktopMotionMapper.phaseFromAction(MotionEvent.ACTION_BUTTON_RELEASE)
        )
    }

    @Test
    fun `unknown action returns null`() {
        assertNull(RemoteDesktopMotionMapper.phaseFromAction(999))
    }

    // ── Tool kind mapping ─────────────────────────────────────────────────────

    @Test
    fun `TOOL_TYPE_STYLUS maps to Pen`() {
        assertEquals(
                PointerToolKind.Pen,
                RemoteDesktopMotionMapper.toolKindFromInt(MotionEvent.TOOL_TYPE_STYLUS)
        )
    }

    @Test
    fun `TOOL_TYPE_ERASER maps to Eraser`() {
        assertEquals(
                PointerToolKind.Eraser,
                RemoteDesktopMotionMapper.toolKindFromInt(MotionEvent.TOOL_TYPE_ERASER)
        )
    }

    @Test
    fun `TOOL_TYPE_FINGER maps to Finger`() {
        assertEquals(
                PointerToolKind.Finger,
                RemoteDesktopMotionMapper.toolKindFromInt(MotionEvent.TOOL_TYPE_FINGER)
        )
    }

    @Test
    fun `TOOL_TYPE_MOUSE maps to Mouse`() {
        assertEquals(
                PointerToolKind.Mouse,
                RemoteDesktopMotionMapper.toolKindFromInt(MotionEvent.TOOL_TYPE_MOUSE)
        )
    }

    @Test
    fun `TOOL_TYPE_UNKNOWN maps to None`() {
        assertEquals(
                PointerToolKind.None,
                RemoteDesktopMotionMapper.toolKindFromInt(MotionEvent.TOOL_TYPE_UNKNOWN)
        )
    }

    // ── Device kind mapping ───────────────────────────────────────────────────

    @Test
    fun `Pen tool maps to Stylus device kind`() {
        assertEquals(
                PointerDeviceKind.Stylus,
                RemoteDesktopMotionMapper.deviceKindFromToolKind(PointerToolKind.Pen)
        )
    }

    @Test
    fun `Eraser tool maps to Eraser device kind`() {
        assertEquals(
                PointerDeviceKind.Eraser,
                RemoteDesktopMotionMapper.deviceKindFromToolKind(PointerToolKind.Eraser)
        )
    }

    @Test
    fun `Finger tool maps to Touch device kind`() {
        assertEquals(
                PointerDeviceKind.Touch,
                RemoteDesktopMotionMapper.deviceKindFromToolKind(PointerToolKind.Finger)
        )
    }

    @Test
    fun `Mouse tool maps to Mouse device kind`() {
        assertEquals(
                PointerDeviceKind.Mouse,
                RemoteDesktopMotionMapper.deviceKindFromToolKind(PointerToolKind.Mouse)
        )
    }

    @Test
    fun `None tool maps to Unknown device kind`() {
        assertEquals(
                PointerDeviceKind.Unknown,
                RemoteDesktopMotionMapper.deviceKindFromToolKind(PointerToolKind.None)
        )
    }

    // ── Button mask mapping ───────────────────────────────────────────────────

    @Test
    fun `no buttons pressed returns mask 0`() {
        assertEquals(0, RemoteDesktopMotionMapper.buttonMaskFromState(0))
    }

    @Test
    fun `BUTTON_PRIMARY sets bit 0`() {
        val mask = RemoteDesktopMotionMapper.buttonMaskFromState(MotionEvent.BUTTON_PRIMARY)
        assertTrue("bit 0 should be set", mask and 0x01 != 0)
        assertEquals(0x01, mask)
    }

    @Test
    fun `BUTTON_STYLUS_PRIMARY sets bit 1`() {
        val mask = RemoteDesktopMotionMapper.buttonMaskFromState(MotionEvent.BUTTON_STYLUS_PRIMARY)
        assertTrue("bit 1 should be set for barrel primary", mask and 0x02 != 0)
        assertTrue("bit 0 should NOT be set", mask and 0x01 == 0)
    }

    @Test
    fun `BUTTON_STYLUS_SECONDARY sets bit 2`() {
        val mask =
                RemoteDesktopMotionMapper.buttonMaskFromState(MotionEvent.BUTTON_STYLUS_SECONDARY)
        assertTrue("bit 2 should be set for barrel secondary", mask and 0x04 != 0)
    }

    @Test
    fun `multiple buttons combine correctly`() {
        val combined = MotionEvent.BUTTON_PRIMARY or MotionEvent.BUTTON_STYLUS_PRIMARY
        val mask = RemoteDesktopMotionMapper.buttonMaskFromState(combined)
        assertTrue("bit 0 set", mask and 0x01 != 0)
        assertTrue("bit 1 set", mask and 0x02 != 0)
        assertTrue("bit 2 not set", mask and 0x04 == 0)
    }

    @Test
    fun `BUTTON_SECONDARY sets bit 3`() {
        val mask = RemoteDesktopMotionMapper.buttonMaskFromState(MotionEvent.BUTTON_SECONDARY)
        assertTrue("bit 3 should be set for secondary", mask and 0x08 != 0)
    }

    @Test
    fun `all stylus buttons set bits 0-3`() {
        val allButtons =
                MotionEvent.BUTTON_PRIMARY or
                        MotionEvent.BUTTON_STYLUS_PRIMARY or
                        MotionEvent.BUTTON_STYLUS_SECONDARY or
                        MotionEvent.BUTTON_SECONDARY
        val mask = RemoteDesktopMotionMapper.buttonMaskFromState(allButtons)
        assertEquals(0x0F, mask)
    }

    // ── Wire value correctness ────────────────────────────────────────────────

    @Test
    fun `PointerPhase wire values match host protocol`() {
        assertEquals("HoverStart", PointerPhase.HoverStart.wireValue)
        assertEquals("HoverMove", PointerPhase.HoverMove.wireValue)
        assertEquals("HoverEnd", PointerPhase.HoverEnd.wireValue)
        assertEquals("ContactStart", PointerPhase.ContactStart.wireValue)
        assertEquals("ContactMove", PointerPhase.ContactMove.wireValue)
        assertEquals("ContactEnd", PointerPhase.ContactEnd.wireValue)
        assertEquals("ButtonPress", PointerPhase.ButtonPress.wireValue)
        assertEquals("ButtonRelease", PointerPhase.ButtonRelease.wireValue)
    }

    @Test
    fun `PointerToolKind wire values match host protocol`() {
        assertEquals("None", PointerToolKind.None.wireValue)
        assertEquals("Pen", PointerToolKind.Pen.wireValue)
        assertEquals("Eraser", PointerToolKind.Eraser.wireValue)
        assertEquals("Mouse", PointerToolKind.Mouse.wireValue)
        assertEquals("Finger", PointerToolKind.Finger.wireValue)
    }

    @Test
    fun `PointerDeviceKind wire values match host protocol`() {
        assertEquals("Mouse", PointerDeviceKind.Mouse.wireValue)
        assertEquals("Touch", PointerDeviceKind.Touch.wireValue)
        assertEquals("Stylus", PointerDeviceKind.Stylus.wireValue)
        assertEquals("Eraser", PointerDeviceKind.Eraser.wireValue)
        assertEquals("Unknown", PointerDeviceKind.Unknown.wireValue)
    }
}
