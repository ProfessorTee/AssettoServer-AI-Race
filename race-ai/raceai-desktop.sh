#!/usr/bin/env bash
# Race AI desktop app: starts the AssettoServer (if it isn't running yet) and opens the dashboard in its own window.
# Closing the window asks whether the server should be stopped (only when this script started it).
# Usage: race-ai/raceai-desktop.sh [server folder]   (default: ../server next to the repository, else race-ai/server)
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SRV="${1:-}"
if [ -z "$SRV" ]; then
  if [ -d "$REPO/../server/cfg" ]; then SRV="$(cd "$REPO/../server" && pwd)"; else SRV="$HERE/server"; fi
fi

msg() { # message box if possible, else terminal / notification
  local kind="$1"; shift
  if command -v zenity >/dev/null; then zenity --"$kind" --title="Race AI Server" --text="$*" --width=520 2>/dev/null
  elif command -v kdialog >/dev/null; then kdialog --title "Race AI Server" --"$([ "$kind" = error ] && echo error || echo msgbox)" "$*" 2>/dev/null
  elif command -v notify-send >/dev/null; then notify-send "Race AI Server" "$*"
  else echo "$*"; fi
}
ask() { # yes/no question, default yes when no dialog tool is available
  if command -v zenity >/dev/null; then zenity --question --title="Race AI Server" --text="$*" --width=420 2>/dev/null
  elif command -v kdialog >/dev/null; then kdialog --title "Race AI Server" --yesno "$*" 2>/dev/null
  else return 0; fi
}

[ -x "$SRV/AssettoServer" ] || { msg error "Kein Server gefunden in:\n$SRV\n\nErst race-ai/setup-testserver.sh ausführen."; exit 1; }
PORT=$(grep -E '^HTTP_PORT=' "$SRV/cfg/server_cfg.ini" 2>/dev/null | head -1 | cut -d= -f2 | tr -d '\r ')
URL="http://127.0.0.1:${PORT:-8081}/raceai"
ping() { curl -fs -m 2 "$URL/api/ping" >/dev/null 2>&1; }

STARTED=0
if ! ping; then
  mkdir -p "$SRV/logs"
  cd "$SRV" || exit 1
  START_TIME=$(date +%s)
  setsid ./AssettoServer >> "$SRV/logs/console.log" 2>&1 < /dev/null &
  SERVER_PID=$!
  STARTED=1
  command -v notify-send >/dev/null && notify-send "Race AI Server" "Server startet … (die Bots werden kalibriert, etwa 30 s)"
  for _ in $(seq 1 180); do
    ping && break
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      CRASH=$(ls -t "$SRV"/crash_*.txt 2>/dev/null | head -1)
      DETAIL=""
      if [ -n "$CRASH" ] && [ "$(stat -c %Y "$CRASH")" -ge "$START_TIME" ]; then
        DETAIL=$(grep -m1 -A2 -E "Exception|Error" "$CRASH" | cut -c1-300)
      else
        DETAIL=$(tail -n 5 "$SRV/logs/console.log" | cut -c1-300)
      fi
      msg error "Der Server ist beim Start abgestürzt.\n\n$DETAIL\n\n${CRASH:-$SRV/logs/console.log}"
      exit 1
    fi
    sleep 1
  done
  ping || { msg error "Der Server antwortet nicht auf $URL"; exit 1; }
fi

# the dashboard in its own window (Chromium-based browsers have an app mode without tabs and address bar)
PROFILE="$HOME/.cache/raceai-dashboard"
APP_ARGS=(--app="$URL" --user-data-dir="$PROFILE" --class=RaceAI --window-size=1500,900 --no-first-run --no-default-browser-check)
OPENED=0
for b in chromium chromium-browser google-chrome-stable google-chrome brave-browser brave microsoft-edge vivaldi; do
  if command -v "$b" >/dev/null; then "$b" "${APP_ARGS[@]}" >/dev/null 2>&1; OPENED=1; break; fi
done
if [ "$OPENED" = 0 ] && command -v flatpak >/dev/null; then
  for app in org.chromium.Chromium com.google.Chrome com.brave.Browser com.microsoft.Edge; do
    if flatpak info "$app" >/dev/null 2>&1; then flatpak run "$app" "${APP_ARGS[@]}" >/dev/null 2>&1; OPENED=1; break; fi
  done
fi
if [ "$OPENED" = 0 ]; then
  # no app window possible (e.g. only Firefox): open a normal tab; the server keeps running, stop it in the dashboard
  xdg-open "$URL" >/dev/null 2>&1 &
  msg info "Das Dashboard ist im Browser geöffnet:\n$URL\n\nDer Server läuft weiter, bis du ihn im Dashboard unter „Server“ beendest."
  exit 0
fi

# window closed
if [ "$STARTED" = 1 ] && ping; then
  if ask "Dashboard geschlossen.\n\nServer beenden?\n(Nein = der Server läuft im Hintergrund weiter, Dashboard wieder über das Startmenü)"; then
    curl -fs -m 3 -X POST -H 'Content-Type: application/json' -d '{"action":"stop"}' "$URL/api/server" >/dev/null 2>&1
    for _ in $(seq 1 20); do kill -0 "$SERVER_PID" 2>/dev/null || break; sleep 0.5; done
    kill -0 "$SERVER_PID" 2>/dev/null && kill -TERM "$SERVER_PID"
  fi
fi
