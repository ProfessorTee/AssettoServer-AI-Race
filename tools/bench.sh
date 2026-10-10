#!/usr/bin/env bash
# Bot benchmark: 12 races on Trial Mountain (3 laps) and 6 on the Nordschleife (1 lap), 20 JDM bots, in parallel.
# Prints held up (best lap vs strength target), passes, contacts and appends the result to tools/bench-results.tsv,
# then shows the last results as a table (before / after a change).
#
#   tools/bench.sh "label" [extra sim args]     e.g. tools/bench.sh "dive 0.985"
#   tools/bench.sh --table                       only the table
# Env: AC (game folder), MODELS, JOBS (parallel races, default: 4, pinned to cores 0-3), SEED (first seed - 1, default 0: another SEED shows the noise).
set -uo pipefail
cd "$(dirname "$0")/.."
RESULTS=tools/bench-results.tsv
table() {
    [ -f "$RESULTS" ] || { echo "no results yet"; return; }
    printf "%-10s %-16s %-28s %8s %6s %8s %8s %6s %8s\n" date commit label "TM held" passes contacts "NS held" passes contacts
    tail -n "${1:-10}" "$RESULTS" | awk -F'\t' '{printf "%-10s %-16s %-28s %8s %6s %8s %8s %6s %8s\n", $1, $2, substr($3,1,28), $4, $5, $6, $7, $8, $9}'
}
if [ "${1:-}" = "--table" ]; then table 20; exit 0; fi
LABEL="${1:-}"; shift || true
export PATH=$HOME/.dotnet:$PATH
AC="${AC:-/mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa}"
MODELS="${MODELS:-ks_toyota_supra_mkiv,ks_nissan_skyline_r34,ks_mazda_rx7_spirit_r,ks_nissan_370z}"
JOBS="${JOBS:-4}"
SEED="${SEED:-0}"
nice -n 19 taskset -c 0-3 dotnet build BotDriverPlugin/Tool -c Release -v q >/dev/null || { echo "build failed"; exit 1; }
TMP=$(mktemp -d)
TOOL=$(ls BotDriverPlugin/Tool/bin/Release/*/BotDriverTool.dll | head -1)
run() { # track layout laps seed (the dll directly: parallel "dotnet run"s get in each other's way)
    nice -n 19 taskset -c 0-3 dotnet "$TOOL" sim --ac "$AC" --track "$AC/content/tracks/$1" --layout "$2" \
        --models "$MODELS" --bots 20 --laps "$3" --style --seed "$4" "${EXTRA[@]}" > "$TMP/$1-$4.txt" 2>&1
}
export -f run; export AC MODELS TMP TOOL
EXTRA=("$@")
# (not "... | while": the loop would run in a subshell and "wait" wouldn't wait for its races)
while read -r t l n s; do
    while [ "$(jobs -rp | wc -l)" -ge "$JOBS" ]; do sleep 0.2; done
    run "$t" "$l" "$n" "$s" &
done < <(for s in $(seq $((SEED + 1)) $((SEED + 12))); do echo "trialmountain forward 3 $s"; done
         for s in $(seq $((SEED + 1)) $((SEED + 6))); do echo "ks_nordschleife nordschleife 1 $s"; done)
wait
summary=$(python3 - "$TMP" <<'PY'
import sys, re, glob
def summ(prefix):
    h = []; p = c = 0
    for f in glob.glob(f"{sys.argv[1]}/{prefix}-*.txt"):
        t = open(f).read()
        m = re.search(r'held up: best lap vs strength target avg \+?(-?[0-9.]+)', t)
        if not m: continue
        h.append(float(m.group(1)))
        p += sum(int(x) for x in re.findall(r'end:passed (\d+)', t))
        c += sum(int(x) for x in re.findall(r'contacts +(\d+) +grass', t))
    return f"{sum(h)/max(1,len(h)):.2f}\t{p}\t{c}"
print(summ("trialmountain") + "\t" + summ("ks_nordschleife"))
PY
)
[ -n "${KEEP:-}" ] && echo "kept $TMP" || rm -rf "$TMP"
printf "%s\t%s\t%s\t%s\n" "$(date +%F)" "$(git rev-parse --short HEAD)$(git diff --quiet || echo +)" "${LABEL:-${EXTRA[*]:-}}$([ "$SEED" != 0 ] && echo " (seeds +$SEED)")" "$summary" >> "$RESULTS"
table 10
