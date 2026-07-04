package com.clindsay94.remex.ui.screens

/**
 * Pure decision logic for applying latched PC-key modifiers (Ctrl/Alt/AltGr/Shift/Win) to a
 * non-modifier keypress (RemEx-yi8o, RemEx-9krr). Extracted from [RemoteDesktopViewModel.sendKeyPress]
 * so the modifier-wrap decision — which VKs to press before the key, which to release after, and
 * the resulting latch-state map — is testable on plain JVM without instantiating the ViewModel.
 */
data class ChordApplication(
        val toPress: List<Int>,
        val toRelease: List<Int>,
        val nextModifierStates: Map<Int, ModifierState>,
)

/**
 * Computes how [keyCode] should be wrapped by the currently latched modifiers in [modifierStates].
 *
 * A modifier not already in [physicallyDownModifiers] is included in [ChordApplication.toPress].
 * ARMED modifiers are included in [ChordApplication.toRelease] and dropped from
 * [ChordApplication.nextModifierStates] (one-shot); LOCKED modifiers stay held and stay in the
 * returned state map unchanged. Modifier keycodes themselves are never wrapped — [keyCode] is
 * checked against [modifierVirtualKeyCodes] and short-circuits to a no-op chord.
 */
fun computeChordApplication(
        modifierStates: Map<Int, ModifierState>,
        physicallyDownModifiers: Set<Int>,
        keyCode: Int,
        modifierVirtualKeyCodes: Set<Int>,
): ChordApplication {
        if (keyCode in modifierVirtualKeyCodes) {
                return ChordApplication(emptyList(), emptyList(), modifierStates)
        }

        val toPress = mutableListOf<Int>()
        val armedModifiers = mutableListOf<Int>()
        modifierStates.forEach { (vk, state) ->
                if (state == ModifierState.OFF) return@forEach
                if (vk !in physicallyDownModifiers) toPress.add(vk)
                if (state == ModifierState.ARMED) armedModifiers.add(vk)
        }

        val nextModifierStates =
                if (armedModifiers.isEmpty()) modifierStates
                else modifierStates - armedModifiers.toSet()

        return ChordApplication(toPress, armedModifiers, nextModifierStates)
}
