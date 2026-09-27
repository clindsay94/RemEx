package com.clindsay94.remex.routines

import androidx.glance.appwidget.action.ActionCallback
import com.clindsay94.remex.routines.widget.RunRoutineWidgetAction
import java.io.File
import java.lang.reflect.Modifier
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * "Widget taps work in a RELEASE build" (spec §14 S2, R-UX-25; bd memory
 * `glance-actioncallback-r8-init`). The failure only exists after R8, so this pins the shape R8 needs:
 * the Routine widget's Run goes through an [ActionCallback] (never a lambda action, which Glance
 * cannot rebuild from a class name), that callback is a public class with a public no-argument
 * constructor, and `proguard-rules.pro` keeps `<init>()` on every ActionCallback.
 */
class RoutineWidgetReleaseShapeTest {
    private fun file(vararg candidates: String): File =
        candidates.map(::File).firstOrNull { it.isFile } ?: error("not found: ${candidates.first()}")

    @Test
    fun `the Run action is a Glance ActionCallback with a no-arg constructor`() {
        val type = RunRoutineWidgetAction::class.java
        assertTrue(ActionCallback::class.java.isAssignableFrom(type))
        assertTrue(Modifier.isPublic(type.modifiers))
        assertTrue(Modifier.isPublic(type.getDeclaredConstructor().modifiers))
    }

    @Test
    fun `the widget taps through actionRunCallback, never a lambda`() {
        val src =
            file(
                "src/main/java/com/clindsay94/remex/routines/widget/RoutineWidget.kt",
                "app/src/main/java/com/clindsay94/remex/routines/widget/RoutineWidget.kt",
            ).readText()
        assertTrue(src.contains("actionRunCallback<RunRoutineWidgetAction>("))
        assertFalse("a lambda action does not survive the widget being recreated", src.contains("actionLambda"))
    }

    @Test
    fun `release keeps the no-arg constructor of every ActionCallback`() {
        val rules = file("proguard-rules.pro", "app/proguard-rules.pro").readText()
        val rule = Regex("""-keep\s+class\s+\*\s+implements\s+androidx\.glance\.appwidget\.action\.ActionCallback\s*\{[^}]*<init>\(\);[^}]*\}""")
        assertTrue("proguard-rules.pro must keep <init>() on ActionCallback implementations", rule.containsMatchIn(rules))
    }
}
