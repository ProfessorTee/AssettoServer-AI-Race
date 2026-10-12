#!/usr/bin/env bash
# Overtaking test: a few bots (BOTS, default 4) evenly spread, slowest first: how fast does the fastest one get to the front?
# Many seeds, so the result says something (the full-field bench is chaotic: +-30 % from run to run).
#   tools/passtest.sh [label]     env: N (seeds, default 40), GAP (strength spread, default "3 1.5"), TRACKS ("trialmountain:forward:2")
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH=$HOME/.dotnet:$PATH
AC="${AC:-/mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa}"
MODELS="${MODELS:-ks_toyota_supra_mkiv,ks_nissan_skyline_r34,ks_mazda_rx7_spirit_r,ks_nissan_370z}"
N="${N:-40}"; GAPS="${GAP:-3 1.5}"; TRACKS="${TRACKS:-trialmountain:forward:2 ks_nordschleife:nordschleife:1}"
nice -n 19 dotnet build BotDriverPlugin/Tool -c Release -v q >/dev/null || { echo "build failed"; exit 1; }
TOOL=$(ls BotDriverPlugin/Tool/bin/Release/*/BotDriverTool.dll | head -1)
TMP=$(mktemp -d)
for tr in $TRACKS; do IFS=: read -r t l laps <<< "$tr"
  for g in $GAPS; do
    for s in $(seq 1 "$N"); do
      while [ "$(jobs -rp | wc -l)" -ge $(nproc) ]; do sleep 0.1; done
      nice -n 19 dotnet "$TOOL" sim --ac "$AC" --track "$AC/content/tracks/$t" --layout "$l" --models "$MODELS" --bots "${BOTS:-4}" \
        --laps "$laps" --strength 91 --spread "$g" --slowest-first --seed "$s" > "$TMP/$t-$g-$s.txt" 2>&1 &
    done
  done
done
wait
python3 - "$TMP" "${1:-}" <<'PY'
import sys, glob, re, collections
res = collections.defaultdict(lambda: [0, 0, 0, []])  # runs, faster one ahead at the end, contacts, first pass times
for f in glob.glob(sys.argv[1] + "/*.txt"):
    t, g, s = f.split("/")[-1][:-4].rsplit("-", 2)
    txt = open(f).read()
    rows = re.findall(r'^(\d+)\s+.+?\s{2,}(\d+)\s+\d+\s+[\d:.]+', txt, re.M)
    if len(rows) < 2: continue
    lv = {int(p): int(l) for p, l in rows}
    r = res[(t, g)]; r[0] += 1
    if lv[1] == max(lv.values()): r[1] += 1
    r[2] += sum(int(x) for x in re.findall(r'contacts (\d+) ghosts', txt))
    m = re.search(r'first pass: ([\d.]+|never)', txt)
    r[3].append(float(m.group(1)) if m and m.group(1) != "never" else float("inf"))
print(f"passtest {sys.argv[2]}")
for (t, g), (n, p, c, ft) in sorted(res.items()):
    ft.sort(); done = [x for x in ft if x != float("inf")]
    print(f"  {t:16} {float(g)*2:4.0f} % apart: in front {len(done):3}/{n}, median {ft[len(ft)//2]:6.0f} s, first at the end {p:3}/{n}, contacts {c}")
PY
rm -rf "$TMP"
