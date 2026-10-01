package com.clindsay94.remex

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Instrumented test, which will execute on an Android device.
 *
 * See [testing documentation](http://d.android.com/tools/testing).
 */
@RunWith(AndroidJUnit4::class)
class ExampleInstrumentedTest {
    @Test
    fun useAppContext() {
        // Context of the app under test: the "instrumented" build type, which carries an
        // application id suffix so it never replaces the real app (RemEx-3dvre).
        val appContext = InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals("com.clindsay94.remex.instrumented", appContext.packageName)
    }
}