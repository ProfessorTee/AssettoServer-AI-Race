# RaceAiPlugin – Renn-KI für AssettoServer

KI-Gegner wie im Einzelspieler, aber auf einem AssettoServer: Bots fahren auf der Kunos-Ideallinie (`fast_lane.ai`),
nehmen an Training, Qualifying und Rennen teil, stehen in der echten Startaufstellung, bekommen offizielle Rundenzeiten
und erscheinen im Leaderboard und in den Ergebnissen. Erstes Ziel: **Nordschleife** (`ks_nordschleife`, Layout `nordschleife`) mit **GT3**.

Das Plugin besteht aus zwei Teilen:

1. **Core-Patch für AssettoServer** (klein, als PR geeignet)
   - `IExternalAiController`: Ein Plugin kann einen KI-Slot selbst steuern (`EntryCar.ExternalAiController`); die Traffic-KI lässt ihn in Ruhe.
   - `SessionManager.OnAiLapCompleted(...)`: Runden von Bots zählen offiziell (Wertung, Leaderboard, Zielflagge, Ergebnisse).
   - Ein Rennen mit **einem** Spieler und Bots wird nicht mehr als „zu wenige Spieler“ übersprungen.
2. **Plugin `RaceAiPlugin`**
   - `Core/`: die eigentliche Renn-KI, ohne Abhängigkeit von AssettoServer (offline testbar, siehe `RaceAiTool`).
   - Plugin-Hülle: Slots, Sessions, Startaufstellung, Timing, Chat-Befehle, Konfiguration.

## Was die Bots können

- Fahren auf der Kunos-Ideallinie mit Streckenbreite, Kurven- und Kuppenlimit (Flugplatz, Pflanzgarten) und Bremspunkten
- **Fahrzeugdaten wie Content Manager** direkt aus `data.acd`: Gewicht, Drehmomentkurve, Turbo, Übersetzungen, Reifenradius und -grip,
  Abtrieb und Luftwiderstand aus `aero.ini`, Schaltdrehzahlen aus `ai.ini`. Ballast und Restriktor aus der Entry List werden berücksichtigt.
- **Kunos-AI-Hints** der Strecke (`data/ai_hints.ini`: langsamere Abschnitte, Max-Speed)
- Windschatten, Folgen, Angriff (Pressure in Kurven und Bremszonen, Windschatten auf Geraden), Nebeneinanderfahren mit
  Innenbahn-Vorrecht, Verteidigen, Fahrfehler je nach Konstanz, Formschwankungen
- Kontakt mit Spielern: Der Bot gibt nach und wird vorsichtig; bei einem stehenden Hindernis wird nach einiger Zeit ausgewichen
- Echte **Startaufstellung** (`AC_START_x` aus den kn5-Dateien bzw. aus `GridFile`), Startampel über das normale AC-Rennen,
  Reaktionszeit am Start
- Offizielle Rundenzeiten in Training, Qualifying und Rennen; das Qualifying-Ergebnis bestimmt die Startaufstellung
- Nach der Zielflagge: Auslaufrunde, danach geht es in die Box (`AC_PIT_x`)
- Grip der Strecke (Dynamic Track, Regen) macht die Bots langsamer, genau wie die Spieler
- **Sprit und Gewicht:** Verbrauch aus `fuel_cons.ini` bzw. `car.ini` mal `FUEL_RATE`. Ein voller Tank macht das Auto schwerer und langsamer.
- **Reifen:** Verschleiß über die Kunos-Verschleißkurven der Standardmischung (`tyres.ini`), mal `TYRE_WEAR_RATE`.
  Neue Reifen sind in den ersten Kilometern kalt.
- **Boxenstopps:** Die Bots fahren über `pit_lane.ai` mit Tempolimit an ihre Box (`AC_PIT_x`), tanken und wechseln die Reifen
  (Standzeit aus `car.ini`) und fahren wieder raus. Die Strategie rechnet: Reicht der Sprit bis ins Ziel? Kosten die alten Reifen
  bis zum Ende mehr Zeit als ein Stopp? Die Pflicht-Boxenstopp-Fenster `RACE_PIT_WINDOW_START/END` werden eingehalten.
- Scheinwerfer bei Dunkelheit, Scheibenwischer bei Regen, Bremslichter, Warnblinker bei Stillstand
- Namen und Nationen im Client, in der Rangliste und in den Ergebnissen

## Einstellungen wie in Content Manager

| Content Manager | `plugin_race_ai_cfg.yml` |
|---|---|
| KI-Stärke | `AiStrength` in % der Bestzeit (100 % = Median-Bestzeit der Bot-Autos, steht im Log). 94 % auf der Nordschleife ≈ 7:18 |
| KI-Stärke: Variation | `AiStrengthSpread` (± %, gleichmäßig auf die Bots verteilt, `AiStrengthDistribution: Even/Random`) |
| – | `AiStrengthReference`: `Field` (gleiche % = gleiche Zeit in jedem Auto) oder `Car` (% der Bestzeit des eigenen Autos) |
| KI-Aggressivität | `AiAggression` (0–100), `AiAggressionVariation` |
| Startposition | `PlayerGridPosition`: `Default`, `First`, `Last`, `Middle`, `Random` |
| Zufällige Startaufstellung | `RandomizeBotGrid` |
| Fahrernamen / Nationen | `DRIVERNAME` in `entry_list.ini`, `Names`, `Drivers` (auch Stärke/Aggressivität pro Fahrer) |

Weitere Optionen: `Practice`/`Qualifying` (`Drive` oder `Parked`), `SlipstreamStrength`, `EdgeMargin`, `SideMargin`, `CoolDownPace`,
`DaytimeLights`, `AnnounceOvertakes` (Chat: „X overtook Y at Flugplatz“), `NamePrefix`, `LogLaps`.

Weitere Endurance-Optionen: `Fuel`, `TyreWear`, `TyreWearFactor`, `TyreChangeGrip`, `PitStops`, `PitSpeedKmh`, `PracticeFuelLaps`,
`QualifyingFuelLaps`, `AnnouncePitStops`.

Chat-Befehle: `/raceai` (Bots mit Stärke, Sprit, Reifen, Stopps), als Admin `/raceai_strength <%> [spread]` und `/raceai_aggression <0-100>`.

## Einrichtung

1. `extra_cfg.yml`: `EnableAi: false` (die Traffic-KI darf nicht mitlaufen), `EnablePlugins: [RaceAiPlugin]`.
2. `entry_list.ini`: Die Bot-Slots bekommen `AI=fixed` (oder `BotSlots` in der Plugin-Konfiguration setzen). Spieler können diese Slots nicht belegen.
   Die Nordschleife hat 24 Startplätze.
3. `plugin_race_ai_cfg.yml` anlegen (Beispiel in `example/nordschleife-gt3/cfg`).
4. Daten: Der Server braucht wie immer `content/cars/<auto>/data.acd` (Checksummen). Das Plugin liest daraus auch die Fahrphysik.
   Ideallinie, `ai_hints.ini` und `sections.ini` werden im Server-Ordner `content` gesucht und danach unter `AssettoCorsaPath`.
   `example/nordschleife-gt3/prepare-content.sh <AC-Ordner> <Server-Ordner>` kopiert alles Nötige.
5. Startaufstellung: Das Plugin liest `AC_START_x`/`AC_PIT_x` aus den kn5-Dateien (unter `AssettoCorsaPath`) oder aus `GridFile`
   (`RaceAiTool grid --out ...`, für die Nordschleife liegt die Datei bei).

Beispielkonfiguration Nordschleife + GT3: `example/nordschleife-gt3/cfg` (4 Spieler-Slots, 16 Bots, Training 15 min, Qualifying 10 min, Rennen 2 Runden).

## Bauen

AssettoServer braucht das **.NET 11 SDK**:

```
dotnet build AssettoServer.slnx -c Release
dotnet publish AssettoServer/AssettoServer.csproj -c Release -r linux-x64      # oder win-x64
dotnet publish RaceAiPlugin/RaceAiPlugin.csproj -c Release -r linux-x64        # -> out-linux-x64/plugins/RaceAiPlugin
```

## RaceAiTool (offline, ohne Server)

```
dotnet run --project RaceAiTool -- selftest
dotnet run --project RaceAiTool -- sim  --ac <AC-Ordner> --models ks_mercedes_amg_gt3,ks_ferrari_488_gt3 --bots 16 --laps 8 --strength 94 --spread 3
dotnet run --project RaceAiTool -- sim  --ac <AC-Ordner> --models ks_mercedes_amg_gt3 --hotlap --strength 100
dotnet run --project RaceAiTool -- grid --ac <AC-Ordner> --track-name ks_nordschleife --layout nordschleife --out grid.json
dotnet run --project RaceAiTool -- car  --ac <AC-Ordner> --model ks_mercedes_amg_gt3
dotnet run --project RaceAiTool -- strength --ac <AC-Ordner> --models ks_mercedes_amg_gt3,ks_ferrari_488_gt3
```

Referenzwerte Nordschleife: 100 % ≈ 6:52 (Median der GT3), 95 % ≈ 7:13, 90 % ≈ 7:38. Die Zielzeit gilt für frische Reifen und wenig Sprit.
Im Rennen mit vollem Tank sind die Bots ein paar Sekunden langsamer.
`RaceAiTool strength --ac <AC-Ordner> --models …` zeigt die Tabelle pro Auto.

## Grenzen (Stand jetzt)

- Die Bots sind kinematisch (keine echte Fahrphysik). Kontakte lösen sie auf, indem sie nachgeben, und nicht über Kollisionen.
- Kein fliegender Start, keine Schäden und Reparaturen. Die Reifen werden nicht warm gefahren (nur ein Kaltstart-Abschlag nach dem Wechsel).
- Ein Bot, dessen Slot ein Admin übernimmt, kommt erst wieder, wenn der Admin den Server verlässt. In ein laufendes Rennen steigt er nicht ein.
- Getestet mit einem simulierten Client und offline. Ein Test mit dem echten AC-Client und CSP steht noch aus.
