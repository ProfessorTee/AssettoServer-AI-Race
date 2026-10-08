# ServerToolsPlugin – Server-Werkzeuge

Strecken-Rotation, Fahrzeugklassen, Spielerstatistik und Safety Rating, Rennergebnis im Chat, echtes Wetter, Bot-/Spielerzahl
im Servernamen, Neustart. Läuft allein; stellt die gemeinsamen Einstellungen (`ChatLanguage`, `AssettoCorsaPath`,
`PublicAddress`) für die anderen Plugins bereit.

Konfiguration: `plugin_server_tools_cfg.yml`, Rotation und Klasse in `rotation.yml`.

| Befehl | |
|---|---|
| `/server_nexttrack [strecke]` | Streckenwechsel (alt: `/server_nexttrack`) |
| `/server_class [klasse] [next]` | Fahrzeugklasse (alt: `/server_class`, `/class`) |
| `/server_set realweather on/off` | Echtes Wetter |
| `/server_restart [update]` | Neustart (Update nur mit Supervisor-Skript) |
| `/top [all]`, `/profile [name]` | Bestzeiten, Profil mit Safety Rating |

Skripte: `scripts/server-supervisor.sh` (Neustart/Update), `scripts/add-track-preset.sh` (Strecke zur Rotation),
`scripts/migrate-presets.py`. Mitgelieferte Klassen: `presets/classes/`.

## Statistik, Safety Rating, Wiedereinstieg

- Jede saubere Runde (ohne Cut) zählt für die Bestzeiten: pro Strecke und Auto die Allzeit-Bestzeit und die der Woche
  (ISO-Woche, die letzten 8 bleiben). Gespeichert in `stats/players.json` im Server-Ordner (alle 30 s).
- **Safety Rating 0–5:** Vorfälle je 10 km (Kontakt mit einem Auto 2–6 Punkte je nach Tempo, Streckenrand 1–2, Cut 1),
  neuere Kilometer zählen mehr (Halbwertszeit 300 km). A ab 4,0 · B ab 3,0 · C ab 2,0 · D ab 1,0 · R darunter; bewertet ab 20 km.
- Öffentliche Seite ohne Passwort: `http://<IP>:<HTTP_PORT>/stats` (Link auch im Dashboard unter „Beitreten“).
## Name in der Server-Liste

Mit Bots zeigt die Server-Liste (Content Manager, CSP, Kunos-Lobby) automatisch `Bots:16,Player:2 - <Name aus server_cfg.ini>`.
Ein- und ausschalten mit `ServerNameCounts`, das Muster steht in `ServerNameFormat` (`{bots}`, `{players}`, `{name}`).
Die Lobby bekommt einen neuen Namen spätestens nach einer Minute; im Spiel selbst bleibt der normale Name.

## Strecken-Rotation

Der Server kann nach einer Anzahl Rennen (oder Minuten) die Strecke wechseln. Er startet sich dabei selbst mit der nächsten
Strecke neu. Spieler mit **CSP** bekommen ein Banner mit Countdown, danach treten sie über Content Manager neu bei. (Ein automatisches Neu-Verbinden
geht nicht: CSP behält dabei die geladene Strecke, das Spiel stürzt mit der neuen ab.)
Gewechselt wird nur zwischen zwei Sessions, nie mitten im Rennen.

### Aufbau: Basis, Strecke, Klasse

Strecke und Fahrzeugklasse sind getrennt und werden beim Start übereinandergelegt. Jede Schicht enthält nur, was anders ist:

```
cfg/                              Basis: Passwörter, Sessions, Plugins, KI-Einstellungen, Begrüßung, Beschreibung
presets/tracks/<strecke>/         TRACK, CONFIG_TRACK, Wetter, MAX_CLIENTS (Boxen), GridFile, Titel
presets/classes/<klasse>/         entry_list.ini (Autos, Skins, Spieler- und Bot-Plätze), CARS=, Titel
```

Preset-Name `nordschleife+gte` = `cfg/` → `presets/tracks/nordschleife/` → `presets/classes/gte/`. Ohne Klasse (`nordschleife`)
kommen die Autos aus `cfg/entry_list.ini`. `server_cfg.ini` wird Wert für Wert zusammengelegt (die `[WEATHER_x]`-Blöcke einer Schicht
ersetzen die darunter ganz), `.yml`-Dateien ebenfalls. Jede andere Datei kommt aus der obersten Schicht, die sie hat.

Die Entry List einer Klasse hat 24 Plätze. Hat eine Strecke weniger Boxen, setzt ihr `MAX_CLIENTS` die Grenze: Die hinteren Bot-Plätze fallen weg.

`{track}` und `{class}` in `NAME`, im Begrüßungstext und in `ServerDescription` werden durch die Titel ersetzt
(`[PRESET] TRACK_TITLE` der Strecke, `[PRESET] CLASS_TITLE` der Klasse). Beispiel:

```ini
; cfg/server_cfg.ini
[SERVER]
NAME={track} {class} vs Race AI

; presets/tracks/trialmountain/server_cfg.ini
[SERVER]
TRACK=trialmountain
CONFIG_TRACK=forward
MAX_CLIENTS=20

[WEATHER_0]
GRAPHICS=3_clear

[PRESET]
TRACK_TITLE=Trial Mountain

; presets/classes/gte/server_cfg.ini
[SERVER]
CARS=ks_corvette_c7r;ks_porsche_911_rsr_2017;ferrari_458_gt2;bmw_m3_gt2

[PRESET]
CLASS_TITLE=GTE
```

Neue Strecke hinzufügen (legt `presets/tracks/<name>/` an, kopiert die nötigen Streckendateien und trägt sie in `rotation.yml` ein):

```
ServerToolsPlugin/scripts/add-track-preset.sh <AC-Ordner> trialmountain forward
```

Alten Server (mit `presets/<strecke>-<klasse>/`-Ordnern) einmalig umstellen; das alte `presets/` bleibt als `presets.bak-<datum>/` erhalten:

```
python3 ServerToolsPlugin/scripts/migrate-presets.py <Serverordner> --dry-run   # nur anzeigen
python3 ServerToolsPlugin/scripts/migrate-presets.py <Serverordner>
```

`rotation.yml` im Server-Ordner:

```yaml
Enabled: true
Tracks: [nordschleife, trialmountain]   # Reihenfolge; "nordschleife+lmp1" = diese Strecke immer mit LMP1
Class: gte                         # Klasse für alle Strecken (leer = Autos aus cfg/entry_list.ini)
RacesPerTrack: 3                   # wechseln nach so vielen Rennen (0 = nur nach Zeit)
Races: { trialmountain: 5 }        # einzelne Strecken mit eigener Anzahl (optional)
MinutesPerTrack: 0                 # oder nach so vielen Minuten (0 = aus)
Random: false                      # zufällige Reihenfolge
ChangeWhenEmpty: true              # niemand online und fällig: sofort wechseln
AnnounceSeconds: 20                # Chat-Hinweis vorher
ResultSeconds: 30                  # nach einem Rennen so lange warten (Ergebnis ansehen), dann erst wechseln
FirstStartSeconds: 60              # Wartezeit beim ersten Start einer Strecke (danach gemessen)
```

- Dashboard, Reiter Server: Karte „Strecken-Rotation“ (jetzt, als Nächstes, sofort wechseln). Admin im Chat: `/server_nexttrack [preset]`.
- Die Willkommensnachricht bekommt automatisch eine Zeile „Strecken-Rotation: jetzt …, danach …“.
- `WELCOME_MESSAGE` ist ein **Dateipfad** (in `cfg/` relativ zum Server-Ordner, z. B. `cfg/welcome.txt`; in einer Strecke oder Klasse
  relativ zu deren Ordner, z. B. `welcome.txt`). Die Kurzbeschreibung in Content Manager kommt aus `ServerDescription` in `extra_cfg.yml`.
- Strecken, die kein Kunos-Inhalt sind (Mods), müssen die Spieler installiert haben, oder man trägt Download-Links ein
  (`[DATA]` in `cfg/cm_content/content.json` bzw. über die Content-Manager-Server-Einstellungen).
- Die Zahl der gefahrenen Rennen bleibt bei einem normalen Neustart erhalten (`rotation.state`).
- Ohne `server-supervisor.sh` (z. B. beim Hoster, dessen Panel immer mit `cfg/` startet) wechselt der Server beim Start selbst
  auf die zuletzt gefahrene Strecke; „Server neu starten“ im Dashboard startet dann im selben Prozess neu.
- `current-preset` merkt sich die laufende Strecke mit Klasse (`trialmountain+gte`); `ServerToolsPlugin/scripts/server-supervisor.sh` startet nach einem Neustart dort weiter.

## Fahrzeugklassen
Mitgeliefert in `ServerToolsPlugin/presets/classes/` (werden von `setup-testserver.sh` und `update-server.sh` nach `presets/classes/` kopiert,
eigene Änderungen bleiben): **GT3** (12 Autos), **GTE/GT2** (C7.R, 911 RSR 2017, 458 GT2, M3 GT2),
**LMP1** (TS040, 919 Hybrid 2015/2016, R18 e-tron, mit Hybrid-Boost aus `ers.ini`), **JDM** (Supra MkIV, Skyline R34, RX-7 Spirit R, 370Z).

**Klasse wählen:** `Class: gte` in `rotation.yml`, im Chat `/server_class gte` (sofort), `/server_class gte next` (ab dem nächsten
Streckenwechsel), `/server_class` = Liste; oder im Dashboard unter Server → Fahrzeugklasse. „Sofort“ startet neu wie ein Streckenwechsel
(Countdown, Neu-Verbinden), der Rennzähler der Strecke läuft weiter.

**Eigene Klasse:** einen Ordner `presets/classes/<name>/` mit `entry_list.ini` (Spieler-Plätze oben, Bots mit `AI=fixed`) und
`server_cfg.ini` (`[SERVER] CARS=…`, `[PRESET] CLASS_TITLE=…`, optional `DESCRIPTION=…`) anlegen, am einfachsten als Kopie einer
vorhandenen. Optional eine `plugin_bot_driver_cfg.yml` mit Einstellungen nur für diese Klasse.
Jedes Modell braucht auf dem Server `content/cars/<modell>/data.acd` (am besten auch `ui/ui_car.json`).

