package com.foundgine.runtime.recovery;

/**
 * The execution ledger could not be read or written (I/O failure, lock timeout, corrupt journal). When thrown before
 * dispatch the engine has executed nothing: it refuses to run a mutation it cannot protect.
 */
public class MutationLedgerException extends RuntimeException {
    public MutationLedgerException(String message) { super(message); }
    public MutationLedgerException(String message, Throwable cause) { super(message, cause); }
}
