#!/usr/bin/env bash
# Updates an installed test server with the plugins of this repository (after a git pull):
#   - the patched AssettoServer core when it changed
#   - installs server-build/<plugin>.dll of BotDriver, WebPortal, ServerTools, DriverRecorder (safe while the server is running)
#   - updates the Nordschleife grid file (start spots, pit boxes, timing sectors)
#   - adds new options to the plugins' yml files (a backup is kept)
# Usage: tools/update-server.sh [server folder]   (default: ../server next to the repository, else tools/server)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SRV="${1:-}"
if [ -z "$SRV" ]; then
  if [ -d "$REPO/../server/plugins" ]; then SRV="$(cd "$REPO/../server" && pwd)"; else SRV="$HERE/server"; fi
fi
[ -d "$SRV/cfg" ] || { echo "No server found in $SRV"; exit 1; }
EXAMPLE="$REPO/examples/nordschleife-gt3/cfg"

# patched AssettoServer core (only when it changed): the SDK build of tools/build.sh if there is one,
# else rebuild the server binary from the official one with server-build/AssettoServer.dll
OUT="$REPO/out-linux-x64"
if [ -x "$OUT/AssettoServer" ]; then
  NEW_SHA=$(sha256sum "$OUT/AssettoServer" | cut -d' ' -f1)
  if [ "$(cat "$SRV/.core-bin.sha" 2>/dev/null)" != "$NEW_SHA" ]; then
    cp -f "$OUT/AssettoServer" "$SRV/AssettoServer.new" && mv -f "$SRV/AssettoServer.new" "$SRV/AssettoServer" \
      && echo "$NEW_SHA" > "$SRV/.core-bin.sha" && echo "AssettoServer core updated (SDK build)"
  fi
  for p in BotDriverPlugin WebPortalPlugin ServerToolsPlugin DriverRecorderPlugin; do
    [ -f "$OUT/plugins/$p/$p.dll" ] && cp -f "$OUT/plugins/$p/$p.dll" "$HERE/server-build/"
  done
elif [ -f "$SRV/AssettoServer.official" ] && [ -f "$HERE/server-build/AssettoServer.dll" ]; then
  NEW_SHA=$(sha256sum "$HERE/server-build/AssettoServer.dll" | cut -d' ' -f1)
  if [ "$(cat "$SRV/.core-dll.sha" 2>/dev/null)" != "$NEW_SHA" ]; then
    python3 "$HERE/rebundle.py" "$SRV/AssettoServer.official" "$SRV/AssettoServer.new" "AssettoServer.dll=$HERE/server-build/AssettoServer.dll" \
      && chmod +x "$SRV/AssettoServer.new" && mv -f "$SRV/AssettoServer.new" "$SRV/AssettoServer" && echo "$NEW_SHA" > "$SRV/.core-dll.sha" \
      && echo "AssettoServer core updated"
  fi
fi

# vehicle classes (presets/classes/<class>/): new ones are added, your own changes are kept
for d in "$REPO/ServerToolsPlugin/presets/classes"/*/; do
  [ -e "$SRV/presets/classes/$(basename "$d")" ] || { mkdir -p "$SRV/presets/classes"; cp -r "$d" "$SRV/presets/classes/"; }
done

# the plugins, installed next to the running file and renamed: safe even while a server is running
for p in BotDriverPlugin WebPortalPlugin ServerToolsPlugin DriverRecorderPlugin; do
  [ -f "$HERE/server-build/$p.dll" ] || continue
  mkdir -p "$SRV/plugins/$p"
  cp -f "$HERE/server-build/$p.dll" "$SRV/plugins/$p/$p.dll.new"
  mv -f "$SRV/plugins/$p/$p.dll.new" "$SRV/plugins/$p/$p.dll"
done
# the Race AI plugin was split into the plugins above ("RaceAiPlugin" in EnablePlugins loads them, its settings are taken over)
rm -rf "$SRV/plugins/RaceAiPlugin"
[ -e "$SRV/cfg/plugin_driver_recorder_cfg.yml" ] || cp "$EXAMPLE/plugin_driver_recorder_cfg.yml" "$SRV/cfg/"
[ -e "$SRV/cfg/ks_nordschleife-nordschleife-grid.json" ] && cp -f "$EXAMPLE/ks_nordschleife-nordschleife-grid.json" "$SRV/cfg/"

# new options of the plugins: copied with their comment block from the example into the server's files (a backup is kept).
# A file the server doesn't have yet is made by the server itself on the next start.
for f in plugin_bot_driver_cfg.yml plugin_server_tools_cfg.yml plugin_web_portal_cfg.yml; do
  CFG="$SRV/cfg/$f"
  [ -f "$CFG" ] && [ -f "$EXAMPLE/$f" ] || continue
  python3 - "$CFG" "$EXAMPLE/$f" <<'PY'
import re, sys, shutil, time
cfg, example = sys.argv[1], sys.argv[2]
s = open(cfg).read(); ex = open(example).read()
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
new = [(k, c, l) for k, c, l in blocks(ex) if k not in have]
if new:
    shutil.copy(cfg, cfg + time.strftime(".bak-%Y%m%d-%H%M%S"))
    s = s.rstrip("\n") + "\n" + "".join("".join(c) + "".join(l) for k, c, l in new)
    open(cfg, "w").write(s)
    print(cfg.split("/")[-1], "added:", ", ".join(k for k, _, _ in new))
PY
done
echo "Plugins updated in $SRV. Restart the server."
