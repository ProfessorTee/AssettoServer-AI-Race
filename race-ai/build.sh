#!/usr/bin/env bash
# Builds the patched server and the plugins with the .NET SDK into out-linux-x64/ (or out-win-x64/ with "win-x64"),
# and refreshes the prebuilt server-build/*.dll used by setup-testserver.sh / update-server.sh without an SDK.
# Usage: race-ai/build.sh [linux-x64|win-x64]
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
export PATH="$HOME/.dotnet:$PATH"
cd "$REPO"
for p in AssettoServer RaceAiPlugin DriverRecorderPlugin; do
  # MinVer takes the version from git tags, which this fork has none of
  dotnet publish "$p/$p.csproj" -c Release -r "$RID" -nologo -v q -p:MinVerVersionOverride="0.0.55-raceai+$(git rev-parse --short HEAD)"
done
cp -f "AssettoServer/bin/Release/net11.0/$RID/AssettoServer.dll" race-ai/server-build/
cp -f "out-$RID/plugins/RaceAiPlugin/RaceAiPlugin.dll" "out-$RID/plugins/DriverRecorderPlugin/DriverRecorderPlugin.dll" race-ai/server-build/
echo "built: out-$RID/  (server-build/ refreshed)"
