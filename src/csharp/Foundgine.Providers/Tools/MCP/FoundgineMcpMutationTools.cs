using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundgine.Core.Abstractions;
using Foundgine.Core.Execution;
using Foundgine.Core.Semantic.Mutation;
using Foundgine.Core.Semantic.Query;
using Foundgine.Core.Semantic.Security.Execution;
using Foundgine.Runtime;
using Foundgine.Runtime.Recovery;
using ModelContextProtocol.Server;
using ExecutionContext = Foundgine.Core.Execution.ExecutionContext;

namespace Foundgine.Providers.Tools.MCP;

[McpServerToolType]
public sealed class FoundgineMcpMutationTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFoundgineMutations? _mutations;
    private readonly ISecurityExecutionContextProvider _securityContextProvider;
    private readonly IFoundgineMcpApprovalTokenSigner? _approvalTokenSigner;
    private readonly IFoundgineMcpApprovalTokenKeyResolver? _approvalTokenKeys;
    private readonly FoundgineMcpApprovalTokenPolicy? _approvalTokenPolicy;

    /// <param name="mutations">The Foundgine mutation execution engine.</param>
    /// <param name="securityContextFactory">
    /// Obsolete delegate form of <paramref name="securityContextProvider"/>, retained in this
    /// parameter position for existing hosts calling positionally. Internally wrapped in a
    /// <see cref="DelegateSecurityExecutionContextProvider"/>.
    /// </param>
    /// <param name="securityContextProvider">
    /// Host-owned source of the caller's <see cref="SecurityExecutionContext"/>. Prefer this
    /// over <paramref name="securityContextFactory"/> for new hosts; both may not be supplied
    /// together.
    /// </param>
    /// <param name="approvalTokenSigner"></param>
    /// <param name="approvalTokenKeys"></param>
    /// <param name="approvalTokenPolicy"></param>
    public FoundgineMcpMutationTools(
        IFoundgineMutations? mutations = null,
        Func<SecurityExecutionContext?>? securityContextFactory = null,
        ISecurityExecutionContextProvider? securityContextProvider = null,
        IFoundgineMcpApprovalTokenSigner? approvalTokenSigner = null,
        IFoundgineMcpApprovalTokenKeyResolver? approvalTokenKeys = null,
        FoundgineMcpApprovalTokenPolicy? approvalTokenPolicy = null)
    {
        _mutations = mutations;

        if (securityContextProvider is not null && securityContextFactory is not null)
            throw new ArgumentException(
                $"Only one of {nameof(securityContextProvider)} or {nameof(securityContextFactory)} may be supplied.",
                nameof(securityContextFactory));

        _securityContextProvider = securityContextProvider
                                   ?? (securityContextFactory is not null
                                       ? new DelegateSecurityExecutionContextProvider(securityContextFactory)
                                       : new DelegateSecurityExecutionContextProvider(() => null));
        if ((approvalTokenSigner is null) != (approvalTokenKeys is null))
            throw new ArgumentException("Both approval-token signer and key resolver must be supplied together.");
        _approvalTokenSigner = approvalTokenSigner;
        _approvalTokenKeys = approvalTokenKeys;
        _approvalTokenPolicy = approvalTokenPolicy;
    }

    [McpServerTool(Name = "foundgine_mutation_dry_run")]
    [Description(
        "Validate and authorize a Foundgine mutation without executing it. Returns the exact semantic plan fingerprint and declared effects.")]
    public string DryRun(string mutationJson)
    {
        var mutations = RequireMutations();
        var request = WithHostSecurity(MutationJsonAdapter.Parse(mutationJson));
        return JsonSerializer.Serialize(mutations.DryRun(request), JsonOptions);
    }

    [McpServerTool(Name = "foundgine_mutation_approve")]
    [Description(
        "Create an approval bound to the exact authorized Foundgine mutation plan. Approval is not authorization and execution re-checks the plan.")]
    public string Approve(string mutationJson, string approvedBy)
    {
        var mutations = RequireMutations();
        _ = approvedBy;
        var request = WithHostSecurity(MutationJsonAdapter.Parse(mutationJson));
        var security = request.Security ??
                       throw new UnauthorizedAccessException(
                           "MCP mutation approval requires a host-supplied SecurityExecutionContext.");
        var signer = _approvalTokenSigner ?? throw new InvalidOperationException(
            "MCP approval-token signing is not configured; unsigned approvals are disabled.");
        var approval = mutations.Approve(request, security.Subject);
        return FoundgineMcpApprovalToken.Encode(approval.ApprovalId, approval.PlanFingerprint,
            approval.ApprovedBy, approval.ApprovedAt, mutationJson, signer);
    }

    [McpServerTool(Name = "foundgine_mutation_execute_approved")]
    [Description(
        "Execute only an approved Foundgine mutation after signature verification, re-authorization and exact plan fingerprint verification. Pass the signed approval token returned by foundgine_mutation_approve.")]
    public async Task<string> ExecuteApprovedAsync(string approvalJson, CancellationToken cancellationToken = default)
    {
        var keys = _approvalTokenKeys ?? throw new InvalidOperationException(
            "MCP approval-token verification is not configured; unsigned approvals are disabled.");
        var claims = FoundgineMcpApprovalToken.DecodeAndVerify(approvalJson, keys);
        var request = MutationJsonAdapter.Parse(claims.RequestJson);
        var security = _securityContextProvider.RequireSecurityExecutionContext(
            "MCP", "mutation execution");
        var mutations = RequireMutations();
        cancellationToken.ThrowIfCancellationRequested();
        (_approvalTokenPolicy ?? throw new InvalidOperationException(
            "MCP approval-token expiry and replay policy is not configured; execution is disabled."))
            .ValidateAndConsume(claims);
        var approval = new MutationPlanApproval(request with { Security = security }, claims.ApprovalId,
            claims.PlanFingerprint, claims.ApprovedBy,
            DateTimeOffset.FromUnixTimeMilliseconds(claims.ApprovedAtUnixMilliseconds));
        // The single-use approval id is the idempotency key: it lets the engine's execution ledger answer
        // foundgine_mutation_reconcile even though the token itself can never be presented again.
        var context = new ExecutionContext(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ExecutionContextKeys.IdempotencyKey] = IdempotencyKeyFor(claims.ApprovalId)
        });
        var result = await mutations.ExecuteApprovedAsync(approval, context, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>Reports what became of an approval presented to <see cref="ExecuteApprovedAsync"/>.</summary>
    /// <remarks>
    /// For the case where the agent never saw the response. The token's signature is verified but the token is not
    /// consumed (it is already spent), and only the caller's own tenant/subject scope is searched. It never executes
    /// the mutation; it can settle the ledger record and make the provider fence a late execution.
    /// </remarks>
    [McpServerTool(Name = "foundgine_mutation_reconcile")]
    [Description(
        "Find out what became of an approval you already presented to foundgine_mutation_execute_approved when the response was lost or reported an unknown outcome. Pass the same approval token. It never executes the mutation. Never retry an execution whose outcome is unknown; reconcile it first.")]
    public async Task<string> ReconcileAsync(string approvalJson, CancellationToken cancellationToken = default)
    {
        var keys = _approvalTokenKeys ?? throw new InvalidOperationException(
            "MCP approval-token verification is not configured.");
        var claims = FoundgineMcpApprovalToken.DecodeAndVerify(approvalJson, keys);
        var security = _securityContextProvider.RequireSecurityExecutionContext(
            "MCP", "mutation reconciliation");
        var recovery = RequireMutations().Recovery ?? throw new InvalidOperationException(
            "Mutation reconciliation is not configured. Configure FoundgineOptions.MutationLedger.");
        var report = await recovery.ReconcileAsync(security.Tenant, security.Subject,
            IdempotencyKeyFor(claims.ApprovalId), cancellationToken);

        var output = new Dictionary<string, object?>
        {
            ["status"] = StatusName(report.Outcome),
            ["detail"] = report.Detail,
            ["nextStep"] = NextStep(report.Outcome)
        };
        if (report.Record is { } record)
        {
            output["state"] = StateName(record.State);
            output["attempt"] = record.Attempt;
            output["requestId"] = record.RequestId;
            if (report.Outcome == MutationReconciliationOutcome.Committed)
                output["receipt"] = record.Receipt;
        }

        return JsonSerializer.Serialize(output, JsonOptions);
    }

    // Same wire names as the Java tool.
    private static string StatusName(MutationReconciliationOutcome outcome) => outcome switch
    {
        MutationReconciliationOutcome.NotFound => "not_found",
        MutationReconciliationOutcome.InProgress => "in_progress",
        MutationReconciliationOutcome.Committed => "committed",
        MutationReconciliationOutcome.Aborted => "aborted",
        MutationReconciliationOutcome.Unresolved => "unresolved",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static string StateName(MutationExecutionState state) => state switch
    {
        MutationExecutionState.Started => "started",
        MutationExecutionState.Committed => "committed",
        MutationExecutionState.Aborted => "aborted",
        MutationExecutionState.InDoubt => "in_doubt",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string NextStep(MutationReconciliationOutcome outcome) => outcome switch
    {
        MutationReconciliationOutcome.Committed =>
            "The mutation was applied. Do not execute it again; use the receipt as the result.",
        MutationReconciliationOutcome.Aborted =>
            "That attempt was not applied. Call foundgine_mutation_approve for a new approval and execute again if still wanted.",
        MutationReconciliationOutcome.InProgress =>
            "The execution is still running. Call this tool again shortly; do not execute again.",
        MutationReconciliationOutcome.Unresolved =>
            "The outcome could not be determined. Do not execute again; ask an operator to resolve it.",
        MutationReconciliationOutcome.NotFound =>
            "No execution is recorded for this approval under your identity. This does not prove that nothing " +
            "was applied: the original call may not have reached the ledger yet, or may have run under another " +
            "identity. Do not execute again until the original call has ended; call this tool again, and if it still " +
            "reports not_found, check the target data before asking for a new approval.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    /// <summary>The approval id itself when it forms a safe identifier, otherwise its SHA-256. Matches Java.</summary>
    private static string IdempotencyKeyFor(string approvalId)
    {
        var key = "mcp-approval:" + approvalId;
        return MutationExecutionRecord.IsSafeId(key)
            ? key
            : "mcp-approval:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(approvalId))).ToLowerInvariant();
    }

    private SemanticMutationRequest WithHostSecurity(SemanticMutationRequest request) =>
        request with
        {
            Security = _securityContextProvider.RequireSecurityExecutionContext(
                "MCP", "mutation execution")
        };

    private IFoundgineMutations RequireMutations() => _mutations ??
                                                      throw new InvalidOperationException(
                                                          "Foundgine mutation execution is not configured. Configure FoundgineOptions.MutationSchema and MutationProvider.");
}

internal static class MutationJsonAdapter
{
    public static SemanticMutationRequest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Mutation JSON is required.", nameof(json));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() == 0)
            throw new ArgumentException("'operations' must contain at least one mutation operation.", nameof(json));

        var list = new List<SemanticMutationOperation>();
        foreach (var op in operations.EnumerateArray()) list.Add(ParseOperation(op, list.Count));
        return new SemanticMutationRequest(new SemanticMutationOperationGraph(list));
    }

    private static SemanticMutationOperation ParseOperation(JsonElement op, int index)
    {
        var entity = new EntityId(GetULong(op, "entity"));
        var kind = Enum.Parse<SemanticMutationKind>(GetString(op, "kind"), true);
        var fields = new List<SemanticMutationField>();
        if (op.TryGetProperty("fields", out var fieldObject) && fieldObject.ValueKind == JsonValueKind.Object)
            foreach (var p in fieldObject.EnumerateObject())
                fields.Add(new SemanticMutationField(new FieldId(ulong.Parse(p.Name)), JsonElementToValue(p.Value)));

        var returns = ParseIds(op, "returnFields");
        var conflicts = ParseIds(op, "conflictFields");
        SemanticFilterExpression? filter =
            op.TryGetProperty("filter", out var filterJson) ? ParseFilter(filterJson) : null;

        return new SemanticMutationOperation(
            entity, kind, fields, filter, conflicts, returns,
            kind switch
            {
                SemanticMutationKind.Create => SemanticMutationBuilder.Create(entity, fields, returns).Effects,
                SemanticMutationKind.Update => SemanticMutationBuilder.Update(entity, fields, filter, returns).Effects,
                SemanticMutationKind.Delete => SemanticMutationBuilder.Delete(entity,
                    filter ?? throw new ArgumentException("Delete requires a filter."), returns).Effects,
                SemanticMutationKind.Upsert => SemanticMutationBuilder.Upsert(entity, fields, conflicts, returns)
                    .Effects,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            },
            Array.Empty<SemanticMutationDependency>());
    }

    private static SemanticFilterExpression ParseFilter(JsonElement json)
    {
        if (json.TryGetProperty("and", out var and))
            return new SemanticAndFilter(and.EnumerateArray().Select(ParseFilter).ToArray());
        if (json.TryGetProperty("or", out var or))
            return new SemanticOrFilter(or.EnumerateArray().Select(ParseFilter).ToArray());
        return new SemanticFieldFilter(
            new FieldId(json.GetProperty("field").GetUInt64()),
            Enum.Parse<SemanticFilterOperator>(GetString(json, "operator"), true),
            json.TryGetProperty("value", out var value) ? JsonElementToValue(value) : null);
    }

    private static IReadOnlyList<FieldId> ParseIds(JsonElement op, string property) =>
        op.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(x => new FieldId(x.GetUInt64())).ToArray()
            : Array.Empty<FieldId>();

    private static ulong GetULong(JsonElement obj, string property) => obj.GetProperty(property).GetUInt64();

    private static string GetString(JsonElement obj, string property) => obj.GetProperty(property).GetString() ??
                                                                         throw new ArgumentException(
                                                                             $"'{property}' is required.");

    private static object? JsonElementToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
        JsonValueKind.String => value.GetString(),
        _ => value.Clone()
    };
}
