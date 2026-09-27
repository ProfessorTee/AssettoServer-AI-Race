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
- **Schwächere Bots fahren wie schwächere Menschen:** Sie bremsen früher und weicher und gehen später aufs Gas. Durch die Kurve
  selbst sind sie nur wenig langsamer. Auf der Geraden hat jedes Auto seine volle Leistung.
- **Menschliche Fehler** (`HumanErrors`, unter `HumanErrorsBelow` = 87 %, bei 75 % am meisten): zu spät gebremst (blockierende
  Vorderräder, das Auto schiebt nach außen, manchmal übers Gras), zu früh gebremst, zu viel Gas am Kurvenausgang (das Heck kommt,
  Gegenlenken, kurzes Pendeln). Ein Teil der Rundenzeit geht so durch Fehler verloren, der Rest durch das vorsichtigere Fahren.
  Die Kalibrierung rechnet beides ein.
- **Dreher** (`Spins`): Aus einem bösen Rutscher wird manchmal ein Dreher. Der Bot bleibt mit Warnblinker stehen, wartet eine Lücke ab,
  dreht um und fährt vorsichtig wieder los. Die anderen sehen die gelbe Flagge.
- **Gras** (`GrassMoments`): Ab und zu kommt ein Bot mit zwei Rädern aufs Gras und verliert dabei etwas Tempo.
- **Leichte Berührungen** (`BotContacts`): In engen Zweikämpfen verschätzt sich ein Bot manchmal und lehnt sich an den anderen.
  Es gibt einen Rempler, beide wackeln kurz, aber keine großen Unfälle.
- **Schaden** (`BotDamage`, nach `DAMAGE_MULTIPLIER`): Berührungen mit Bots und Spielern beschädigen Karosserie und Aufhängung.
  Die Schadenszonen gehen wie bei Spielern an alle Clients. Das Auto hat weniger Grip und mehr Luftwiderstand. Lohnt es sich,
  kommt der Bot zur Reparatur in die Box (Zeiten aus `car.ini`: `BODY_REPAIR_TIME_SEC`, `SUSP_REPAIR_TIME_SEC`).
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
- **Ungeduld:** Wer hinter einem langsameren Auto festhängt, wird mit der Zeit ungeduldig (`ImpatienceSeconds`). Er fährt dichter auf,
  greift früher an, sucht öfter eine Lücke und gibt die Lichthupe (nachts Fernlicht, tagsüber Scheinwerfer). Crashs vermeidet er trotzdem.
- **Blaue Flagge:** Ein überrundeter Bot fährt an den Rand, setzt den Blinker zu dieser Seite und nimmt etwas Gas weg. Der Führende
  fährt auf der anderen Seite vorbei.
- **Gelbe Flagge:** Steht ein Auto auf der Strecke, schalten die Bots in der Nähe den Warnblinker ein, fahren langsamer und überholen nicht.
- **Regen:**
  - Nasse Strecke und stehendes Wasser kosten Grip, je nach Reifen: Slicks verlieren viel, Regenreifen wenig. Dafür sind
    Regenreifen im Trockenen langsamer und verschleißen schnell.
  - Auf Slicks im Nassen fahren die Bots vorsichtiger, machen mehr Fehler und schwimmen auf stehendem Wasser auch mal auf
    (`RainCaution`).
  - Reifenwechsel (`WetTyres`): Bei Regen wechseln die Bots in der Box auf Regenreifen und beim Abtrocknen zurück auf Slicks,
    sofern das Auto eine Regenmischung in `tyres.ini` hat. Die Kunos-GT3 haben nur Slicks. Mit `VirtualWetTyres: true` dürfen die Bots
    trotzdem Regenreifen nehmen, die Spieler aber nicht.
  - Für die Spieler: Ohne die Regenphysik der CSP-Preview-Versionen ist Regen nur optisch. Mit `RainTrackGripReductionPercent`
    (0–0.5) in `extra_cfg.yml` senkt der Server bei Nässe den Grip für alle. Die Bots rechnen das mit ein.
- **Echtes Wetter** (`RealWeather`): Das aktuelle Wetter am Nürburgring kommt von Open-Meteo (kostenlos, ohne API-Key). Der Server
  schickt es als CSP-WeatherFX an die Clients, Sol und Pure zeigen es an. Mit `EnableRealTime: true` passt auch die Tageszeit.
- Scheinwerfer bei Dunkelheit, **Fernlicht**, solange etwa 250 m davor frei sind (`HighBeams`), und sofort Abblendlicht, sobald ein
  Spieler oder Bot davor ist. Dazu Scheibenwischer bei Regen, Bremslichter und Warnblinker bei Stillstand.
- Namen und Nationen im Client, in der Rangliste und in den Ergebnissen

## Einstellungen wie in Content Manager

| Content Manager | `plugin_race_ai_cfg.yml` |
|---|---|
| KI-Stärke | `AiStrength` in % der Bestzeit (100 % = Median-Bestzeit der Bot-Autos, steht im Log). 94 % auf der Nordschleife ≈ 7:18 |
| KI-Stärke: Variation | `AiStrengthSpread` (± %). `AiStrengthDistribution: Even` = in gleichen Schritten verteilt (90 ± 10 bei 16 Bots: 80,0 / 81,3 / … / 100), `Random` = zufällig im Bereich |
| – | `AiStrengthReference`: `Field` (gleiche % = gleiche Zeit in jedem Auto) oder `Car` (% der Bestzeit des eigenen Autos) |
| KI-Aggressivität | `AiAggression` (0–100), `AiAggressionVariation` |
| Startposition | `PlayerGridPosition`: `Default`, `First`, `Last`, `Middle`, `Random` |
| Zufällige Startaufstellung | `RandomizeBotGrid` |
| Fahrernamen / Nationen | `DRIVERNAME` in `entry_list.ini`, `Names`, `Drivers` (auch Stärke/Aggressivität pro Fahrer) |

Faktoren wie `SlipstreamStrength`, `TyreWearFactor` und `RainGripLoss`: 1.0 = 100 % (normal), 0.5 = halb, 2.0 = doppelt.

Weitere Optionen: `Practice`/`Qualifying` (`Drive` oder `Parked`), `SlipstreamStrength`, `EdgeMargin`, `SideMargin`, `CoolDownPace`,
`DaytimeLights`, `AnnounceOvertakes` (Chat: „X overtook Y at Flugplatz“), `NamePrefix` (Standard `AI-`), `LogLaps`.

Renn-Verhalten: `ImpatienceSeconds`, `FlashLights`, `FlashLightsDaytime`, `BlueFlags`, `YellowFlags`, `RainGripLoss`,
`RealWeather`, `RealWeatherUpdateMinutes`, `RealWeatherTransitionSeconds`.

Menschliches Verhalten: `HumanErrors`, `HumanErrorsBelow`, `HumanErrorsFull`, `Spins`, `GrassMoments`, `BotContacts`, `BotDamage`,
`BotDamageFactor`, `WetTyres`, `VirtualWetTyres`, `RainCaution`, `HighBeams`. Jede dieser Funktionen lässt sich mit `false` abschalten, während der Sitzung auch per Chat (siehe unten).

Weitere Endurance-Optionen: `Fuel`, `TyreWear`, `TyreWearFactor`, `TyreChangeGrip`, `PitStops`, `PitSpeedKmh`, `PracticeFuelLaps`,
`QualifyingFuelLaps`, `AnnouncePitStops`.

Chat-Befehle:
- `/raceai`: Bots mit Stärke, Sprit, Reifen, Stopps, Fehlern und Schaden
- Als Admin (`/admin <Passwort>`):
  - `/raceai_strength <%> [spread]` und `/raceai_aggression <0-100>`
  - `/raceai_set <Funktion> on|off` schaltet bis zum Neustart ein und aus: `errors`, `spins`, `grass`, `contacts`, `damage`,
    `blueflags`, `yellowflags`, `flash`, `highbeams`, `wettyres`, `virtualwets`, `raincaution`, `realweather`
  - `/raceai_lighttest`: Licht-Test. Alle Bots zeigen nacheinander je etwa 6 s linken Blinker, rechten Blinker, Warnblinker,
    Lichthupe, Bremslicht und Fernlicht, mit eingeschaltetem Licht. Für Nacht vorher `/settime 22:00`.
  - Regen zum Testen (Befehle von AssettoServer): `/setcspweather HeavyRain 30`, direkt nass mit `/setrain 0.8 0.8 0.3`
    (Intensität, Nässe, Wasser). Vorher `/raceai_set realweather off`, sonst holt sich der Server wieder das echte Wetter.

## Einrichtung

1. `extra_cfg.yml`: `EnableAi: false` (die Traffic-KI darf nicht mitlaufen), `EnablePlugins: [RaceAiPlugin]`.
2. `entry_list.ini`: Die Bot-Slots bekommen `AI=fixed` (oder `BotSlots` in der Plugin-Konfiguration setzen). Mit `PlayersCanTakeBotSlots: true`
   (Standard) kann ein Spieler trotzdem jedes Auto wählen: Zuerst bekommt er einen freien Spieler-Slot. Sind die alle belegt, macht ein Bot
   mit diesem Auto Platz und kommt zurück, sobald der Spieler den Server verlässt. Spieler-Slots gehören in der Entry List nach oben.
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

Referenzwerte Nordschleife: 100 % ≈ 6:52 (Median der GT3), 95 % ≈ 7:13, 90 % ≈ 7:37, 85 % ≈ 8:05, 80 % ≈ 8:40.
Unter etwa 80 % fahren die Bots sehr zaghaft durch die Kurven. Die Zielzeit gilt für frische Reifen und wenig Sprit.
Im Rennen mit vollem Tank sind die Bots ein paar Sekunden langsamer.
`RaceAiTool strength --ac <AC-Ordner> --models …` zeigt die Tabelle pro Auto.

## Grenzen (Stand jetzt)

- Die Bots sind kinematisch (keine echte Fahrphysik). Rutscher, Dreher und Rempler sind nachgebildet, nicht physikalisch berechnet.
- Beim Start misst das Plugin pro Auto einige simulierte Runden, auch mit Fehlern. Das dauert etwa 3 s pro Automodell.
- Kein fliegender Start, keine Schäden und Reparaturen. Die Reifen werden nicht warm gefahren (nur ein Kaltstart-Abschlag nach dem Wechsel).
- Mit dem echten AC-Client getestet: Fahren und Aussehen. Blinker, Lichthupe und echtes Wetter sind noch nicht mit dem echten Client geprüft.
