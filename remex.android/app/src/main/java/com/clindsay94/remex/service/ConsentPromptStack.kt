package com.clindsay94.remex.service

/**
 * The consent prompts currently outstanding, newest first (RemEx-hncl).
 *
 * **A SLOT WAS ENOUGH UNTIL TWO DIRECTIONS EXISTED.** Every prompt on this phone used to come from
 * one place, so `FileConsentManager` held a single `_activePrompt`. RemEx-vyhm added the routed
 * direction, and now two can be outstanding together: the PC pushing files to this phone (served
 * locally) while this phone asks to browse the PC (routed back here). The second `show()` overwrote
 * the slot, and `dismiss()` of the second cleared it to null rather than restoring the first — so the
 * first prompt's dialog never came back.
 *
 * **UI FIDELITY, NOT CONSENT SAFETY, and the distinction is worth keeping straight.** No prompt was
 * ever answered on the user's behalf: the overwritten one stayed reachable through its notification,
 * and both still fail closed on their own timeouts. What was lost was the dialog, which is the
 * surface a user is actually looking at when they are asked.
 *
 * NEWEST WINS while it is up, because the newest is the one the user was just interrupted by;
 * dismissing it falls back to whatever is still waiting rather than to nothing.
 *
 * A plain class with no Android types so the ordering can be tested directly — `FileConsentManager`
 * is an `object` whose prompter is built inside `start(context)`.
 */
class ConsentPromptStack {

    private val outstanding = mutableListOf<FileConsentPrompt>()

    /**
     * Adds a prompt and returns the one that should now be on screen.
     *
     * Re-showing the same consent id REPLACES rather than stacks: a re-raise is the same question
     * asked again, and stacking it would leave a duplicate behind that dismissing the top could
     * never clear.
     */
    @Synchronized
    fun push(prompt: FileConsentPrompt): FileConsentPrompt {
        outstanding.removeAll { it.consentId == prompt.consentId }
        outstanding.add(prompt)
        return prompt
    }

    /**
     * Removes a prompt by id and returns what should be on screen now, or null when nothing is left.
     *
     * Removing one that is NOT on top is ordinary — a routed prompt can be answered on the PC, or
     * time out, while a local one sits above it — and must not disturb the top.
     */
    @Synchronized
    fun remove(consentId: String): FileConsentPrompt? {
        outstanding.removeAll { it.consentId == consentId }
        return outstanding.lastOrNull()
    }

    /** What is on screen right now, or null. */
    @Synchronized
    fun top(): FileConsentPrompt? = outstanding.lastOrNull()

    /** How many questions are still unanswered. */
    @Synchronized
    fun size(): Int = outstanding.size
}
