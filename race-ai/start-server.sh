#!/usr/bin/env bash
# Starts the Race AI server and opens the dashboard (live map, bot control) in its own window.
#   race-ai/start-server.sh            -> server + dashboard (no console needed)
#   race-ai/start-server.sh --console  -> only the server in the terminal
HERE="$(dirname "$(readlink -f "$0")")"
if [ "${1:-}" = "--console" ]; then
  shift
  cd "$HERE/server" && exec ./AssettoServer "$@"
fi
exec "$HERE/raceai-desktop.sh" "$@"
