package com.clindsay94.remex.ui.navigation

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards RemEx-740mr: a pager tab without a page must fail at compile time, not on first swipe.
 *
 * The compile-time half of the fix — `AppNavigation.PrimaryDestinationsPager`'s exhaustive `when`
 * over the sealed [PrimaryDestination] type, with no `else` branch — cannot be exercised by a JVM
 * unit test; a missing branch is a build failure, not a runtime condition to assert on. That half is
 * proven by injection instead (add a `data object` under `PrimaryDestination` with no page wired and
 * confirm `:app:compileReleaseKotlin` fails), not by this file.
 *
 * What THIS test pins is the other half of the contract, which compiles fine either way and so needs
 * its own guard: `navItems`, the runtime list `PrimaryDestinationsPager` indexes into by position,
 * must actually contain exactly one instance of every sealed subclass of [PrimaryDestination]. A
 * destination that extends `PrimaryDestination` (so the exhaustive `when` demands — and gets — a
 * branch) but is left out of `navItems` would still never receive a page, because the pager only
 * ever iterates `navItems.indices`.
 */
class NavigationPagerExhaustivenessTest {

    @Test
    fun `every PrimaryDestination subclass has exactly one slot in navItems`() {
        val declaredSubclasses = PrimaryDestination::class.sealedSubclasses
        assertTrue(
                "expected at least one PrimaryDestination subclass to exist",
                declaredSubclasses.isNotEmpty(),
        )

        val declaredInstances = declaredSubclasses.map { it.objectInstance }
        assertTrue(
                "every PrimaryDestination subclass must be a singleton (data object) — " +
                        "PrimaryDestinationsPager can only render one that is",
                declaredInstances.all { it != null },
        )

        assertEquals(
                "navItems must hold exactly the sealed subclasses of PrimaryDestination",
                declaredInstances.toSet(),
                navItems.toSet(),
        )
        assertEquals(
                "navItems must not repeat or drop a destination — the pager indexes by position",
                declaredInstances.size,
                navItems.size,
        )
    }
}
