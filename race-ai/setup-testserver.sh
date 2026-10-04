#!/usr/bin/env bash
# Builds a runnable TEST server (linux-x64) with the Race AI patch, without a .NET SDK:
#   1. downloads the official AssettoServer release v0.0.55-pre42 (the patch was compiled against exactly these libraries)
#   2. swaps AssettoServer.dll inside the single-file bundle for the patched one (server-build/AssettoServer.dll)
#   3. installs the RaceAiPlugin, the Nordschleife/GT3 example config and copies the needed content from your AC folder
# Usage: race-ai/setup-testserver.sh [assettocorsa folder]   -> server in race-ai/server
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
AC="${1:-$HOME/.steam/steam/steamapps/common/assettocorsa}"
SRV="$HERE/server"
TAG="v0.0.55-pre42"
EXAMPLE="$REPO/RaceAiPlugin/example/nordschleife-gt3"

mkdir -p "$SRV" "$HERE/.cache"
TARBALL="$HERE/.cache/assetto-server-linux-x64-$TAG.tar.gz"
if [ ! -s "$TARBALL" ]; then
  echo "Downloading AssettoServer $TAG ..."
  curl -fL -o "$TARBALL" "https://github.com/compujuckel/AssettoServer/releases/download/$TAG/assetto-server-linux-x64.tar.gz"
fi
tar xzf "$TARBALL" -C "$SRV"
mv -f "$SRV/AssettoServer" "$SRV/AssettoServer.official"
python3 "$HERE/tools/rebundle.py" "$SRV/AssettoServer.official" "$SRV/AssettoServer" "AssettoServer.dll=$HERE/server-build/AssettoServer.dll"

mkdir -p "$SRV/plugins/RaceAiPlugin" "$SRV/cfg"
# install next to the running file and rename: safe even while a server is running
cp -f "$HERE/server-build/RaceAiPlugin.dll" "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll.new"
mv -f "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll.new" "$SRV/plugins/RaceAiPlugin/RaceAiPlugin.dll"
mkdir -p "$SRV/plugins/DriverRecorderPlugin"
cp -f "$HERE/server-build/DriverRecorderPlugin.dll" "$SRV/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll.new"
mv -f "$SRV/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll.new" "$SRV/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll"
# example configuration (existing files are kept, so your own changes survive a re-run)
for f in "$EXAMPLE"/cfg/*; do
  [ -e "$SRV/cfg/$(basename "$f")" ] || cp "$f" "$SRV/cfg/"
done
sed -i "s|^AssettoCorsaPath:.*|AssettoCorsaPath: $AC|" "$SRV/cfg/plugin_race_ai_cfg.yml"
# vehicle classes (presets/classes/<class>/): new ones are added, your own changes are kept
for d in "$REPO/race-ai/presets/classes"/*/; do
  [ -e "$SRV/presets/classes/$(basename "$d")" ] || { mkdir -p "$SRV/presets/classes"; cp -r "$d" "$SRV/presets/classes/"; }
done

bash "$EXAMPLE/prepare-content.sh" "$AC" "$SRV"
echo
echo "Test server ready: $HERE/start-server.sh"
