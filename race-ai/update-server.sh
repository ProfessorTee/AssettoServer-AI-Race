#!/usr/bin/env bash
# Updates an installed test server with the RaceAiPlugin of this repository (after a git pull):
#   - installs server-build/RaceAiPlugin.dll (safe while the server is running: copy + rename)
#   - updates the Nordschleife grid file (start spots, pit boxes, timing sectors)
#   - adds new options to plugin_race_ai_cfg.yml and removes old ones (a backup is kept)
# Usage: race-ai/update-server.sh [server folder]   (default: ../server next to the repository, else race-ai/server)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SRV="${1:-}"
if [ -z "$SRV" ]; then
  if [ -d "$REPO/../server/plugins" ]; then SRV="$(cd "$REPO/../server" && pwd)"; else SRV="$HERE/server"; fi
fi
[ -d "$SRV/cfg" ] || { echo "No server found in $SRV"; exit 1; }
EXAMPLE="$REPO/RaceAiPlugin/example/nordschleife-gt3/cfg"

mkdir -p "$SRV/plugins/RaceAiPlugin"
cp -f "$HERE/server-build/RaceAiPlugin.dll" "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll.new"
mv -f "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll.new" "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll"
[ -e "$SRV/cfg/ks_nordschleife-nordschleife-grid.json" ] && cp -f "$EXAMPLE/ks_nordschleife-nordschleife-grid.json" "$SRV/cfg/"

CFG="$SRV/cfg/plugin_race_ai_cfg.yml"
cp -f "$CFG" "$CFG.bak-$(date +%Y%m%d-%H%M%S)"
python3 - "$CFG" "$EXAMPLE/plugin_race_ai_cfg.yml" <<'PY'
import re, sys
cfg, example = sys.argv[1], sys.argv[2]
s = open(cfg).read(); ex = open(example).read()
# options that don't exist any more
for key in ["VirtualWetTyres", "WetTyres"]:
    # whole lines only (with their comment lines above)
    s = re.sub(r"(?m)^(#[^\n]*\n)*" + key + r":[^\n]*\n", "", s)
s = re.sub(r"(?m)^[A-Za-z]*# ", "# ", s)  # repair lines broken by the first version of this script
# new top-level options: copy them with their comment block from the example, in front of "# Race craft" (or at the end)
def blocks(text):
    out, comment = [], []
    for line in text.splitlines(keepends=True):
        if line.startswith("#"): comment.append(line); continue
        m = re.match(r"^([A-Za-z]+):", line)
        if m: out.append((m.group(1), comment, [line])); comment = []
        elif out and (line.startswith(" ") or line.startswith("-")): out[-1][2].append(line)
        else: comment = []
    return out
have = {k for k, _, _ in blocks(s)}
add = "".join("".join(c) + "".join(l) for k, c, l in blocks(ex) if k not in have)
if add:
    s = s.replace("# Race craft", add + "# Race craft", 1) if "# Race craft" in s else s + add
    print("added:", ", ".join(k for k, _, _ in blocks(ex) if k not in have))
open(cfg, "w").write(s)
PY
echo "Race AI plugin updated in $SRV. Restart the server."
