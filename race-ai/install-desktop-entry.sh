#!/usr/bin/env bash
# Adds "Race AI Server" to the application menu (and the desktop, if there is one).
set -euo pipefail
HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$APPS"
FILE="$APPS/raceai-server.desktop"
cat > "$FILE" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Race AI Server
GenericName=AssettoServer mit Renn-KI
Comment=Server starten, Bots steuern, Live-Karte
Exec="$HERE/raceai-desktop.sh"
Icon=$HERE/raceai-icon.svg
Terminal=false
Categories=Game;
StartupWMClass=RaceAI
DESKTOP
chmod +x "$FILE" "$HERE/raceai-desktop.sh"
DESK="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
if [ -d "$DESK" ]; then
  cp -f "$FILE" "$DESK/raceai-server.desktop"
  chmod +x "$DESK/raceai-server.desktop"
  command -v gio >/dev/null && gio set "$DESK/raceai-server.desktop" metadata::trusted true 2>/dev/null || true
fi
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" 2>/dev/null || true
echo "Menüeintrag angelegt: $FILE"
[ -d "$DESK" ] && echo "Verknüpfung auf dem Desktop: $DESK/raceai-server.desktop"
