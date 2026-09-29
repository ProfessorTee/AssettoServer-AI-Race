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

# patched AssettoServer core (only when it changed): rebuild the server binary from the official one
if [ -f "$SRV/AssettoServer.official" ] && [ -f "$HERE/server-build/AssettoServer.dll" ]; then
  NEW_SHA=$(sha256sum "$HERE/server-build/AssettoServer.dll" | cut -d' ' -f1)
  if [ "$(cat "$SRV/.core-dll.sha" 2>/dev/null)" != "$NEW_SHA" ]; then
    python3 "$HERE/tools/rebundle.py" "$SRV/AssettoServer.official" "$SRV/AssettoServer.new" "AssettoServer.dll=$HERE/server-build/AssettoServer.dll" \
      && chmod +x "$SRV/AssettoServer.new" && mv -f "$SRV/AssettoServer.new" "$SRV/AssettoServer" && echo "$NEW_SHA" > "$SRV/.core-dll.sha" \
      && echo "AssettoServer core updated"
  fi
fi

mkdir -p "$SRV/plugins/RaceAiPlugin"
cp -f "$HERE/server-build/RaceAiPlugin.dll" "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll.new"
mv -f "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll.new" "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll"
if [ -f "$HERE/server-build/DriverRecorderPlugin.dll" ]; then
  mkdir -p "$SRV/plugins/DriverRecorderPlugin"
  cp -f "$HERE/server-build/DriverRecorderPlugin.dll" "$SRV/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll.new"
  mv -f "$SRV/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll.new" "$SRV/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll"
  [ -e "$SRV/cfg/plugin_driver_recorder_cfg.yml" ] || cp "$EXAMPLE/plugin_driver_recorder_cfg.yml" "$SRV/cfg/"
  # switch the plugin on once (a later "remove it from EnablePlugins" is respected: marker file)
  if [ ! -e "$SRV/cfg/.driverrecorder-enabled" ] && ! grep -q "DriverRecorderPlugin" "$SRV/cfg/extra_cfg.yml"; then
    python3 - "$SRV/cfg/extra_cfg.yml" <<'PY'
import re, sys
p = sys.argv[1]; s = open(p).read()
s = re.sub(r"(?m)^(EnablePlugins:\s*\n(?:[ \t]*-[^\n]*\n)*)", lambda m: m.group(1) + "  - DriverRecorderPlugin\n", s, count=1)
open(p, "w").write(s)
print("extra_cfg.yml: DriverRecorderPlugin enabled")
PY
    touch "$SRV/cfg/.driverrecorder-enabled"
  fi
fi
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
# personalities: LateBraking (0..1) became BrakeBehavior (-1..1) with Patience and LineErrors; the unchanged old default block
# is replaced by the new one with its explanations, a changed one only gets the key renamed
def section(text):
    # from the comment block above UsePersonalities to the end of the Personalities list
    i = text.find("# Driver personalities"); p = text.find("\nPersonalities:", i)
    if i < 0 or p < 0: return (-1, -1)
    m = re.compile(r"(?m)^[^ \n-]").search(text, p + 15)
    return (i, m.start() if m else len(text))
i, j = section(s); ei, ej = section(ex)
if i >= 0 and "LateBraking" in s[i:j]:
    old_default = [l.split("#")[0].strip() for l in s[i:j].splitlines() if l.strip() and not l.strip().startswith("#")]
    if old_default == ["UsePersonalities: true", "Personalities:", "- Name: Balanced", "Share: 40", "- Name: DiveBomber", "Share: 20",
                       "Aggression: 25", "LateBraking: 1", "InsideLine: 1", "TyreWear: 1.25", "FuelUse: 1.05", "Composure: 0.4",
                       "Weaving: 0.9", "Mistakes: 1.2", "- Name: Chill", "Share: 20", "Aggression: -20", "TyreWear: 0.75", "FuelUse: 0.97",
                       "Smoothness: 0.5", "Composure: 0.9", "Weaving: 0.1", "Mistakes: 0.8", "- Name: FuelSaver", "Share: 20",
                       "Aggression: -10", "TyreWear: 0.9", "FuelUse: 0.85", "Smoothness: 0.7", "Composure: 0.7", "Weaving: 0.2"] and ei >= 0:
        s = s[:i] + ex[ei:ej] + s[j:]
        print("personalities: updated to the new defaults (BrakeBehavior, Patience, LineErrors)")
    else:
        s = re.sub(r"(?m)^(\s*)LateBraking:", r"\1BrakeBehavior:", s)
        print("personalities: LateBraking renamed to BrakeBehavior")
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
