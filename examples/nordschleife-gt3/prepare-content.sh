#!/usr/bin/env bash
# Copies the files the server and the Race AI plugin need from your Assetto Corsa installation
# into the server's content folder (like Content Manager's "pack server data").
#   ./prepare-content.sh <assettocorsa folder> [server folder]
# Example:
#   ./prepare-content.sh /mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa .
set -euo pipefail
AC="${1:?path to the assettocorsa folder}"
SERVER="${2:-.}"
TRACK=ks_nordschleife
LAYOUT=nordschleife
CFG="$SERVER/cfg"

[ -d "$AC/content" ] || { echo "No content folder in $AC"; exit 1; }
mkdir -p "$SERVER/content/cars" "$SERVER/content/tracks/$TRACK/$LAYOUT" "$SERVER/system/data"

# cars from entry_list.ini: data.acd (checksums + bot physics), collider.kn5, ui_car.json
for car in $(grep -i '^MODEL=' "$CFG/entry_list.ini" | cut -d= -f2 | tr -d '\r' | sort -u); do
  src="$AC/content/cars/$car"
  if [ ! -d "$src" ]; then echo "WARNING: car $car not installed"; continue; fi
  mkdir -p "$SERVER/content/cars/$car/ui"
  cp -u "$src/data.acd" "$SERVER/content/cars/$car/" 2>/dev/null || echo "WARNING: $car has no data.acd"
  [ -f "$src/collider.kn5" ] && cp -u "$src/collider.kn5" "$SERVER/content/cars/$car/"
  [ -f "$src/ui/ui_car.json" ] && cp -u "$src/ui/ui_car.json" "$SERVER/content/cars/$car/ui/"
  echo "car $car"
done

# track: checksum files, racing line, AI hints, sections (the large kn5 files are not needed)
T="$AC/content/tracks/$TRACK"
cp -u "$T/models_$LAYOUT.ini" "$SERVER/content/tracks/$TRACK/"
cp -ru "$T/$LAYOUT/ai" "$T/$LAYOUT/data" "$SERVER/content/tracks/$TRACK/$LAYOUT/"
[ -f "$AC/system/data/surfaces.ini" ] && cp -u "$AC/system/data/surfaces.ini" "$SERVER/system/data/"
echo "track $TRACK/$LAYOUT"
echo "done"
