#!/usr/bin/env bash
# Checks the Windows app reads saved variables exactly as the Python app does (sync/perpetua_sync.py): same
# profiles, loot, rolls and roster from the same Perpetua.lua. Usage: tests/parity.sh <Perpetua.lua> [perpetua_sync.py]
set -euo pipefail
cd "$(dirname "$0")/.."
LUA_FILE="$1"
PY="${2:-../sync/perpetua_sync.py}"
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 dotnet run --project tests/PerpetuaSync.Tests -- --dump "$LUA_FILE" > "$OUT/cs.json"
python3 -I - "$PY" "$LUA_FILE" > "$OUT/py.json" <<'PYEOF'
import importlib.util, json, sys
spec = importlib.util.spec_from_file_location("ps", sys.argv[1]); ps = importlib.util.module_from_spec(spec); spec.loader.exec_module(ps)
files = [sys.argv[2]]
profiles = {n: d for n, (d, g, p) in sorted(ps.collect(files).items())}
loot, rolls = ps.collect_loot(files)
at, roster = ps.collect_roster(files)
members = []
for full, r in sorted(roster.items()):
    if not isinstance(r, dict): continue
    name, _, surname = full.partition(" ")
    members.append({"n": name, "s": surname or None, "l": r.get("l"), "c": r.get("c"), "t": r.get("s")})
print(json.dumps({"profiles": profiles, "loot": loot, "rolls": rolls, "roster_at": at, "roster": members}))
PYEOF
python3 -I - "$OUT/cs.json" "$OUT/py.json" <<'PYEOF'
import json, sys
a, b = (json.load(open(f)) for f in sys.argv[1:3])
def norm(x):  # 5.0 and 5 are the same number in JSON
    if isinstance(x, float) and x.is_integer(): return int(x)
    if isinstance(x, dict): return {k: norm(v) for k, v in x.items()}
    if isinstance(x, list): return [norm(v) for v in x]
    return x
a, b = norm(a), norm(b)
for k in b:
    same = json.dumps(a.get(k), sort_keys=True) == json.dumps(b[k], sort_keys=True)
    n = len(b[k]) if isinstance(b[k], (list, dict)) else b[k]
    print(f"  {'ok  ' if same else 'DIFF'} {k} ({n})")
    if not same and isinstance(b[k], dict):
        for name in sorted(set(a[k]) | set(b[k])):
            if json.dumps(a[k].get(name), sort_keys=True) != json.dumps(b[k].get(name), sort_keys=True): print("       differs:", name)
sys.exit(0 if json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True) else 1)
PYEOF
echo "PARITY OK"
