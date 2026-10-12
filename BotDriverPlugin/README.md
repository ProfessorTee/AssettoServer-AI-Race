# BotDriverPlugin (Physik-Fork) – Renn-Bots für AssettoServer

> **Branch `bots-physics`.** Diese Version fährt mit einem Fahrzeugmodell und dem nachgebauten Fahrer der Einzelspieler-KI von
> Assetto Corsa (Kunos `AIDriver`, aus `acs.exe` ausgelesen, siehe `UEBERGABE_AC_KI.md`). Das kinematische Plugin mit
> Persönlichkeiten, Klonen, Duellen, Gummiband, Safety Car und Flaggen liegt auf `main`.

KI-Gegner wie im Einzelspieler, aber auf einem AssettoServer: Die Bots fahren auf der Kunos-Ideallinie (`fast_lane.ai`).
Sie nehmen an Training, Qualifying und Rennen teil und stehen in der echten Startaufstellung. Ihre Rundenzeiten sind offiziell,
sie erscheinen im Leaderboard und in den Ergebnissen.

Das Plugin besteht aus zwei Teilen:

1. **Core-Patch für AssettoServer** (klein, als PR geeignet)
   - `IExternalAiController`: Ein Plugin steuert einen KI-Slot selbst (`EntryCar.ExternalAiController`), die Traffic-KI lässt ihn in Ruhe.
   - `SessionManager.OnAiLapCompleted(...)`: Runden von Bots zählen offiziell (Wertung, Leaderboard, Zielflagge, Ergebnisse).
   - Ein Rennen mit **einem** Spieler und Bots wird nicht mehr als „zu wenige Spieler“ übersprungen.
2. **Plugin `BotDriverPlugin`**
   - `Core/`: die Renn-KI, ohne Abhängigkeit von AssettoServer (offline testbar mit `BotDriverTool`).
     `Core/Physics/`: Fahrzeug-, Reifen- und Fahrwerksmodell. `Core/RaceWorld.Kunos.cs`: der Kunos-Fahrer.
   - Plugin-Hülle: Slots, Sessions, Startaufstellung, Timing, Chat-Befehle, Konfiguration.

Webseiten: [`WebPortalPlugin`](../WebPortalPlugin/README.md). Rotation, Klassen, Statistik, Wetter: [`ServerToolsPlugin`](../ServerToolsPlugin/README.md).

## Fahrzeugmodell (`Core/Physics`)

- **Daten wie Content Manager** direkt aus `data.acd`: Masse, Drehmoment, Turbos, Übersetzungen, Kupplung, Autoblip, Differenziale,
  Aufhängung (Doppelquerlenker/McPherson aus `suspensions.ini` gelöst: Sturz, Spur, Ackermann), Federn, Dämpfer, Stabis, Flügel aus `aero.ini`,
  ABS/TC aus `electronics.ini`. Ballast und Restriktor aus der Entry List zählen mit.
- **Reifen wie AC (Modell 10, „SCTM“):** Grip mit Lastabhängigkeit, Sturz, Schlupf, Relaxationslänge, Rollwiderstand.
  Der Grip ist pro Auto auf das kalibriert, womit die AC-KI plant (`Envelope.Calibrate`). Darüber gibt es 20 % Ultra-Grip als Reserve, wie in AC.
- **Reifentemperatur und -druck wie AC** (`TyreHeat`): je Reifen 12 × 3 Laufflächenzonen plus Kern, Wärme aus Schlupf und Rollen,
  Kühlung durch Fahrtwind, alle Werte aus `tyres.ini [THERMAL_…]`.
  - Druck = Kaltdruck + (Kerntemperatur − 26 °C) × 0,16 psi/°C.
  - Grip × Temperaturkurve (`PERFORMANCE_CURVE`) ÷ (1 + Abstand zum Idealdruck × `PRESSURE_D_GAIN`).
- **Fahrbahn unter jedem Rad:** Bodenwellen und Curbs (bis 25 cm) aus dem Strecken-Mesh, Grip der Oberfläche aus `surfaces.ini`.
  Neben der Strecke zählt auch die Höhe des Gras- und Kies-Meshes.
- Im Client stehen die Autos auf allen vier Rädern auf der echten Fahrbahn: Höhe, Nicken und Wanken kommen aus dem Mesh.

## Der Fahrer (Kunos-KI)

- **Stärke = KI-Level wie im Einzelspieler:** `AiStrength: 95` heißt `AI_LEVEL` 95. Unter Level 0,9 begrenzt die KI ihr Gas wie bei Kunos.
- Kurventempo nach der Kunos-Formel (Radius, Grip, Querneigung, `ai_hints.ini`), Bremsplanung 300 m voraus, Lenkung als PID wie `stepSteer`.
- Hilfen der AC-KI: Feder zur Linie, Bremshilfe vor dem Bremspunkt, perfektes ABS pro Rad, KI-Traktionskontrolle,
  Stabilitätshilfe bei mehr als 2–5° Querstellung.
- **Zweikampf wie `stepCarsAwareness2`:** nebeneinander Abstand halten, Überholen auf der Seite mit mehr Platz, sonst folgen.
  `AiAggression` wie AC `AI_AGGRESSION`.
- **Pace-Schwankung und Genom** (`PushVariation`): Das Level schwankt über Minuten um ±4 %, jeder Bot ist in manchen Abschnitten stärker.
- **Fehler** (`HumanErrors`): Kunos-Humanizer, alle 20–120 s bis zu 2 s Überbremsen ohne ABS-Hilfe.
- Gänge nach `ai.ini` (`UP`/`DOWN`), Motorbremse beim Runterschalten.

## Rennstart

- In der Startaufstellung gibt der Fahrer ab und zu Gas. Die letzten 5 s hält er im 1. Gang die Startdrehzahl.
- Reaktionszeit 50–400 ms wie AC.
- Mit `HumanErrors` und je nach Level:
  - Durchdrehende Räder: Chance 1,5 × (1 − Level), höchstens 60 %, für 0,4–2 s.
  - Fehlstart: Chance 0,25 × (1 − Level). Die Strafe richtet sich nach `START_RULE` in `server_cfg.ini`:
    1 = zurück in die Box, 2 = Durchfahrtsstrafe. Bei 0 sind die Autos blockiert, dann gibt es keinen Fehlstart.
- Heizdecken wie bei Spielern: Mit `TYRE_BLANKETS_ALLOWED=1` haben die Reifen beim Start und nach dem Boxenstopp
  Deckentemperatur (`BLANKETS_TEMP`), sonst Umgebungstemperatur.

## Reifendruck der Bots

- **Ziel:** Heiß sollen die Reifen den Idealdruck erreichen.
- **Fahrstil:** Aggressive Fahrer lassen hinten bis 1,5 psi mehr, damit das Auto williger dreht. Vorsichtige machen das vorne,
  damit es ruhiger liegt.
- **Können:** Schwächere Fahrer treffen den Druck ungenauer, um ±3 psi × (1 − Level). Bessere Fahrer rechnen Luft- und
  Streckentemperatur ein (ab Level 0,6, voll bei 1,0).
- **Lernen:** Beim Reifenwechsel und bei einer neuen Session korrigieren sie den Kaltdruck nach dem gemessenen Heißdruck des letzten Stints.

## Sprit, Reifen, Boxenstopps, Schaden

- Verbrauch aus `fuel_cons.ini` bzw. `car.ini` × `FUEL_RATE`. Der Sprit zählt zum Gewicht.
- Verschleiß über die Kunos-Verschleißkurven (`tyres.ini`) × `TYRE_WEAR_RATE`.
- Mischung wie AC: im Qualifying die griffigste erlaubte, sonst `COMPOUND_DEFAULT`. `LEGAL_TYRES` gilt, keine Regenreifen.
- Boxenstopps über `pit_lane.ai` mit Tempolimit an die eigene Box (`AC_PIT_x`): tanken, Reifen bei 95–97 % Grip wechseln wie die AC-KI,
  reparieren. Pflicht-Boxenstopp-Fenster `RACE_PIT_WINDOW_START/END` werden eingehalten.
- Schaden (`BotDamage`, nach `DAMAGE_MULTIPLIER`): Berührungen beschädigen Karosserie und Aufhängung. Die Schadenszonen gehen an
  alle Clients, das Auto verliert Grip und hat mehr Luftwiderstand. Repariert wird in der Box (Zeiten aus `car.ini`).
- Nässe kostet Grip (`RainGripLoss`), wie bei den Spielern auch Dynamic Track.

## Sonstiges

- Echte Startaufstellung (`AC_START_x` aus den kn5-Dateien oder `GridFile`), Startampel des normalen AC-Rennens.
- Boxenstart in Training und Qualifying (`SessionStart: Pits`), Spieler haben Vorrang (`QualifyingBotDelaySeconds`).
- Ende von Training/Qualifying: Eine laufende Runde wird geschätzt, danach fährt der Bot in die Box (`EstimateLapsAtSessionEnd`).
- Auslaufrunde nach der Zielflagge, danach in die Box.
- Scheinwerfer bei Dunkelheit, Fernlicht, solange `HighBeamRange` frei ist (`HighBeams`), Scheibenwischer, Bremslichter,
  Warnblinker bei Stillstand.
- Notfall: Ein Bot, der `GhostAfterSeconds` lang steht, wird kurz zum Geist und fährt aus dem Stau.
- Ein Spieler kann jedes Bot-Auto nehmen (`PlayersCanTakeBotSlots`), der Bot kommt zurück, wenn der Spieler geht.

## Einstellungen (`plugin_bot_driver_cfg.yml`)

| Content Manager | Einstellung |
|---|---|
| KI-Stärke | `AiStrength` (= KI-Level in %) |
| KI-Stärke: Variation | `AiStrengthSpread` (± %), `AiStrengthDistribution: Even` oder `Random` |
| KI-Aggressivität | `AiAggression` (0–100), `AiAggressionVariation` |
| Startposition | `PlayerGridPosition`: `Default`, `First`, `Last`, `Middle`, `Random` |
| Zufällige Startaufstellung | `RandomizeBotGrid` |
| Reihenfolge der Bots | `BotGridOrder`: `Qualifying`, `SlowestFirst`, `Random` |
| Fahrernamen / Nationen | `DRIVERNAME` in `entry_list.ini`, `Names`, `Drivers` (auch Stärke/Aggressivität pro Fahrer) |

Weitere Optionen: `BotSlots`, `MaxBots`, `NamePrefix`, `Practice`/`Qualifying` (`Drive` oder `Parked`), `SessionStart`,
`PracticeBotDelaySeconds`, `PitReleaseIntervalSeconds`, `UseTrackHints`, `PhysicsCorneringGrip` (Faktor auf den Ultra-Grip),
`PushVariation`, `HumanErrors`, `Fuel`, `TyreWear`, `TyreWearFactor`, `PitStops`, `PitSpeedKmh`, `PracticeFuelLaps`,
`QualifyingFuelLaps`, `BotContacts`, `BotDamage`, `BotDamageFactor`, `RainGripLoss`, `HighBeams`, `HighBeamRange`, `DaytimeLights`,
`CoolDownPace`, `GhostAfterSeconds`, `HeightOffset`, `ParkHeightAdjust`, `AssettoCorsaPath`, `GridFile`, `LogLaps`, `Debug`.

Einträge aus der kinematischen Version (`Personalities`, `RubberBanding`, `LineErrors` usw.) dürfen in der Datei stehen bleiben,
sie haben keine Wirkung mehr.

## Chat-Befehle

| Befehl | Wirkung |
|---|---|
| `/bots` | alle Bots mit Stärke, KI-Level, Runden, Bestzeit, Sprit, Reifen, Stopps, Kontakten, Schaden |
| `/bots_strength <%> [spread]` | Stärke neu verteilen (Admin) |
| `/bots_aggression <0-100>` | Aggressivität (Admin) |
| `/bots_grid Qualifying\|SlowestFirst\|Random` | Startaufstellung ab dem nächsten Rennen (Admin) |
| `/bots_count 8` · `off` · `on` | nur 8 Bots / keine / alle (Admin, Start-Wert `MaxBots`) |
| `/bots_set errors\|push\|contacts\|damage\|highbeams on\|off` | Funktion ein/aus (Admin) |
| `/bots_debug on [Bot]` · `off` | Debug-Log und Telemetrie-CSV mit Position, Richtung, Reifen (Druck, Temperaturen), Absicht (Admin) |

## Einrichtung

1. `extra_cfg.yml`: `EnableAi: false` (die Traffic-KI darf nicht mitlaufen), `EnablePlugins: [BotDriverPlugin]`.
2. `entry_list.ini`: Die Bot-Slots bekommen `AI=fixed` (oder `BotSlots` setzen). Spieler-Slots gehören nach oben.
3. `plugin_bot_driver_cfg.yml` anlegen (Beispiel in `examples/nordschleife-gt3/cfg`).
4. Daten: Der Server braucht `content/cars/<auto>/data.acd`, das Plugin liest daraus die Fahrphysik. Ideallinie, `ai_hints.ini`,
   die Strecken-Meshes (Fahrbahn, Curbs) und die Startplätze kommen aus dem Server-Ordner `content` oder aus `AssettoCorsaPath`.

## Bauen und testen

```
tools/build.sh                                     # Server und Plugins -> out-linux-x64/
dotnet run --project BotDriverPlugin/Tool -c Release -- selftest
dotnet run --project BotDriverPlugin/Tool -c Release -- sim --ac <AC-Ordner> --track <AC>/content/tracks/trialmountain --layout forward \
    --models ks_toyota_supra_mkiv,ks_nissan_skyline_r34 --bots 12 --laps 2 --strength 95 [--poses] [--trace <id>]
dotnet run --project BotDriverPlugin/Tool -c Release -- sim ... --hotlap --strength 100
dotnet run --project BotDriverPlugin/Tool -c Release -- car --ac <AC-Ordner> --model ks_toyota_supra_mkiv   # Modell, Skidpad, Grip-Kalibrierung
dotnet run --project BotDriverPlugin/Tool -c Release -- physstep --ac <AC-Ordner> --model <auto> --speed 45 --brake 1 --steer 0.4
dotnet run --project BotDriverPlugin/Tool -c Release -- strength --ac <AC-Ordner> --track-name <strecke> --layout <layout> --models <autos>
tools/bench.sh "was geändert"   ·   tools/passtest.sh
```

## Grenzen (Stand jetzt)

- Rennstart, Fehlstart, Heizdecken und Kerb-Physik sind nur gebaut und im Selbsttest geprüft, nicht im Spiel.
- Der Kunos-Fahrer überholt eher zögerlich (wie in AC).
- Beim harten Anbremsen mit Runterschalten bricht unser Modell hinten leichter aus als AC.
- Nicht nachgebaut: Druckeinfluss auf Federrate und Reifenflanke, Graining, Blasenbildung.
- Die AC-Clients zeigen für fremde Autos keine Reifentemperaturen. Die Werte stehen im Dashboard und in der Debug-Telemetrie.
