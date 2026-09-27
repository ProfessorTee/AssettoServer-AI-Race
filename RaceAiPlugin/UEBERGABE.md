# Race-AI für AssettoServer: Übergabe (Stand 27.09.2026, 2. Sitzung)

## Status
- Der Core-Patch und das Plugin **kompilieren** gegen AssettoServer master (Basis `e92d253`, entspricht Release `v0.0.55-pre42`).
- **End-to-End-Test** mit dem echten Server (linux-x64) und einem simulierten AC-Client (Handshake, Checksummen, UDP, Ping,
  Positionsupdates, Runden, Kollision) auf der Nordschleife mit 16 GT3-Bots:
  Qualifying → Startaufstellung (Spieler in der Mitte) → Start → 1 Runde → Zielflagge → Ergebnis → nächste Session.
  Siegerzeit 6:59, Feld 6:59–7:08, keine ungültigen Posen, Bot-Namen beim Client, Überholungsansagen mit Streckenabschnitt.
- Offline: `RaceAiTool selftest` grün. `RaceAiTool sim` auf der echten Linie: Hotlap bei Level 100 6:52.8, Rennen ohne Überlappungen.
- Noch **nicht** mit dem echten AC-Client und CSP getestet. Das ist der nächste Schritt.

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
3. Blaue Flaggen: Überrundete Bots machen noch nicht aktiv Platz.
4. Boxenstopps und Reifen, fliegender Start, Formationsrunde
5. Weitere Strecken: Es sollte generisch funktionieren (fast_lane.ai v7, kn5-Grid). Nur mit der Nordschleife getestet.
