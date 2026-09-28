#!/usr/bin/env bash
# Runs the AssettoServer and starts it again when the dashboard asks for a restart
# (the server writes restart.request and stops: "restart" = just start again,
# "update" = git pull + race-ai/update-server.sh first, then start again).
# A normal stop (dashboard "Server beenden", Ctrl+C, crash) ends this script too.
# Usage: race-ai/server-supervisor.sh <server folder>     (used by raceai-desktop.sh and start-server.sh)
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SRV="$(cd "${1:?server folder}" && pwd)"
cd "$SRV" || exit 1
mkdir -p logs
export RACEAI_SUPERVISED=1

child=0
trap '[ "$child" != 0 ] && kill -TERM "$child" 2>/dev/null; wait "$child" 2>/dev/null; exit 0' INT TERM

while true; do
  rm -f restart.request
  ./AssettoServer "$@" &
  child=$!
  wait "$child"
  code=$?
  child=0
  [ -f restart.request ] || exit "$code"
  mode="$(cat restart.request 2>/dev/null)"
  rm -f restart.request
  if [ "$mode" = "update" ]; then
    echo "[$(date '+%H:%M:%S')] Race AI: updating from GitHub …" | tee -a logs/console.log
    if git -C "$REPO" pull --ff-only 2>&1 | tee -a logs/console.log; then
      bash "$HERE/update-server.sh" "$SRV" 2>&1 | tee -a logs/console.log
    else
      echo "[$(date '+%H:%M:%S')] Race AI: update failed (see above), starting the old version" | tee -a logs/console.log
    fi
  fi
  echo "[$(date '+%H:%M:%S')] Race AI: restarting the server" | tee -a logs/console.log
  sleep 1
done
