namespace Foundgine.Runtime.Recovery;

/// <summary>
/// The execution ledger could not be read or written (I/O failure, lock timeout, corrupt journal). When thrown before
/// dispatch the engine has executed nothing: it refuses to run a mutation it cannot protect.
/// </summary>
public class MutationLedgerException : Exception
{
    public MutationLedgerException(string message) : base(message) { }
    public MutationLedgerException(string message, Exception innerException) : base(message, innerException) { }
}
