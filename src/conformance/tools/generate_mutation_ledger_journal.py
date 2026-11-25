#!/usr/bin/env python3
"""Mutation ledger journal for cross-language conformance (see ../README.md).

Regenerate with:  python3 tools/generate_mutation_ledger_journal.py   (writes ../mutation-ledger-journal.jsonl and
../mutation-ledger-scenarios.json next to this script's parent directory)

`foundgine-mutation-ledger-v1`: one JSON record per line, camelCase names in the fixed order below, compact separators,
UTC timestamps with 7 fractional digits, null fields written as null. The journal is append-only; for one identity the
line with the highest version wins. A runtime that reads, rewrites and re-reads these lines must reproduce every line
byte for byte and arrive at the final records listed in the scenarios file.
"""
import json, os

T = lambda s: '2026-01-02T03:04:%s.1234567Z' % s

def receipt(request_id, status, corr):
    return {
        'requestId': request_id, 'status': status, 'semanticModelVersion': 'sm-1', 'capabilityContractVersion': 1,
        'capabilityVersion': 1, 'intentVersion': 1, 'planVersion': 1, 'intentFingerprint': 'intent-fp',
        'planFingerprint': 'plan-fp', 'authorizationFingerprint': 'auth-fp', 'provider': 'provider',
        'startedAtUtc': T('05'), 'completedAtUtc': T('05'), 'affectedNodeIds': [1, 2], 'effects': ['write'],
        'resultFingerprint': 'result-fp', 'approvalId': None, 'approvedBy': None, 'approvedAtUtc': None,
        'warrantId': None, 'warrantDigest': None, 'securityInvariantDigest': None, 'correlationId': corr}

def record(tenant, subject, key, rid, state, version, rows, resolution, status, lease='07', audit=False):
    return {
        'format': 'foundgine-mutation-ledger-v1', 'tenant': tenant, 'subject': subject, 'idempotencyKey': key,
        'requestId': rid, 'planFingerprint': 'plan-fp', 'correlationId': 'corr-1', 'state': state, 'attempt': 1,
        'version': version, 'startedAtUtc': T('05'), 'updatedAtUtc': T('06' if version > 1 else '05'),
        'leaseExpiresAtUtc': T(lease) if lease else None, 'affectedRows': rows, 'auditAcknowledged': audit,
        'resolution': resolution, 'receipt': receipt(rid, status, 'corr-1')}

LINES = [
    record('t1', 's1', 'k-committed', 'r-1', 'STARTED', 1, [], None, 'pending'),
    record(None, None, 'k-null-scope', 'r-2', 'STARTED', 1, [], None, 'pending'),
    record('t1', 's1', 'k-started', 'r-3', 'STARTED', 1, [], None, 'pending'),
    record('t1', 's1', 'k-committed', 'r-1', 'COMMITTED', 2, [1], 'done', 'succeeded', lease=None, audit=True),
    record(None, None, 'k-null-scope', 'r-2', 'IN_DOUBT', 2, [], 'provider failed after dispatch', 'pending'),
]

def dump(o):
    return json.dumps(o, separators=(',', ':'), ensure_ascii=False)

def final(lines):
    best = {}
    for r in lines:
        k = (r['tenant'], r['subject'], r['idempotencyKey'])
        if k not in best or r['version'] >= best[k]['version']:
            best[k] = r
    return [{'tenant': k[0], 'subject': k[1], 'idempotencyKey': k[2], 'requestId': r['requestId'],
             'state': r['state'], 'version': r['version'], 'affectedRows': r['affectedRows'],
             'resolution': r['resolution']} for k, r in best.items()]

if __name__ == '__main__':
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
    with open(os.path.join(out, 'mutation-ledger-journal.jsonl'), 'w', encoding='utf-8', newline='\n') as f:
        for r in LINES: f.write(dump(r) + '\n')
    scenarios = {
        'format': 'foundgine-mutation-ledger-v1',
        'journal': 'mutation-ledger-journal.jsonl',
        'finalRecords': final(LINES),
        'unresolved': ['k-null-scope', 'k-started'],
        'tornTail': {'append': '{"format":"foundgine-mutation-ledger-v1","tenant":"t1"', 'finalRecordsUnchanged': True},
        'corruptLine': {'replaceLine': 1, 'with': 'not json', 'expect': 'fails closed'}}
    with open(os.path.join(out, 'mutation-ledger-scenarios.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(scenarios, f, indent=2); f.write('\n')
