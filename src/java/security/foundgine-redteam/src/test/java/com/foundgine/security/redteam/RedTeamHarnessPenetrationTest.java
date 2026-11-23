package com.foundgine.security.redteam;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/** Runs the red-team harness under Maven so a regression fails the build instead of going unnoticed. */
class RedTeamHarnessPenetrationTest {

    @Test
    void everyRedTeamAttackIsBlocked() {
        var attacks = RedTeamHarness.run();

        assertFalse(attacks.isEmpty(), "The red-team harness produced no attacks.");

        var failed =
                attacks.stream()
                        .filter(a -> !a.passed())
                        .map(a -> a.id() + " :: " + a.detail())
                        .toList();

        assertTrue(failed.isEmpty(), () -> "Red-team attacks were not blocked: " + failed);
    }
}
