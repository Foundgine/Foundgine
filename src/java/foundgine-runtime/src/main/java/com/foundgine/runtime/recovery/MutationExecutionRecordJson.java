package com.foundgine.runtime.recovery;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.foundgine.core.execution.ExecutionReceipt;
import java.time.Instant;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.List;

/**
 * Canonical JSON form of a {@link MutationExecutionRecord} ({@code foundgine-mutation-ledger-v1}). One record is one
 * line in the file ledger. The .NET and Java runtimes read and write the same form: camelCase names in a fixed order,
 * UTC timestamps as {@code yyyy-MM-ddTHH:mm:ss.fffffffZ} (100 ns, the .NET tick), no payloads.
 */
public final class MutationExecutionRecordJson {
    public static final String FORMAT = "foundgine-mutation-ledger-v1";
    private static final ObjectMapper MAPPER = new ObjectMapper();
    private static final DateTimeFormatter TS =
            DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSS'Z'").withZone(ZoneOffset.UTC);

    private MutationExecutionRecordJson() {}

    public static String formatTimestamp(Instant value) {
        return value == null ? null : TS.format(Instant.ofEpochSecond(value.getEpochSecond(), value.getNano() / 100 * 100));
    }

    public static String toJson(MutationExecutionRecord r) {
        ObjectNode n = MAPPER.createObjectNode();
        n.put("format", FORMAT);
        putText(n, "tenant", r.tenant());
        putText(n, "subject", r.subject());
        n.put("idempotencyKey", r.idempotencyKey());
        n.put("requestId", r.requestId());
        n.put("planFingerprint", r.planFingerprint());
        putText(n, "correlationId", r.correlationId());
        n.put("state", r.state().name());
        n.put("attempt", r.attempt());
        n.put("version", r.version());
        n.put("startedAtUtc", formatTimestamp(r.startedAt()));
        n.put("updatedAtUtc", formatTimestamp(r.updatedAt()));
        putText(n, "leaseExpiresAtUtc", formatTimestamp(r.leaseExpiresAt()));
        ArrayNode rows = n.putArray("affectedRows");
        r.affectedRows().forEach(rows::add);
        n.put("auditAcknowledged", r.auditAcknowledged());
        putText(n, "resolution", r.resolution());
        n.set("receipt", receipt(r.receipt()));
        try {
            return MAPPER.writeValueAsString(n);
        } catch (Exception e) {
            throw new IllegalStateException("Unable to serialize the ledger record.", e);
        }
    }

    public static MutationExecutionRecord fromJson(String json) {
        JsonNode n;
        try {
            n = MAPPER.readTree(json);
        } catch (Exception e) {
            throw new IllegalArgumentException("Ledger record is not valid JSON.", e);
        }
        if (n == null || !n.isObject() || !FORMAT.equals(text(n, "format")))
            throw new IllegalArgumentException("Unsupported ledger record format.");
        List<Integer> rows = new ArrayList<>();
        for (JsonNode x : n.path("affectedRows")) rows.add(x.asInt());
        return new MutationExecutionRecord(
                text(n, "tenant"), text(n, "subject"), required(n, "idempotencyKey"), required(n, "requestId"),
                required(n, "planFingerprint"), text(n, "correlationId"),
                MutationExecutionState.valueOf(required(n, "state")), n.path("attempt").asInt(),
                n.path("version").asLong(), Instant.parse(required(n, "startedAtUtc")),
                Instant.parse(required(n, "updatedAtUtc")), instant(n, "leaseExpiresAtUtc"),
                receipt(n.path("receipt")), rows, n.path("auditAcknowledged").asBoolean(false),
                text(n, "resolution"));
    }

    private static ObjectNode receipt(ExecutionReceipt r) {
        ObjectNode n = MAPPER.createObjectNode();
        n.put("requestId", r.requestId());
        n.put("status", r.status());
        n.put("semanticModelVersion", r.semanticModelVersion());
        n.put("capabilityContractVersion", r.capabilityContractVersion());
        n.put("capabilityVersion", r.capabilityVersion());
        n.put("intentVersion", r.intentVersion());
        n.put("planVersion", r.planVersion());
        n.put("intentFingerprint", r.intentFingerprint());
        n.put("planFingerprint", r.planFingerprint());
        n.put("authorizationFingerprint", r.authorizationFingerprint());
        n.put("provider", r.provider());
        n.put("startedAtUtc", formatTimestamp(r.startedAt()));
        n.put("completedAtUtc", formatTimestamp(r.completedAt()));
        ArrayNode nodes = n.putArray("affectedNodeIds");
        r.affectedNodeIds().forEach(nodes::add);
        ArrayNode effects = n.putArray("effects");
        r.effects().forEach(effects::add);
        n.put("resultFingerprint", r.resultFingerprint());
        putText(n, "approvalId", r.approvalId());
        putText(n, "approvedBy", r.approvedBy());
        putText(n, "approvedAtUtc", formatTimestamp(r.approvedAt()));
        putText(n, "warrantId", r.warrantId());
        putText(n, "warrantDigest", r.warrantDigest());
        putText(n, "securityInvariantDigest", r.securityInvariantDigest());
        putText(n, "correlationId", r.correlationId());
        return n;
    }

    private static ExecutionReceipt receipt(JsonNode n) {
        if (!n.isObject()) throw new IllegalArgumentException("Ledger record has no receipt.");
        List<Integer> nodes = new ArrayList<>();
        for (JsonNode x : n.path("affectedNodeIds")) nodes.add(x.asInt());
        List<String> effects = new ArrayList<>();
        for (JsonNode x : n.path("effects")) effects.add(x.asText());
        return new ExecutionReceipt(
                required(n, "requestId"), required(n, "status"), required(n, "semanticModelVersion"),
                n.path("capabilityContractVersion").asInt(), n.path("capabilityVersion").asInt(),
                n.path("intentVersion").asInt(), n.path("planVersion").asInt(), required(n, "intentFingerprint"),
                required(n, "planFingerprint"), required(n, "authorizationFingerprint"), required(n, "provider"),
                Instant.parse(required(n, "startedAtUtc")), Instant.parse(required(n, "completedAtUtc")), nodes,
                effects, required(n, "resultFingerprint"), text(n, "approvalId"), text(n, "approvedBy"),
                instant(n, "approvedAtUtc"), text(n, "warrantId"), text(n, "warrantDigest"),
                text(n, "securityInvariantDigest"), text(n, "correlationId"));
    }

    private static void putText(ObjectNode n, String name, String value) {
        if (value == null) n.putNull(name); else n.put(name, value);
    }

    private static String text(JsonNode n, String name) {
        JsonNode v = n.get(name);
        return v == null || v.isNull() ? null : v.asText();
    }

    private static String required(JsonNode n, String name) {
        String v = text(n, name);
        if (v == null) throw new IllegalArgumentException("Ledger record is missing '" + name + "'.");
        return v;
    }

    private static Instant instant(JsonNode n, String name) {
        String v = text(n, name);
        return v == null ? null : Instant.parse(v);
    }
}
