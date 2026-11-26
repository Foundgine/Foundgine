using Foundgine.Core.Execution;
using Foundgine.Runtime.Recovery;

// Child-process entry point for FileMutationExecutionLedgerProcessTests. Usage: <ledgerPath> <mode> <id>
// Modes: race (create contested keys, then compare-and-set a shared record), write (append until killed).
const int Keys = 40, UpdatesEach = 25;
var t0 = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

MutationExecutionRecord Record(string key, string requestId)
{
    var receipt = new ExecutionReceipt(requestId, "pending", "sm-1", 1, 1, 1, 1, "intent", "plan", "auth", "provider",
        t0, t0, [1], ["write"], "pending", CorrelationId: "corr-1");
    return new MutationExecutionRecord("t", "s", key, requestId, "plan", "corr-1", MutationExecutionState.Started, 1,
        0, t0, t0, t0.AddSeconds(60), receipt, [], false, null);
}

var ledger = new FileMutationExecutionLedger(args[0]);
string mode = args[1], id = args[2];
switch (mode)
{
    case "race":
        for (var k = 0; k < Keys; k++)
            if ((await ledger.TryCreateAsync(Record("k" + k, "p" + id))).Created) Console.WriteLine("W k" + k);
        var done = 0;
        while (done < UpdatesEach)
        {
            var cur = (await ledger.FindAsync("t", "s", "shared"))!;
            var next = cur.Transition(MutationExecutionState.Started, t0, cur.Receipt, cur.AffectedRows, "p" + id + "-" + done);
            if (await ledger.TryUpdateAsync(cur.Version, next) is not null) done++;
        }
        Console.WriteLine("DONE " + done);
        return 0;
    case "write": // appends until killed by the parent
        for (var i = 0; ; i++) await ledger.TryCreateAsync(Record("w" + id + "-" + i, "r" + i));
    default:
        throw new ArgumentException(mode);
}
