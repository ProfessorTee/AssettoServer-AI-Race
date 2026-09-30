#!/usr/bin/env bash
# Adds a track to the track rotation: a preset presets/<name>/ made from your current cfg/ (same cars, same settings),
# the track files the server needs, and the entry in rotation.yml.
#   race-ai/add-track-preset.sh <assettocorsa folder> <track> [layout] [preset name] [server folder]
# Example:
#   race-ai/add-track-preset.sh /mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa trialmountain forward
set -euo pipefail
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
AC="${1:?assettocorsa folder}"
TRACK="${2:?track folder name, e.g. trialmountain}"
LAYOUT="${3:-}"
NAME="${4:-$TRACK}"
SRV="${5:-}"
if [ -z "$SRV" ]; then
  if [ -d "$REPO/../server/cfg" ]; then SRV="$(cd "$REPO/../server" && pwd)"; else SRV="$HERE/server"; fi
fi
T="$AC/content/tracks/$TRACK"
[ -d "$T" ] || { echo "Track not found: $T"; exit 1; }
[ -z "$LAYOUT" ] || [ -d "$T/$LAYOUT" ] || { echo "Layout not found: $T/$LAYOUT"; exit 1; }
AI="$T${LAYOUT:+/$LAYOUT}/ai"
[ -f "$AI/fast_lane.ai" ] || { echo "No ai/fast_lane.ai: the Race AI can't drive this track"; exit 1; }
[ -f "$AI/pit_lane.ai" ] || echo "Note: no ai/pit_lane.ai, the bots won't make pit stops here"

# server content: checksum files, racing line, pit lane, data (no big kn5 files needed)
DST="$SRV/content/tracks/$TRACK"
mkdir -p "$DST${LAYOUT:+/$LAYOUT}"
cp -ru "$AI" "$T${LAYOUT:+/$LAYOUT}/data" "$DST${LAYOUT:+/$LAYOUT}/"
if [ -n "$LAYOUT" ]; then [ -f "$T/models_$LAYOUT.ini" ] && cp -u "$T/models_$LAYOUT.ini" "$DST/"; else [ -f "$T/models.ini" ] && cp -u "$T/models.ini" "$DST/"; fi

# the preset: a copy of cfg/ with the other track
P="$SRV/presets/$NAME"
mkdir -p "$P"
for f in "$SRV"/cfg/*.ini "$SRV"/cfg/*.yml "$SRV"/cfg/*.txt; do
  [ -e "$f" ] || continue
  case "$(basename "$f")" in *.bak*|*grid.json) continue;; esac
  [ -e "$P/$(basename "$f")" ] || cp "$f" "$P/"
done
UI="$T/ui${LAYOUT:+/$LAYOUT}/ui_track.json"
TITLE="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1],encoding='utf-8-sig')).get('name','$TRACK'))" "$UI" 2>/dev/null || echo "$TRACK")"
python3 - "$P" "$TRACK" "$LAYOUT" "$TITLE" <<'PY'
import re, sys
p, track, layout, title = sys.argv[1:]
s = open(f"{p}/server_cfg.ini", encoding="utf-8").read()
s = re.sub(r"(?m)^TRACK=.*$", f"TRACK={track}", s)
s = re.sub(r"(?m)^CONFIG_TRACK=.*$", f"CONFIG_TRACK={layout}", s)
# the welcome message of a preset is read relative to the preset folder
s = re.sub(r"(?m)^WELCOME_MESSAGE=cfg/", "WELCOME_MESSAGE=", s)
open(f"{p}/server_cfg.ini", "w", encoding="utf-8").write(s)
c = open(f"{p}/plugin_race_ai_cfg.yml", encoding="utf-8").read()
# start positions and pit boxes are read from the track's kn5 files (AssettoCorsaPath) unless a grid file for this track exists
c = re.sub(r"(?m)^GridFile:.*$", "GridFile: ''", c)
open(f"{p}/plugin_race_ai_cfg.yml", "w", encoding="utf-8").write(c)
print(f"preset {p}: {title}")
PY

# rotation.yml
R="$SRV/rotation.yml"
if [ ! -f "$R" ]; then
  cat > "$R" <<EOF
# Track rotation (Race AI): the server restarts with the next track, players with CSP are reconnected automatically.
# Tracks: preset folders in presets/ ("default" = the cfg/ folder), in this order
Enabled: true
Tracks:
  - default
# Change the track after this many finished races (0 = only by time)
RacesPerTrack: 1
# ... or after this many minutes (0 = off); only between sessions, never during a race
MinutesPerTrack: 0
# Random order instead of the list (never the same track twice in a row)
Random: false
# Nobody online and it's due: change right away
ChangeWhenEmpty: true
# Chat warning this many seconds before the change
AnnounceSeconds: 20
# Seconds the players wait for a track that never started before (later the measured start time is used)
FirstStartSeconds: 60
# Names for chat and welcome message
Titles:
  default: $(grep -m1 '^TRACK=' "$SRV/cfg/server_cfg.ini" | cut -d= -f2 | tr -d '\r')
EOF
fi
python3 - "$R" "$NAME" "$TITLE" <<'PY'
import re, sys
p, name, title = sys.argv[1:]
s = open(p, encoding="utf-8").read()
if not re.search(rf"(?m)^\s*-\s*{re.escape(name)}\s*$", s):
    s = re.sub(r"(?m)^(Tracks:\s*\n(?:\s*-[^\n]*\n)*)", lambda m: m.group(1) + f"  - {name}\n", s, count=1)
if not re.search(rf"(?m)^\s+{re.escape(name)}:", s):
    s = re.sub(r"(?m)^(Titles:\s*\n(?:\s+[^\n]*\n)*)", lambda m: m.group(1) + f"  {name}: {title}\n", s, count=1)
open(p, "w", encoding="utf-8").write(s)
PY
echo "rotation.yml: $(grep -A10 '^Tracks:' "$R" | grep -- '- ' | tr -d ' ' | tr '\n' ' ')"
echo "Done. Restart the server."
