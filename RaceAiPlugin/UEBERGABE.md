# Race-AI für AssettoServer: Übergabe (Stand 28.09.2026, 2. Sitzung, Teil 9)

## Status
- Der Core-Patch und das Plugin **kompilieren** gegen AssettoServer master (Basis `e92d253`, entspricht Release `v0.0.55-pre42`).
- **End-to-End-Test** mit dem echten Server (linux-x64) und einem simulierten AC-Client (Handshake, Checksummen, UDP, Ping,
  Positionsupdates, Runden, Kollision) auf der Nordschleife mit 16 GT3-Bots:
  Qualifying → Startaufstellung (Spieler in der Mitte) → Start → 1 Runde → Zielflagge → Ergebnis → nächste Session.
  Siegerzeit 6:59, Feld 6:59–7:08, keine ungültigen Posen, Bot-Namen beim Client, Überholungsansagen mit Streckenabschnitt.
- Offline: `RaceAiTool selftest` grün. `RaceAiTool sim` auf der echten Linie: Hotlap bei Level 100 6:52.8, Rennen ohne Überlappungen.
- Mit dem echten AC-Client (CSP, Proton) vom Nutzer getestet: Aussehen und Fahren passen.

## Neu in Teil 2 (nach dem ersten Test mit dem echten Client)
- Rückmeldung vom Nutzer: Die Bots sehen gut aus und fahren sauber auf der Straße. Sie waren aber zu schnell (Nutzer 7:17, Bots 6:59–7:08).
- **Stärke in %** (`AiStrength`, `AiStrengthSpread` ±, gleichmäßig verteilt): Pro Auto wird beim Start mit simulierten Runden die Bestzeit
  gemessen (`StrengthCalibration`), danach wird die Pace auf die Zielzeit interpoliert. `AiStrengthReference: Field` = Median der
  Bot-Autos (gleiche % = gleiche Zeit). Nordschleife: 100 % ≈ 6:52, 94 % ≈ 7:18.
- **Endurance**: Sprit (fuel_cons.ini, FUEL_RATE, Gewicht), Reifenverschleiß (Kunos-Wear-LUT der Standardmischung, TYRE_WEAR_RATE,
  `TyreWearFactor` 0.15 virtuelle km pro km), kalte Reifen nach dem Wechsel, **Boxenstopps** über `pit_lane.ai` (`PitLane`,
  `RaceWorld.Pit.cs`), Strategie (Sprit bis ins Ziel? Reifenverlust gegen Stoppkosten, Pflicht-Fenster).
- „Echte Physik“ (Fahrdynamik auf dem Server) wurde bewusst nicht gebaut: Im AC-Multiplayer sind auch menschliche Gegner beim Client
  nur Positionsdaten. Stattdessen gibt es die physikalischen Effekte oben.

## Neu in Teil 3
- Faktoren einheitlich: `SlipstreamStrength` und `TyreWearFactor` 1.0 = 100 % (intern 0.35 bzw. 0.15). `NamePrefix` hat den Standard `AI-`.
- Schwäche in den Kurven: `RaceWorld.Integrate` skaliert die Gas-Pace mit der Querbeschleunigung (`cornerLoad`). Auf der Geraden gibt es volles Gas.
  Die Kalibrierung misst das automatisch mit.
- Ungeduld (`Impatience`, `ImpatienceSeconds`) mit Lichthupe (`BotPose.Flash` → HighBeam bzw. LightsOn), gelbe Flagge
  (`FindIncidents`: Auto < 8 m/s auf der Strecke → Warnblinker, langsamer, kein Überholen), blaue Flagge (Überrundeter fährt
  an den Rand, Blinker `BotPose.Indicator` → IndicateLeft/Right). Selftests: `FlagTest`.
- Regen-Grip für die Bots (`RainGripLoss`, nur wenn `RainTrackGripReductionPercent` = 0).
- `RealWeatherService`: Open-Meteo (ohne Key), WMO-Code → WeatherFxType, alle `RealWeatherUpdateMinutes`.
  Offen: am Rechner des Nutzers mit CSP testen, ob Sol/Pure die Übergänge sauber zeigen.

## Neu in Teil 4
- `PlayersCanTakeBotSlots` (Standard true): `RaceAiSlotFilter` öffnet die Bot-Slots für Spieler. AssettoServer probiert die Slots eines Modells
  in der Reihenfolge der Entry List, freie Spieler-Slots (oben) also zuerst. Beim Connect räumt der Bot den Slot (Chat: „X made room for Y“),
  beim Disconnect kommt er zurück. E2E getestet (Mercedes → Spieler-Slot 0, Audi → Bot-Slot 11, Bot kommt zurück).

## Neu in Teil 5 (nach Testfahrten mit dem echten Client)
- Bug beim Nutzer: Keine Bot-Zeiten und ein verschwundener Führender. Ursache: Die Plugin-DLL wurde bei laufendem Server überschrieben,
  danach kam in jeder Bot-Runde eine `BadImageFormatException`. Seitdem wird immer atomar installiert (`.new` + `mv`).
  E2E erneut geprüft: Bot-Runden stehen mit Zeit, Rundenzahl und Zielflagge im Leaderboard-Paket (0x49).
- Pace aufgeteilt (`DriverProfile.CornerSkill/BrakeSkill/ThrottleSkill`): Schwächere Bots bremsen früher und weicher und sind später
  am Gas. In der Kurve sind sie nur halb so viel langsamer wie vorher.
- `RaceWorld.Human.cs`: menschliche Fehler (`DriverProfile.Errors`, aus der Stärke über `ErrorsFor`)
  - `LateBrake`: plant mit zu viel Bremsleistung, blockierende Vorderräder über `FrontTyreFactor`, schiebt in der Kurve nach außen
    (Überschuss der Querbeschleunigung), ggf. aufs Gras
  - Zu früh gebremst: `MistakeUntil`
  - `Slide`: Heck kommt am Ausgang (Yaw, Gegenlenken, Hinterräder drehen durch)
  - `Spin`: Stehen mit Warnblinker, wartet auf eine Lücke, dreht um, fährt wieder los
  - `Grass`: zwei Räder neben der Strecke (`EdgeAllowance`)
- Kontakte zwischen Bots (`BotContact`, nur bei echter Annäherungsgeschwindigkeit). Verschätzen in Zweikämpfen über
  `MarginOverrideUntil`/`MisjudgeTowards`.
- Schaden: AC-Schadenszonen (≈ Aufprall-km/h, `damage.ini` animiert zwischen MIN_SPEED und FULL_SPEED) und Aufhängung.
  Grip- und Luftwiderstandsverlust, Reparatur beim Stopp mit `BODY_REPAIR_TIME_SEC`/`SUSP_REPAIR_TIME_SEC`, Strategie-Grund
  `damage`. `DamageUpdate`-Pakete an die Clients.
- Kalibrierung misst den Zeitverlust durch Fehler (Fehlerlevel 0,5 und 1, je 5 Runden). `PaceFor(strength, ref, errors)` zieht ihn ab.
- Admin: `/raceai_set <feature> on|off`, `/raceai_lighttest` (Blinker links/rechts, Warnblinker, Lichthupe, Bremslicht).
- Offen: Mit dem echten Client prüfen, wie die Schadenszonen aussehen (Skalierung geschätzt), ob die blockierenden bzw.
  durchdrehenden Räder bei Remote-Autos sichtbar sind und wie Rutscher und Dreher bei 18 Hz wirken.

## Neu in Teil 6
- Regen pro Bot und Reifen (`RaceWorld.RainGrip`): Slicks und Regenreifen, mit `ServerRainReduction` (extra_cfg `RainTrackGripReductionPercent`),
  sonst mit eigener Nässe-Kurve. `WantsWets` mit Hysterese, Strategie-Grund `wets`/`slicks`, Start auf passenden Reifen.
  `CarSpec.WetCompound` aus `tyres.ini` (Name wet/rain/inter). `VirtualWetTyres` für Autos ohne Regenmischung.
- `RainCaution`: Skill-Abzug und mehr Fehler im Nassen (auf Slicks stark), Aufschwimmen auf stehendem Wasser (`Aquaplaning`).
- Fernlicht: `UpdateClearAhead` (`HighBeamRange` frei, Standard 85 m, 1,5 s Verzögerung) → `BotPose.HighBeam` → `BotSlot` nimmt `HighBeamsOff` weg.
  Die Lichthupe schaltet um. Der Licht-Test hat Phase 6 „high beams“.
- `RealWeatherService` lässt sich zur Laufzeit ein- und ausschalten (`/raceai_set realweather off`).

## Neu in Teil 7
- Pedale statt Bang-Bang (`RaceWorld.Integrate`): Bremsdruck = Vorsteuerung (wie schnell die Zielgeschwindigkeit fällt) + Regelung,
  Aufbau 0,12 s, Lösen 0,4 s. Kleine Korrekturen nur per Gaswegnehmen (Motorbremse + Luftwiderstand). Teilgas zum Halten.
  `RaceBot.Throttle/Brake` 0..1 für Pose und Bremslicht. Planung: kleine Tempoverluste mit sanfterer, früherer Bremsung (`LineSpeedLimit`).
- Geparkte Bots auf Boxengassen-Höhe (`OnGround`, `ParkHeightAdjust`). AC_PIT-Dummies liegen auf der Nordschleife 1,3 m höher.
- `HighBeamRange` einstellbar (Standard 85 m). Online ist CSP-Regen nur optisch, deshalb gilt `RainTrackGripReductionPercent: 0.15`.
  Virtuelle Regenreifen sind standardmäßig aus, weil Spieler online keine CSP-Regenreifen wählen können.

## Neu in Teil 8
- Regenreifen komplett entfernt (CSP bietet sie online nicht an). Behalten: nasser Grip, `RainCaution`, Aufschwimmen.
  Neu: `PuddleRisk` (Senken, Ideallinie) und `RainLineOffset`, die Regenlinie weg vom Wasser.
- Sprit-Gewicht: `CarSpec.BrakeAt`/`CornerLimit` mit `massRatio` (Aero-Hilfe pro kg sinkt). Dazu Lastempfindlichkeit über
  `massGrip` und Beschleunigung / `MassRatio`. `RaceAiTool fuel` zeigt Bremsweg, 0–200 km/h und Rundenzeit bei 20 l, halbem und
  vollem Tank.
- `Personality` (Core) und `PersonalityConfiguration` (YAML, `Personalities`, `UsePersonalities`, `Drivers[].Personality`):
  `LateBraking` (Planung ×, Angriffe), `InsideLine` (≥ 0,5 nie außen), `TyreWear`/`FuelUse`/`Smoothness` (Verbrauch,
  sanftere Planung), `Composure` (Druck), `Weaving`, `Mistakes`.
- Druck: `PressureTime`/`Pressure` (Auto < 20 m hinter mir in der Spur) → `ErrorLevel` +0,15×. Pendeln: `DraftTime`, `Weaving`, `WeaveCenter`.
- `RaceWorld.Incidents.cs`: `YellowFlag`-Event (Dreher, Unfall > 30 km/h, > 3 s Stillstand, auch Spieler), Chat mit Sektor
  (`TrackInfo.SectorLines` aus AC_TIME_n, auch in der Grid-JSON) und Streckenabschnitt, höchstens 1× pro Sektor und 15 s.
  `ChatLanguage` de/en für alle Chat-Meldungen.
- Festgefahren: `UpdateStuck` → `Unstuck` (vorderes Auto zuerst, `Considers` ignoriert Autos dahinter, `UnstuckAround`
  umfährt stehende Autos). Notfall-Ghost nach `GhostAfter`: Bot ignoriert alles, `EntryCar.SetCollisions(false)` für die Clients.
  Der alte „Spieler ignorieren nach 12 s“ gilt nur noch während des Ghostings.

## Neu in Teil 9: Desktop-GUI
- `RaceAiDashboardController` (ASP.NET im Server, Plugin-Assembly als ApplicationPart):
  - `GET /raceai`: die Seite `Dashboard/index.html`, als Ressource eingebettet
  - `GET /raceai/api/state`, `/track`, `/log`, `/ping`
  - `POST /raceai/api/bot/{id}`, `/ai`, `/server` (next, restart, time, cspweather, rain, grip, chat, kick, stop, weathertypes)
  - Zugriff nur von Loopback, außer bei `DashboardRemoteAccess` (dann Header `X-Admin-Password` = ADMIN_PASSWORD).
- `RaceAiService.Dashboard.cs`: Zustand (Autos inkl. Bot-Details, Session, Wetter, KI-Schalter), Streckenumriss (Linie, Ränder,
  Box, Start, Sektoren, Abschnitte), `UpdateBot`, globale Stärke und Aggressivität.
- `race-ai/raceai-desktop.sh` (Start, Warten auf `/api/ping`, Absturzmeldung per zenity/kdialog, Chromium `--app`,
  Frage beim Schließen), `install-desktop-entry.sh`, `raceai-icon.svg`. Build: `csc -resource:` bzw. `EmbeddedResource` im csproj.

## Neu in Teil 10: Reifen, Persönlichkeiten, Join-Seite, Neustart, Fahrer-Klone
- `Core/RaceWorld.Tyres.cs`: Kerntemperatur vorne/hinten (Heizen aus Last × Tempo, Rutschen, Blockieren; Kühlen zur Straße, Regen),
  Grip `TyreTempGrip` (Fenster um 85 °C), Verschleiß-Faktor, Aufwärm-Pendeln auf der Out-Lap.
- `Personality`: `BrakeBehavior` (-1..1, ersetzt `LateBraking`), `Patience`, `LineErrors`, `TyreChangeAt`; `Smoothness` wirkt auf die Pedale.
- Lichthupe nur bei deutlichem Vorteil (`PressureEma > 1.6`), nicht in den ersten `FlashStartDelaySeconds`, abhängig von `Patience`.
- Startaufstellung: Autos ohne Quali-Zeit hinten (`ArrangePlayers`), `BotGridOrder`.
- `JoinInfo`/`JoinPageHtml`: `GET /raceai/join` (öffentlich) und `/raceai/api/join`: CM-Link `acstuff.club/s/q:race/online/join`, IPs.
- `race-ai/server-supervisor.sh`: startet den Server nach `restart.request` neu (Aktion `restartserver`, `text: update` = git pull + update-server.sh);
  Umgebungsvariable `RACEAI_SUPERVISED=1`.
- **DriverRecorderPlugin** (eigenes Projekt): CSP-Online-Skript `lua/driverrecorder.lua` (per `CSPServerScriptProvider`), OnlineEvents
  `DR_samples` (5 Messungen je Paket, 20 Hz), `DR_status` (1 Hz), `DR_control` (Server → Client). Opt-in `/rec on|off|delete|info`,
  `recordings/optin.json`, Runden als gzip-CSV je SteamID/Strecke/Auto; Schnitt an der Ziellinie (Spline-Sprung), Abgleich mit dem
  offiziellen `LapCompleted` (Zeit ± 1,5 s), sonst ungültig.
- `Core/CloneProfile.cs`: Profil alle 4 m (Tempo, Linienversatz + Streuung, Gas, Bremse) aus sauberen Runden ≤ 104 % der besten.
  `RaceBot.Clone`: `LineSpeedLimit` nimmt das Tempo des Spielers (skaliert mit √Grip, begrenzt durch die Autophysik), `Think` seine Linie
  (+ `CloneZ` × Streuung je Kurve), keine generierten Linienfehler.
- `CloneLibrary` (Plugin): liest die Aufzeichnungen, baut Profile bei Bedarf, Cache nach Dateiliste. `Drivers[].Clone`.
- Übernahme (`TryTakeOver`/`EndTakeover` in `RaceAiService`): beim Verbindungsabbruch im Rennen neuer `BotSlot` mit `TakeoverGuid`
  an der letzten Position, gleiche Runde; `RaceAiSlotFilter` hält das Auto für den Spieler frei.
- Fahrerwechsel (`RaceAiService.DriverSwap.cs`, `Dashboard/driverswap.lua`, `RaiSwapPacket`): Phasen `SwapPhase` Away/Watching/
  PitRequested/Handover; Zuschauen aus einem freien Spieler-Platz (beliebiges Modell, Umsetzen per `ac.reconnectTo`), Klon mit eigenem
  `CarStatus` (der Spieler kann kurz in seinem Auto „durchreisen“), `/bot`, `/play` (auch `!bot`/`!play`), Wechsel in der Box
  (`PitServiceUntil` gehalten, 90 s Zeit). Core: `IExternalAiController.StandsInFor`.

## Neu in Teil 11: Strecken-Rotation
- `TrackRotation.cs` (`BackgroundService`): liest `rotation.yml`, zählt beendete Rennen (`SessionChanged`), wechselt an Session-Grenzen
  per `AssettoServer.Program.RestartServer(preset, portOverrides)` (gleiche Ports). Vorher Chat-Hinweis und `SendTrackChange`
  (`RaiSwapPacket` Phase 6: Banner, danach `ac.reconnectTo` in das eigene Auto, Wartezeit = gemessene Startzeit + 8 s).
  `rotation.state` (Startzeiten je Preset), `current-preset` (für `server-supervisor.sh --preset`).
- Willkommensnachricht: `CSPServerExtraOptions.WelcomeMessageSending` hängt die Rotations-Zeile an.
- Dashboard `GET /raceai/api/rotation`, Aktion `rotate` (Ziel in `text`), Log liest alle `*.txt` (Presets haben eigene Log-Präfixe).
- `race-ai/add-track-preset.sh`: Preset aus `cfg/`, Streckendaten (ai, data, models_<layout>.ini), Eintrag in `rotation.yml`.

## Neu in Teil 12: Hoster (BisectHosting), Chat, Rücksicht auf Spieler
- Core: `ChatSplitter` teilt Chat-Nachrichten > 250 Zeichen (AC schickt die Länge in einem Byte, sonst Zeichensalat „von“ einem anderen Auto).
- Core: Steam wird beim Stoppen heruntergefahren (Neustart im selben Prozess), Presets ohne `ADMIN_PASSWORD` nehmen das aus `cfg/`,
  `ListedNameProvider` (Name in der Server-Liste), Session-Längen für die Server-Liste.
- `PlayerSideMargin` (1,0 m) und `PlayerOverlap` (3 m): Spieler gelten länger als „nebeneinander“, Bots lassen mehr Platz.
- DriverRecorder: Pakete ohne Skalar neben Arrays (`count` entfernt, `fuel` als float[4]), damit die Feldreihenfolge in CSP und
  AssettoServer eindeutig gleich ist; `/rec info` zeigt empfangene Messungen und Ziellinien-Überquerungen.

## Neu in Teil 13: Statistik, Duell, Wiedereinstieg, Einstellungen speichern
- `PlayerStats`: Bestzeiten (Woche/Allzeit), Safety Rating, Profile; `/top`, `/profile`, Seite `/raceai/stats`, API `/raceai/api/stats`.
- `/raceai_duel`: ein Bot wird zum Klon eines aufgezeichneten Spielers, Rundenvergleich im Chat.
- Core: `EmptyRaceGraceMilliseconds` (Rennen endet nicht sofort ohne Spieler) und `MayJoinClosedSession` (Beitritt ins laufende
  Rennen für Wiedereinsteiger); Plugin: `RejoinSeconds`.
- `ConfigWriter`: Dashboard-Einstellungen und Admin-Befehle werden in `plugin_race_ai_cfg.yml` geschrieben.
- Reifen: neue Temperaturkurve (kalt/heiß kostet mehr Grip), wenig Grip macht mehr Fehler.
- Dashboard: kein „Server beenden“ mehr; Neustart-Knöpfe nur mit `start-server.sh`.

## Neu in Teil 14: Kollisionen / „Reinglitchen“
- Core: Uhrabgleich pro Client gefiltert (`EntryCar.UpdateClock`). Vorher wurde der Zeit-Offset jede Sekunde aus einer einzelnen
  Ping-Messung gesetzt; ein Ausreißer (z. B. 100 ms) verschob für eine Sekunde alle anderen Autos auf dem Bildschirm des Spielers
  um Tempo x Ausreißer (bei 200 km/h 5 m) und die Bots sahen den Spieler ebenso versetzt. Messung mit Testclient (30 ms + 0–20 ms
  Jitter, Last): Streuung des Offsets 39 ms -> 1 ms, größter Sprung 125 ms -> 1 ms.
- Bots sehen Spieler zu jedem Rechenschritt: letzter Stand ab Zeitstempel mit Tempo und Bremsen/Beschleunigen hochgerechnet.
- Hinter Spielern etwas mehr Abstand, Bremsen des Spielers wird vorweggenommen.
- Berührungen ohne Sprünge: seitlich wegfedern statt Versatz, von hinten getroffen -> wird angeschoben statt abgebremst,
  nach dem Auseinanderschieben kein weiteres Hineinrutschen (das Zittern ineinander).
- RaceAiTool `sim --player-pace 0.97 --player-aggression 0.9 --latency 0.04`: Netzmodell, misst Überlappungen aus Sicht des Spielers.

## Neu in Teil 15: Server-Feinschliff (Rechenleistung ist da)
- Messung: ein Server-Tick braucht 0,6 ms (p99 1,9 ms) von 33 ms, kaum GC; Ressourcen sind kein Engpass.
- Simulationsschritt 25 ms -> 10 ms (`RaceWorld.DefaultStep`), Kalibrierung mit demselben Schritt. Im Simulator: Überlappungen
  zwischen Bots 22 -> 2 Frames, neben der Strecke 1371 -> 827 Frames pro Runde. Kalibrierung dauert länger, läuft jetzt parallel
  und wird gecacht (erster Start nach einem Update: bis ~1 min auf der Nordschleife).
- Core: Autos außerhalb der Netzwerk-Blase (500 m) mit 10 statt 4 Updates/s.
- Dashboard: Ping und Ping-Schwankung pro Spieler (`EntryCar.PingJitter`).

## Neu in Teil 16: Speicher wächst über Stunden
- Ursache: der Server erzeugt nur ~1 MB/min Müll, die erste GC-Generation ist auf großen Host-CPUs aber so groß (aus dem CPU-Cache
  berechnet), dass stundenlang nicht aufgeräumt wird (gemeldet: 300 -> 700 MB in 8 h, lebende Daten ~12 MB).
- Core: alle 5 min eine Hintergrund-GC (`ASSETTOSERVER_GC_INTERVAL`, Sekunden, 0 = aus). Test 25 min: verwalteter Speicher
  konstant 12 MB, Arbeitsspeicher pendelt 190-210 MB statt zu steigen.

## Neu in Teil 17: Ruckeln der Bots am Streckenrand, robuster Uhrabgleich
- Messung (`RaceAiTool sim --jumps`): Positionssprünge pro 10-ms-Schritt, die nicht zur gesendeten Geschwindigkeit passen.
  97 % kamen vom Streckenrand: die Breite wurde punktweise (ohne Interpolation) begrenzt, ein Bot am Rand wurde jeden Schritt
  ein Stück zurückgesetzt und rutschte wieder hinaus. Jetzt interpoliert (`RoomPlusAt/RoomMinusAt`), seitliche Geschwindigkeit
  zum Rand wird beim Anschlag gestoppt. Nordschleife, 16 Bots, 1 Runde: 1183 -> 45 Sprünge, max 0,36 -> 0,06 m;
  Überlappungs-Korrekturen 136 -> 19.
- Core: Uhrabgleich übernimmt große Sprünge (> 250 ms) erst nach 3 Messungen in Folge (vorher reichte ein Lag-Spike).
  Testclient unter Last: Streuung 36 -> 1,6 ms, größter Sprung 143 -> 3,4 ms, größter Positionssprung eines Bots 10,7 -> 0,8 m.

## Neu in Teil 18: Schneller Start, Kalibrierung im Hintergrund, Dashboard
- Kalibrierung (`RaceAiService.Calibration.cs`): beim Start pro Auto die finale aus dem Cache, sonst eine vorläufige (älterer Build
  aus dem Cache oder grob gemessen: 50-ms-Schritte, 2 statt 5 Fehlerrunden). Der Server startet sofort; ein Hintergrund-Thread misst
  die finalen und tauscht sie ein (Stärken werden neu angewendet), danach die Autos der anderen Rotationsstrecken (Preset-Ordner,
  server_cfg/entry_list mit Rückfall auf cfg/). Cache-Dateien: `<model>-<basis>-<build>.json`; alte Builds werden ersetzt.
  Messung (2 Kerne, Nordschleife, 8 Autos, leerer Cache): Start 80 s -> 16 s; mit Cache 3 s; Trial Mountain danach sofort.
- `StrengthCalibration.Measure`: alle 18 Kalibrierrunden sind unabhängig und laufen parallel auf allen Kernen.
- Multithreading im Takt: bewusst nicht. Ein Takt braucht 0,5 ms (Qualifying) bis 3 ms (Rennen, 16 Bots) von 33 ms; die Bots teilen
  sich einen Zufallsgenerator und lesen sich gegenseitig, Aufteilen brächte 1-2 ms bei echtem Risiko für schwer findbare Fehler.
- Core: Antwortkompression (Brotli/Gzip, nur wenn der Client sie anfordert). Dashboard-Status 9 -> 1,6 KB, Strecke 119 -> 44 KB.
- Dashboard: Tabelle wird zeilenweise aktualisiert (kein Neuaufbau 4x pro Sekunde), Karte rechnet Autos mit ihrer Geschwindigkeit
  weiter (statt eine Abfrage hinterher zu sein), Log nur noch neue Zeilen (statt ~50 KB alle 2 s), Server-Zustand im Kopf
  (Takt, CPU, Kerne, RAM), Kalibrierungs-Hinweis, Tag „Duell“, Handy-Ansicht aufgeräumt.

## Neu in Teil 19: Klon fährt die eigene Linie des Spielers
- Vorher: Klon zielte auf einen Punkt 0,55 s voraus auf der Spielerlinie (Versatz zur KI-Linie), Regler mit Verstärkung 2,5,
  Ziel auf die KI-Breite beschnitten, Zufallsversatz sprang pro Kurve. Ergebnis: im Schnitt 0,75 m neben der Spielerlinie,
  bis 4 m, Zucken.
- Jetzt (`CloneLineOffset`, `OnCloneLine`): Spielerlinie als eigener Pfad, beim Bauen bis 0,6 m an die Breitendaten heran
  begrenzt (Randsteine) und doppelt geglättet; gleich lange Abschnitte rund um die Runde (vorher Naht am Anfang der KI-Linie).
  Gefahren mit Vorsteuerung: Seitengeschwindigkeit = Tempo x Steigung der Linie + kleine Korrektur. Zufallsversatz gleitet,
  höchstens 0,5 m. Klone dürfen die Randsteine nutzen wie der Spieler.
- Trial Mountain, Tees BMW-Runden: im Schnitt 0,75 -> 0,26 m neben seiner Linie, max 4,4 -> 1,2-1,8 m,
  Sprünge der Seitengeschwindigkeit > 0,5 m/s pro Frame 2-3 -> 0.

## Neu in Teil 20: Persönlichkeit im Fahrverhalten
- Messwerkzeug: `RaceAiTool sim --style` (Linienabstand der Persönlichkeiten in Kurven, Angriffe/Überholungen/Verteidigungen,
  Kontakte, Gras, Seitenabstand, 0-100 beim Start), `--personality X`, `--personalities A,B`.
- Vorher: alle Persönlichkeiten in Kurven 0,01 m auseinander, Seitenabstand für alle gleich (~0,5 m).
- Eigene Linie (`PersonalLineOffset`): Muster der KI-Linie quer über die Strecke entlang verschoben (Apex früher/später, bis ±7 m)
  plus Rand-/Randsteinnutzung (+0,4 / -0,6 m an den Stellen, wo die Linie am Rand ist). Planer rechnet nur den Radiuseffekt
  (die Eigenkrümmung der Linie zählt als Fahrstil), sonst wären Linien bis 10 % langsamer. Ergebnis: Chill vs DiveBomber im Schnitt
  1,3 m auseinander (max 4 m), Rundenzeiten aller Persönlichkeiten innerhalb 0,7 %. Randsteine zählen für diese Fahrer nicht als Gras.
- Zweikampf: `Room` (Seitenabstand, wer bei Gleichstand zurücksteckt), zurückstecken in Kurven mit leichtem Lupfen, Angriff
  (`Attack`: Reichweite, nötiger Vorteil, kürzere Lückenprüfung, schnellere Autos vorne blockieren nicht, keine Zielwechsel
  mittendrin, Seitenwechsel wenn zu, Zeitlimits in Sekunden), Commit beim Angriff (+2..6 % Pace, später bremsen, mehr Verbremser),
  Verteidigen (`Defend`, eine Bewegung, nicht wenn der Angreifer schon innen ist, gegen Spieler nur mit Abstand), Chill lässt vorbei.
- Start: Reaktionszeit, Kupplung, Wheelspin/Bog, Drehzahl beim Start; Gras nach zu frühem Gas (`GreedyExitChance`).
- Gummiband nach CSP-Vorbild (`UpdateRubberBand`, `PaceBoost`).

## Neu in Teil 21: Fahrzeugklassen und Hybrid-Antrieb
- `race-ai/classes/classes.json`: Klassen GT3 (12 Autos), GTE/GT2, LMP1, JDM mit Modellen und Skins.
- `race-ai/set-class.py`: stellt `cfg/` oder ein Preset um (entry_list MODEL/SKIN, Spieler- und KI-Plätze getrennt reihum,
  KI mit anderen Skins als die Spieler; CARS=, Klassenname in NAME/ServerDescription/welcome.txt). `--new-preset X --from Y`
  kopiert ein Preset und biegt `GridFile` um; `--all` für cfg/ und alle Race-AI-Presets. Klassenwechsel = Rotationswechsel
  (Server startet neu, Spieler treten über CM neu bei).
- Hybrid: `CarDataLoader` liest `ers.ini` (KINETIC an der Kurbelwelle mit Gang × Achse, FRONT_MOTORS an der Vorderachse mit eigener
  Traktion) und den Deploy-Controller (`ctrl_ers_<DEFAULT_CONTROLLER>.ini`: SPEED_KMH- und GEAR-Tabellen) zu `ErsGain`/`ErsPower`.
  `CarSpec.SetTrack(länge)` (aus `RaceWorld.Step`) rechnet den Anteil, den MAX_KJ_PER_LAP auf der Strecke hergibt (Deploy über ~45 %
  der Runde): TS040 100 % auf Trial Mountain, 27 % Nordschleife. `RaceAiTool car` zeigt das, `RaceAiTool acd --model X [--file a,b]`
  listet/zeigt den Inhalt einer data.acd.
- KI bei 100 % auf Trial Mountain: GT3 1:30,7–1:33,2, GTE 1:31,4–1:32,5, LMP1 1:19,9–1:22,0, JDM 1:46,3–1:52,5.

## Neu in Teil 22: /bot im eigenen Modell, Live-Timing
- `/bot`: Zuschauen nur noch aus einem Auto des eigenen Modells (`SpareCar(own, guid)`): freier Spielerplatz, sonst wird ein Bot-Platz
  geliehen (`BotSlot.Lend`): Bot fährt weiter mit eigenem Status (`_standInStatus`), sein Ergebnis wird nach dem Beitritt zurückgesetzt
  (`SavedResult`), `CarConnected` mit Botnamen erneut an alle. Slot-Filter öffnet nur den zugewiesenen Platz (`WatchSeat` /
  `PendingBot.Seat`). Freigabe beim Verlassen, bei neuer Session oder nach 3 min unbenutzt. Kontakte des Zuschauers werden ignoriert.
  Lokal getestet (Fake-Client landet in Slot 4, Bot fährt weiter).
- Live-Timing: `LiveFeed` (Singleton, Server-Sent Events, eine Berechnung pro Takt für alle), `RaceAiService.LiveSnapshot()`,
  `LivePageHtml`, `PlayerStats.Licence()`, `RaceWorld.ExternalInPitLane()`. Abstände über Zeitmesspunkte (Runde × ~20 m) mit
  Interpolation zwischen den Frames; Fortschritt monoton gemacht (Linie überquert, Runde noch nicht gezählt).

## Neu in Teil 23: Klassenwechsel per Einstellung / Admin-Befehl
- `ClassPresets.cs`: `ClassCatalog` (classes.json eingebettet, Überschreiben mit `classes.json` bzw. `cfg/classes.json`; Klasse eines
  Presets = Mehrheit der Modelle), `ClassPresets.Scan/Info/Resolve/Generate` (Preset gleicher Strecke + Klasse suchen, eigene zuerst,
  sonst `presets/<strecke>-<klasse>` als Overlay erzeugen, Marker `.raceai-class.json`, Port von set-class.py `AssignCars`).
- `TrackRotation`: `Class:` in rotation.yml, `_currentEntry` (Rotations-Eintrag des laufenden Presets), `PresetFor`, `SetClass(key, now)`
  (schreibt `Class:` in rotation.yml, legt rotation.yml an wenn keine da), `Change(...)` für Strecken- und Klassenwechsel (KeepRaces).
  Beim Start ohne Supervisor: letzte Strecke + konfigurierte Klasse → ggf. Neustart ins Klassen-Preset. Vorkalibrierung nimmt die
  Klassen-Presets der Rotation.
- Befehl `/raceai_class` (`/class`), Dashboard-Karte „Fahrzeugklasse“, API `action: "class"`, `text: "gte"` oder `"gte next"`.
- Lokal getestet: gte jetzt → nordschleife-gte erzeugt und gestartet, Rotation → trialmountain-gte, lmp1 next + Neustart (Panel-Start mit
  cfg/) → trialmountain-lmp1, gt3 jetzt → zurück auf trialmountain.

## Wie gebaut wurde (ohne .NET 11 SDK)
In der Cloud-Sitzung gab es kein NuGet und kein .NET 11 SDK. Der Trick:
1. Offizielles Release `assetto-server-linux-x64.tar.gz` (v0.0.55-pre42) laden. Es ist ein Single-File-Bundle mit .NET 11 RC,
   allen NuGet-DLLs und dem Runtime.
2. Das Bundle entpacken (`sfextract.py`) und mit `csc` aus dem .NET 10 SDK gegen diese DLLs kompilieren:
   den gepatchten AssettoServer (der CommunityToolkit-Generator wurde in `AiParams` durch normale Properties ersetzt, nur für den Check)
   und danach das Plugin.
3. `rebundle.py` tauscht `AssettoServer.dll` im Bundle aus. Das Ergebnis ist ein lauffähiger Test-Server.
Die Skripte liegen in `tools/testbuild/` im Übergabe-Ordner auf dem Rechner.

## Dateien
- `AssettoServer/…`: Core-Patch (`IExternalAiController`, `EntryCar`, `EntryCarAi`, `AiBehavior`, `SessionManager`, `ACTcpClient`)
- `RaceAiPlugin/Core/`: `RaceWorld` (KI), `RacingLine`, `FastLaneFile`, `CarSpec`, `CarDataLoader` (data.acd wie CM), `AcdReader`, `Kn5Reader`,
  `TrackInfo` (ai_hints, sections, Grid, Pits), `IniFile`/`Lut`, `BotNames`
- `RaceAiPlugin/`: `RaceAiService` (Sessions, Grid, Timing, Tick), `BotSlot` (Pose → CarStatus), `RaceAiSlotFilter`, `RaceAiCommandModule`,
  `RaceAiConfiguration`, `TrackData`
- `RaceAiTool/`: `sim`, `grid`, `car`, `selftest`
- `RaceAiPlugin/example/nordschleife-gt3/`: Konfiguration und `prepare-content.sh`

## Offene Punkte / Ideen
1. Test mit dem echten Client: Höhe der Autos (`HeightOffset`), Lenkrad- und Radwinkel-Kodierung (`BotSlot.WriteStatus`, geraten),
   Gang-Anzeige, Bremslichter, das Aussehen bei 20 Hz
2. Überholen: Züge ähnlich schneller Autos bleiben oft zusammen. Mögliche Stellschrauben: `PressureEma`-Schwelle, Windschatten, Innenbahn-Vorrecht.
3. Blinkerkodierung (IndicateLeft/Right) und Lichthupe mit dem echten Client prüfen.
4. Boxenstopps und Reifen, fliegender Start, Formationsrunde
5. Weitere Strecken: Es sollte generisch funktionieren (fast_lane.ai v7, kn5-Grid). Nur mit der Nordschleife getestet.
