#!/usr/bin/env bash
# Adds a track to the track rotation: presets/tracks/<name>/ (only the track: TRACK, layout, weather, title, pit box limit;
# the cars come from the class in presets/classes/, everything else from cfg/), the track files the server needs, and the entry in rotation.yml.
#   ServerToolsPlugin/scripts/add-track-preset.sh <assettocorsa folder> <track> [layout] [preset name] [server folder]
# Example:
#   ServerToolsPlugin/scripts/add-track-preset.sh /mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa trialmountain forward
set -euo pipefail
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
AC="${1:?assettocorsa folder}"
TRACK="${2:?track folder name, e.g. trialmountain}"
LAYOUT="${3:-}"
NAME="${4:-$TRACK}"
SRV="${5:-}"
if [ -z "$SRV" ]; then
  if [ -d "$REPO/../server/cfg" ]; then SRV="$(cd "$REPO/../server" && pwd)"; else SRV="$REPO/tools/server"; fi
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

# the track layer: only the track (cars come from the class, everything else from cfg/)
P="$SRV/presets/tracks/$NAME"
mkdir -p "$P"
UI="$T/ui${LAYOUT:+/$LAYOUT}/ui_track.json"
read -r TITLE PITS < <(python3 - "$UI" "$TRACK" <<'PY0'
import json, sys
try:
    d = json.load(open(sys.argv[1], encoding="utf-8-sig"))
except Exception:
    d = {}
print(d.get("name", sys.argv[2]).replace(" ", "\u00a0"), d.get("pitboxes", ""))
PY0
)
TITLE="${TITLE//$'\u00a0'/ }"
python3 - "$P" "$TRACK" "$LAYOUT" "$TITLE" "$PITS" "$SRV/cfg/server_cfg.ini" <<'PY2'
import os, re, sys
p, track, layout, title, pits, main = sys.argv[1:]
if not os.path.exists(f"{p}/server_cfg.ini"):
    m = open(main, encoding="utf-8").read()
    weather = "\n".join(x.strip() + "\n" for x in re.findall(r"(?ms)^\[WEATHER_\d+\].*?(?=^\[|\Z)", m))
    clients = re.search(r"(?m)^MAX_CLIENTS=(\d+)", m)
    # not more cars than pit boxes: the class's entry list is cut to MAX_CLIENTS
    limit = f"MAX_CLIENTS={pits}\n" if pits.isdigit() and clients and int(pits) < int(clients.group(1)) else ""
    open(f"{p}/server_cfg.ini", "w", encoding="utf-8").write(
        f"; only what differs from cfg/server_cfg.ini\n[SERVER]\nTRACK={track}\nCONFIG_TRACK={layout}\n{limit}\n{weather}\n"
        f"[PRESET]\nTRACK_TITLE={title}\n")
if not os.path.exists(f"{p}/plugin_race_ai_cfg.yml"):
    # start positions and pit boxes are read from the track's kn5 files (AssettoCorsaPath) unless a grid file for this track exists
    open(f"{p}/plugin_race_ai_cfg.yml", "w", encoding="utf-8").write(
        "# only what differs from cfg/plugin_race_ai_cfg.yml\nGridFile: ''\n")
print(f"track {p}: {title}" + (f", {pits} pit boxes" if pits else ""))
PY2

# rotation.yml
R="$SRV/rotation.yml"
if [ ! -f "$R" ]; then
  cat > "$R" <<EOF
# Track rotation (Race AI): the server restarts with the next track, players with CSP are reconnected automatically.
# Tracks: folders in presets/tracks/, in this order ("nordschleife+lmp1" = always with that class)
Enabled: true
Tracks: []
# Vehicle class for every track (folder in presets/classes/); /server_class <class> changes it. Empty = cars of cfg/entry_list.ini
Class:
# Change the track after this many finished races (0 = only by time)
RacesPerTrack: 1
# Different number for single tracks (the others use RacesPerTrack), e.g. Races: { trialmountain: 3, default: 2 }
Races: {}
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
EOF
fi
python3 - "$R" "$NAME" <<'PY'
import re, sys
p, name = sys.argv[1:]
s = open(p, encoding="utf-8").read()
s = re.sub(r"(?m)^Tracks:\s*\[\]\s*$", "Tracks:", s)
if not re.search(rf"(?m)^\s*-\s*{re.escape(name)}(\+\S*)?\s*$", s):
    s = re.sub(r"(?m)^(Tracks:\s*\n(?:\s*-[^\n]*\n)*)", lambda m: m.group(1) + f"  - {name}\n", s, count=1)
open(p, "w", encoding="utf-8").write(s)
PY
echo "rotation.yml: $(grep -A10 '^Tracks:' "$R" | grep -- '- ' | tr -d ' ' | tr '\n' ' ')"
echo "Done. Restart the server."
