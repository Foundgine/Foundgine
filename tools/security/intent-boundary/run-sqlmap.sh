#!/usr/bin/env bash
# Run sqlmap against a LOCAL Foundgine Supply Chain sample, with injection points marked on the intent's
# value, order direction and limit slots (the `*` markers in request-template.txt).
#
# Safety: this script refuses any host that is not loopback. Only point it at a sample you started yourself.
#
#   Java sample:  mvn -f src/java/pom.xml -pl samples/foundgine-supply-chain-advanced exec:java \
#                   -Dexec.mainClass=com.foundgine.samples.supplychain.advanced.mcp.AdvancedMcpServer
#                 (listens on localhost:4783, POST /intent, identity from X-Foundgine-* headers)
#   C# sample:    see src/csharp/samples/Foundgine.SupplyChain.Advanced/README.md ("Agent HTTP surface").
#                 Its route and identity mechanism differ; set INTENT_PATH and adjust the headers in the
#                 template before running.
#
# Usage: ./run-sqlmap.sh [BASE_URL]      (default http://localhost:4783)
# Env:   INTENT_PATH (default /intent)  TENANT (tenant-a)  ROLE (analyst)  LEVEL (5)  RISK (3)
set -euo pipefail

BASE_URL="${1:-http://localhost:4783}"
INTENT_PATH="${INTENT_PATH:-/intent}"
TENANT="${TENANT:-tenant-a}"
ROLE="${ROLE:-analyst}"
LEVEL="${LEVEL:-5}"
RISK="${RISK:-3}"

if ! [[ "$BASE_URL" =~ ^https?://(localhost|127\.0\.0\.1|\[::1\])(:[0-9]+)?/?$ ]]; then
  echo "Refusing to test '$BASE_URL': only loopback targets are allowed." >&2
  exit 2
fi
command -v sqlmap >/dev/null || { echo "sqlmap not found on PATH (https://sqlmap.org)." >&2; exit 3; }

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOSTPORT="${BASE_URL#*://}"; HOSTPORT="${HOSTPORT%/}"
OUT="${OUT:-$HERE/reports}"
mkdir -p "$OUT"
REQ="$OUT/request.txt"

sed -e "s#__PATH__#$INTENT_PATH#" -e "s#__HOSTPORT__#$HOSTPORT#" \
    -e "s#__TENANT__#$TENANT#" -e "s#__ROLE__#$ROLE#" "$HERE/request-template.txt" > "$REQ"

# Sanity: the unmodified request must succeed, or every result below is meaningless.
STATUS="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE_URL$INTENT_PATH" \
  -H 'Content-Type: application/json' -H "X-Foundgine-Tenant: $TENANT" -H "X-Foundgine-Role: $ROLE" \
  --data '{"rootEntity":"Product","selections":[{"field":"Sku"}],"limit":1}' || true)"
[[ "$STATUS" == "200" ]] || { echo "Baseline request returned HTTP $STATUS, not 200. Is the sample running?" >&2; exit 4; }

# 400/403 are the EXPECTED answers to hostile input, so they must not abort the scan.
sqlmap -r "$REQ" --batch --level "$LEVEL" --risk "$RISK" \
  --ignore-code 400,403 --flush-session --output-dir "$OUT/sqlmap" "${@:2}"

echo
echo "Done. Reports in $OUT/sqlmap. Read README.md before drawing conclusions from a clean result."
