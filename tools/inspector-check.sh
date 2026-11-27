#!/bin/sh
# inspector-check.sh : check the MCP server the way a client sees it, with the official MCP Inspector CLI (needs Node).
#
#   tools/inspector-check.sh            offline: fake APCOA credentials (no APCOA call is made) — CI-able
#   tools/inspector-check.sh --live     also calls get_vehicles with the live tests' account (user-secrets
#                                        apcoa-flow-api-tests, or APCOAFLOW_EMAIL / APCOAFLOW_PASSWORD)
#
# Checks: tools/list --strict is clean (schema portability: exit 0, empty stderr), the expected tool and prompt names,
# every tool has a title. Starts the HTTP host on a free port with throwaway dev settings and stops it afterwards.
# Read-only: never calls a tool that starts, extends or ends a session.
set -eu
cd "$(dirname "$0")/.."
EXPECTED_TOOLS="end_session extend_session find_parkings get_active_sessions get_parking_details get_price get_session_history get_vehicles start_expensive_session start_free_session start_free_session_here start_session start_session_here"
EXPECTED_PROMPTS="end_parking park_free_at park_free_here parking_costs"
live=false; [ "${1:-}" = "--live" ] && live=true
work=$(mktemp -d); trap 'kill $host 2>/dev/null || true; rm -rf "$work"' EXIT

port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
dotnet build src/ApcoaFlowMcp -v q -nologo >/dev/null
env ASPNETCORE_URLS="http://127.0.0.1:$port" Security__AllowedApiKeys__0=check-key \
    Security__TokenSecret=inspector-check-secret-at-least-32-chars \
    dotnet run --project src/ApcoaFlowMcp --no-build --no-launch-profile >"$work/host.log" 2>&1 &
host=$!
for _ in $(seq 1 60); do curl -s -o /dev/null "http://127.0.0.1:$port/.well-known/oauth-authorization-server" && break; sleep 1; done

if $live; then
  basic=$( { if [ -n "${APCOAFLOW_EMAIL:-}" ]; then printf 'ApcoaFlow:Email = %s\nApcoaFlow:Password = %s\n' "$APCOAFLOW_EMAIL" "$APCOAFLOW_PASSWORD";
             else dotnet user-secrets list --id apcoa-flow-api-tests; fi; } | python3 -c 'import sys,base64
d=dict(l.rstrip("\n").split(" = ",1) for l in sys.stdin if " = " in l)
print(base64.b64encode((d["ApcoaFlow:Email"]+":"+d["ApcoaFlow:Password"]).encode()).decode())')
else
  basic=$(printf 'nobody@example.com:not-used' | base64)
fi
I() { npx -y @modelcontextprotocol/inspector --cli "http://127.0.0.1:$port/mcp" --header "X-Api-Key: check-key" --header "Authorization: Basic $basic" "$@"; }

fail=0
I --method tools/list --strict >"$work/tools.json" 2>"$work/strict.err" || { echo "FAIL tools/list --strict exited non-zero"; fail=1; }
[ -s "$work/strict.err" ] && { echo "FAIL schema portability:"; cat "$work/strict.err"; fail=1; }
python3 - "$work/tools.json" "$EXPECTED_TOOLS" <<'PY' || fail=1
import json, sys
tools = json.load(open(sys.argv[1]))["tools"]
names = sorted(t["name"] for t in tools); expected = sorted(sys.argv[2].split())
untitled = [t["name"] for t in tools if not (t.get("annotations") or {}).get("title") and not t.get("title")]
ok = names == expected and not untitled
print(("ok  " if ok else "FAIL") + f" tools: {len(names)}" + ("" if names == expected else f" (expected {expected}, got {names})") + (f"; untitled: {untitled}" if untitled else ""))
sys.exit(0 if ok else 1)
PY
I --method prompts/list >"$work/prompts.json"
python3 - "$work/prompts.json" "$EXPECTED_PROMPTS" <<'PY' || fail=1
import json, sys
names = sorted(p["name"] for p in json.load(open(sys.argv[1]))["prompts"]); expected = sorted(sys.argv[2].split())
print(("ok  " if names == expected else "FAIL") + f" prompts: {names}")
sys.exit(0 if names == expected else 1)
PY
if $live; then
  I --method tools/call --tool-name get_vehicles >"$work/vehicles.json" 2>&1 || true
  python3 - "$work/vehicles.json" <<'PY' || fail=1
import json, sys
d = json.load(open(sys.argv[1]))
ok = not d.get("isError") and d["content"][0]["text"].strip() != ""
print(("ok  " if ok else "FAIL") + " get_vehicles (live)")
sys.exit(0 if ok else 1)
PY
fi
[ $fail -eq 0 ] && echo "inspector-check: all good" || { echo "inspector-check: FAILED (host log: $work/host.log)"; trap - EXIT; kill $host 2>/dev/null; exit 1; }
