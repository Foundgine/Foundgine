package com.foundgine.runtime;

import com.foundgine.core.execution.ExecutionReceipt;
import com.foundgine.core.execution.mutation.MutationBatchResult;

/** Result of an executed semantic mutation, including provider-neutral execution evidence. */
public record MutationExecutionResult(
        MutationBatchResult result,
        String planFingerprint,
        String resultFingerprint,
        String approvalId,
        String approvedBy,
        ExecutionReceipt receipt) {

    /** Source-compatible constructor for callers created before receipts were attached. */
    public MutationExecutionResult(
            MutationBatchResult result,
            String planFingerprint,
            String resultFingerprint,
            String approvalId,
            String approvedBy) {
        this(result, planFingerprint, resultFingerprint, approvalId, approvedBy, null);
    }
}
