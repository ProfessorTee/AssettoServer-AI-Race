#!/bin/bash
REPO=/home/claude/AssettoServer
EXT=/home/claude/work/release/extracted
OUT=/home/claude/work/cc/out
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1)
AN=$(ls -d /usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/analyzers/dotnet/cs | head -1)
cat > globalusings.cs <<'G'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
G
REFS=$(ls $EXT/*.dll | grep -v "/AssettoServer.dll$" | sed 's/^/-r:/' | tr '\n' ' ')
find $REPO/RaceAiPlugin -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" > pfiles.txt
dotnet $CSC -noconfig -nostdlib -nologo -target:library -langversion:preview -nullable:enable -nowarn:1701,1702 -warnaserror- \
  -features:runtime-async=on -out:$OUT/RaceAiPlugin.dll -resource:$REPO/RaceAiPlugin/Dashboard/index.html,RaceAiPlugin.Dashboard.index.html -resource:$REPO/RaceAiPlugin/Dashboard/driverswap.lua,RaceAiPlugin.Dashboard.driverswap.lua -resource:$REPO/race-ai/classes/classes.json,RaceAiPlugin.classes.json \
  -analyzer:$AN/System.Text.RegularExpressions.Generator.dll \
  -r:$OUT/AssettoServer.dll $REFS globalusings.cs @pfiles.txt 2>&1 | grep -E "error|warning" | sort | uniq | head -60
find $REPO/DriverRecorderPlugin -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" > dfiles.txt
dotnet $CSC -noconfig -nostdlib -nologo -target:library -langversion:preview -nullable:enable -nowarn:1701,1702 -warnaserror- \
  -features:runtime-async=on -out:$OUT/DriverRecorderPlugin.dll -resource:$REPO/DriverRecorderPlugin/lua/driverrecorder.lua,DriverRecorderPlugin.lua.driverrecorder.lua \
  -analyzer:$AN/System.Text.RegularExpressions.Generator.dll \
  -r:$OUT/AssettoServer.dll $REFS globalusings.cs @dfiles.txt 2>&1 | grep -E "error|warning" | sort | uniq | head -60
ls -la $OUT
