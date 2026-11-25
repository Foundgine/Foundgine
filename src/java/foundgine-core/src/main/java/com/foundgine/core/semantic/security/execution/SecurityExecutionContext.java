package com.foundgine.core.semantic.security.execution;

import com.foundgine.core.semantic.security.warrants.SecurityWarrant;

import java.util.List;

import java.util.Objects;

/**
 * Trusted host-owned security context carried with a semantic request. The warrant is evidence of
 * authority; the engine still verifies it and checks it against the resolved capability at
 * execution time.
 */
public record SecurityExecutionContext(
        SecurityWarrant warrant,
        String subject,
        String audience,
        String tenant,
        String resourceScope,
        List<SecurityWarrant> delegationChain) {

    public SecurityExecutionContext {
        Objects.requireNonNull(warrant, "warrant");
        Objects.requireNonNull(subject, "subject");
        Objects.requireNonNull(audience, "audience");
        delegationChain = delegationChain == null ? null : List.copyOf(delegationChain);
    }

    public SecurityExecutionContext(
            SecurityWarrant warrant, String subject, String audience, String tenant, String resourceScope) {
        this(warrant, subject, audience, tenant, resourceScope, null);
    }

    public SecurityExecutionContext(SecurityWarrant warrant, String subject, String audience) {
        this(warrant, subject, audience, null, null, null);
    }

    public SecurityExecutionContext withDelegationChain(List<SecurityWarrant> chain) {
        return new SecurityExecutionContext(warrant, subject, audience, tenant, resourceScope, chain);
    }

    /**
     * Stable authority partition used to prevent cross-warrant provider-plan cache reuse. The full
     * warrant digest is intentional and conservative.
     */
    public String authorityCachePartition() {
        return String.join(
                "|",
                escape(subject),
                escape(audience),
                escape(tenant == null ? "-" : tenant),
                escape(resourceScope == null ? "-" : resourceScope),
                escape(warrant.digest()));
    }

    private static String escape(String value) {
        return value.replace("\\", "\\\\").replace("|", "\\|");
    }
}
