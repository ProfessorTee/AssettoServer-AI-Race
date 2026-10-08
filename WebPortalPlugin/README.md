# WebPortalPlugin – Webseiten des Servers

Join-Seite, Live-Timing mit Karte, Bestzeiten und Admin-Seite. Läuft allein mit dem, was der Server weiß (Session, Wetter,
Spieler, Runden, eigene Streckenkarte aus `fast_lane.ai`). Mit anderen Plugins zeigt es mehr:

| Plugin | zusätzlich |
|---|---|
| BotDriverPlugin | Bots in Karte und Timing, KI-Tab der Admin-Seite (`/api/bots/...`) |
| ServerToolsPlugin | Lizenz/Safety Rating im Live-Timing, Statistik-Seite, Rotation, Klasse, Echtwetter, Neustart (`/api/tools/...`) |

| Seite | Zugriff |
|---|---|
| `/` | leitet auf `/join` oder `/live` (`LandingPage`) |
| `/join`, `/live`, `/stats` | öffentlich |
| `/admin` | nur vom Server-PC, mit `DashboardRemoteAccess: true` auch von außen mit ADMIN_PASSWORD |

Konfiguration: `plugin_web_portal_cfg.yml`. Gemeinsame Einstellungen (Sprache, AC-Pfad, öffentliche Adresse) setzt ServerToolsPlugin; hier überschreibbar.

## Desktop-Oberfläche (Dashboard)

`tools/server-desktop.sh` startet den Server und öffnet das Dashboard in einem eigenen Fenster (App-Modus von
Chromium, Chrome, Brave oder Edge; sonst ein Browser-Tab). `tools/install-desktop-entry.sh` legt „Race AI Server“ im
Startmenü und auf dem Desktop an. Beim Schließen des Fensters fragt der Starter, ob der Server beendet werden soll.

Das Dashboard läuft im Server selbst unter `http://127.0.0.1:<HTTP_PORT>/admin`, standardmäßig nur vom Server-Rechner aus
(`DashboardRemoteAccess`). Es bietet:
- **Live-Karte:** Strecke, Boxengasse, Sektoren, Streckenabschnitte. Alle Autos mit Position, Farbe nach Persönlichkeit, Spieler blau.
  Zoomen mit dem Mausrad, Verschieben durch Ziehen, Doppelklick zeigt die ganze Strecke, „Folgen“ verfolgt ein Auto.
- **Fahrerfeld:** Position, Runden, beste und letzte Runde, Tempo, Sprit, Reifen, Status (Box, Dreher, Rutscher, Geist, pendelt,
  unter Druck, Schaden). Ein Klick auf einen Bot öffnet die Einstellungen für Stärke, Aggressivität und Persönlichkeit, dazu
  „In die Box schicken“. Spieler lassen sich kicken.
- **KI:** Stärke, Streuung und Aggressivität für das ganze Feld. Alle Funktionen als Schalter (Fehler, Dreher, Gras, Berührungen,
  Schaden, Flaggen, Licht, Regen, echtes Wetter, Chat-Meldungen). Dazu der Licht-Test.
- **Server:** nächste Session oder Neustart, Tageszeit, CSP-Wetter, Regen/Nässe/Wasser, Luft- und Streckentemperatur, Grip,
  Chat an alle. Server beenden/neu starten macht beim Hoster das Panel; nur mit `tools/start-server.sh` (eigener PC)
  gibt es „Server neu starten“ und „Update holen & neu starten“.
- **Admin-Befehle:** alle Server-Befehle mit Vorlagen (kick, ban, ballast, restrict, forcelights, pit, whois, set, whitelist …),
  mit Antwort. Bei Spielern gibt es Knöpfe für Ballast, Restriktor, Box, Licht erzwingen, Kicken und Bannen.
- **Log:** live, mit Filter. Wer nach unten scrollt, bleibt dort stehen, auch wenn oben neue Zeilen dazukommen.
- **Beitreten:** Content-Manager-Link (öffnet CM und verbindet), Adresse zum Kopieren und die öffentliche Seite
  `http://<IP>:<HTTP_PORT>/join` für Freunde (ohne Passwort; mit Anleitung für den Original-Launcher, der keine
  Beitritts-Links kennt). Die öffentliche IP wird automatisch ermittelt oder mit `PublicAddress` (z. B. DynDNS) festgelegt.
- **Einstellungen bleiben:** was im Dashboard oder per Admin-Befehl verstellt wird (Stärke, Streuung, Aggressivität, Startaufstellung,
  Schalter, Bot-Anzahl), schreibt das Plugin in `plugin_bot_driver_cfg.yml` (die Zeile wird ersetzt, Kommentare bleiben). Steht der
  Schlüssel im Preset der aktuellen Strecke, landet er dort, sonst in `cfg/` (gilt dann für alle Strecken).
- **Neustart aus der Ferne:** „Server neu starten“ und „Update holen & neu starten“ (git pull + `update-server.sh`). Das geht,
  wenn der Server über `tools/start-server.sh` bzw. das Desktop-Symbol läuft: `ServerToolsPlugin/scripts/server-supervisor.sh` startet ihn neu.
- Oben rechts lassen sich die Datenrate (1–10 pro Sekunde) und die Bildrate der Karte (10–60 fps) einstellen. Weniger heißt
  weniger Last, wenn Spiel und Dashboard auf demselben PC laufen. Die Strecke wird nur neu gezeichnet, wenn sich die Ansicht ändert.

## Live-Timing (öffentlich)
`http://<server>:<HTTP_PORT>/live`: Streckenkarte (ganze Strecke oder einem Auto folgen), Zeitenturm mit Abstand/Intervall
(Zeitmesspunkte alle ~20 m, überrundete mit „+1 Rd.“), Box, letzte Runde (lila = schnellste der Session, grün = persönliche Bestzeit),
Lizenz der Spieler, Tempo/Gang/Gas/Bremse/Drehzahl eines Autos mit Tempo-Kurve (diese und letzte Runde), Ereignisse (Start, Führung,
schnellste Runde, Boxenstopps). Die Daten kommen als Server-Sent Events (`/api/admin/live/stream`, Takt `LiveViewHz`, Standard 5/s),
werden nur berechnet solange jemand zuschaut, einmal pro Takt für alle Zuschauer (30 Zuschauer ≈ 3 % eines Kerns). Ohne Stream fragt die
Seite `/api/admin/live/state` jede Sekunde ab. Keine Admin-Daten (keine Steam-IDs, keine KI-Interna). `LiveView: false` schaltet ab.

