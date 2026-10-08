#!/usr/bin/env bash
# Starts the Race AI server and opens the dashboard (live map, bot control) in its own window.
#   tools/start-server.sh            -> server + admin page (no console needed)
#   tools/start-server.sh --console  -> only the server in the terminal
HERE="$(dirname "$(readlink -f "$0")")"
if [ "${1:-}" = "--console" ]; then
  shift
  exec "$HERE/../ServerToolsPlugin/scripts/server-supervisor.sh" "$HERE/server" "$@"
fi
exec "$HERE/server-desktop.sh" "$@"
