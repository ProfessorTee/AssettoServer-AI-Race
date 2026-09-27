#!/bin/bash
set -e
REPO=/home/claude/AssettoServer
EXT=/home/claude/work/release/extracted
OUT=/home/claude/work/cc/out
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1)
AN=$(ls -d /usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/analyzers/dotnet/cs | head -1)
rm -rf src && mkdir -p src $OUT
cp -r $REPO/AssettoServer src/
# replace Mvvm [ObservableProperty] partial properties by plain ones (check build only)
python3 - <<'PY'
import re
p='src/AssettoServer/Server/Configuration/Extra/AiParams.cs'
s=open(p).read()
def f(m):
    typ,name,rest=m.group(1),m.group(2),m.group(3) or ''
    prop=name[1].upper()+name[2:]
    return f'public {typ} {prop} {{ get; set; }}{rest}' + (';' if rest else '')
s=re.sub(r'\[ObservableProperty\]\s*(?:\[property:[^\n]*\]\s*)?private (\S+) (_\w+)( = [^;]+)?;', f, s)
open(p,'w').write(s)
PY
REFS=$(ls $EXT/*.dll | grep -v "/AssettoServer.dll$" | sed 's/^/-r:/' | tr '\n' ' ')
find src/AssettoServer -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" > files.txt
cat > src/asminfo.cs <<'A'
[assembly: System.Reflection.AssemblyInformationalVersion("0.0.55-raceai+local")]
[assembly: System.Reflection.AssemblyVersion("0.0.55.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AssettoServer.Tests")]
A
echo src/asminfo.cs >> files.txt
S=src/AssettoServer
RES="-resource:$S/Server/Ai/ai_debug.lua,AssettoServer.Server.Ai.ai_debug.lua -resource:$S/Assets/logo_42.png,AssettoServer.Assets.logo_42.png -resource:$S/Assets/srp-logo-new.png,AssettoServer.Assets.srp-logo-new.png -resource:$S/Assets/crash_report.md.tpl,AssettoServer.Assets.crash_report.md.tpl -resource:$S/Assets/server_cfg.ini,AssettoServer.Assets.server_cfg.ini -resource:$S/Assets/entry_list.ini,AssettoServer.Assets.entry_list.ini"
dotnet $CSC -noconfig -nostdlib -nologo -langversion:preview -unsafe+ -nullable:enable -nowarn:1701,1702,CS8618,CS1591 \
  -features:runtime-async=on -out:$OUT/AssettoServer.dll \
  -analyzer:$AN/System.Text.RegularExpressions.Generator.dll -analyzer:$AN/Microsoft.Interop.LibraryImportGenerator.dll -analyzer:$AN/Microsoft.Interop.SourceGeneration.dll -analyzer:$AN/System.Text.Json.SourceGeneration.dll \
  -resource:src/AssettoServer/Server/Lua/assettoserver.lua,AssettoServer.Server.Lua.assettoserver.lua $RES -target:exe -main:AssettoServer.Program \
  $REFS @files.txt 2>&1 | grep -E "error|warning CS8" | grep -v "warning" | sort | uniq | head -60
echo "exit: done"; ls -la $OUT
