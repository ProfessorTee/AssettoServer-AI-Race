# AssettoServer AI Race – Renn-KI für AssettoServer

KI-Gegner für Assetto Corsa auf einem **AssettoServer**, wie im Einzelspieler, aber online. Bots fahren Training, Qualifying
und Rennen, stehen in der echten Startaufstellung, und ihre Runden zählen offiziell. Freunde können dazukommen und jedes Auto
nehmen, ein Bot macht dann Platz. Erstes Ziel: **Nordschleife** mit **GT3**-Autos.

> Entwickelt von **Claude** ([Anthropic](https://www.anthropic.com), Claude Code) zusammen mit
> [ProfessorTee](https://github.com/ProfessorTee), der im echten Spiel getestet und die Richtung vorgegeben hat.
> Basis: [AssettoServer](https://github.com/compujuckel/AssettoServer) von compujuckel, **AGPL-3.0**
> (Original-README: [`README.AssettoServer.md`](README.AssettoServer.md)).

## Zwei Varianten

| Branch | Bots | Stand |
|---|---|---|
| `main` | **kinematisch**: Die Bots folgen der Ideallinie mit berechnetem Tempo, die Pose wird gesetzt. Dazu Persönlichkeiten, Fahrer-Klone, Gummiband, Safety Car, Flaggen. | ausgeliefert |
| `bots-physics` (dieser) | **Physik-Rewrite**: echtes Fahrzeugmodell plus nachgebauter Kunos-Fahrer aus `acs.exe` | in Arbeit, läuft auf dem Testserver |

### Was der Rewrite ändert

Die kinematischen Bots sahen aus wie Autos, fuhren aber nicht wie welche: kein Grip-Limit, keine Masse, Kontakte und Kerbs nur
simuliert. Auf `bots-physics` fährt jeder Bot ein Fahrzeug mit Physik nach AC, gelenkt von derselben Logik wie die
Einzelspieler-KI:

- **Fahrzeug** (`BotDriverPlugin/Core/Physics/`): alle Daten aus `data.acd` (Motor, Turbo, Getriebe, Differenzial,
  Aufhängungsgeometrie, Federn, Dämpfer, Aero, ABS/TC), Reifenmodell wie AC mit Temperatur und Druck.
- **Fahrer** (`Core/RaceWorld.Kunos.cs`): Kunos `AIDriver`, per Ghidra aus `acs.exe` ausgelesen. Kurvenformel, Bremsplanung,
  Lenk-PID, die unsichtbaren Fahrhilfen der AC-KI, Zweikampf, Humanizer. `AiStrength` = `AI_LEVEL` wie im Einzelspieler.
- **Strecke unter jedem Rad**: Höhe, Kerbs und Grip direkt aus dem Strecken-Mesh und `surfaces.ini`.
- **Client-Netcode nachgebaut**: Pose und Zeitstempel so, wie der AC-Client fremde Autos glättet. Dadurch kein Versinken,
  kein Schnipsen, Kontakte als echte Stöße.

Analyse und Adressen: [`BotDriverPlugin/UEBERGABE_AC_KI.md`](BotDriverPlugin/UEBERGABE_AC_KI.md).
Funktionen, Einstellungen und Grenzen: [`BotDriverPlugin/README.md`](BotDriverPlugin/README.md).

## Aufbau

Vier Plugins, jedes läuft allein und nutzt die anderen, wenn sie da sind (Schnittstellen in `AssettoServer/Server/Extensions/`).

| Pfad | Inhalt |
|---|---|
| `AssettoServer/…` | **Core-Patch**: externe KI-Slots, offizielle Bot-Runden, Preset-Ebenen, Uhr-Sync, Plugin-Schnittstellen |
| [`BotDriverPlugin/`](BotDriverPlugin/README.md) | Die Bots. `Core/` ohne AssettoServer-Abhängigkeit, `Tool/` Offline-Simulator und Selbsttest |
| [`WebPortalPlugin/`](WebPortalPlugin/README.md) | Webseiten: `/join`, `/live`, `/stats`, `/admin` |
| [`ServerToolsPlugin/`](ServerToolsPlugin/README.md) | Rotation, Klassen, Statistik/Safety Rating, echtes Wetter, Neustart |
| [`DriverRecorderPlugin/`](DriverRecorderPlugin/README.md) | Zeichnet Spieler mit Zustimmung auf (`/rec on`) |
| `Shared/` | Gemeinsamer Quellcode (Streckengeometrie, Klassen, Admin-Zugriff) |
| `examples/nordschleife-gt3/` | Beispielkonfiguration: 4 Spieler, 16 Bots |
| `tools/` | Build, Testserver ohne .NET SDK, vorgebaute DLLs, Benchmark |

Alte Server mit `RaceAiPlugin` laufen weiter: Die alte Konfiguration wird beim ersten Start auf die neuen Plugins aufgeteilt.

## Schnellstart (Linux, ohne .NET SDK)

```bash
tools/setup-testserver.sh /pfad/zu/steamapps/common/assettocorsa   # lädt AssettoServer und richtet alles ein
tools/server-desktop.sh                                             # Server + Admin-Seite starten
tools/update-server.sh                                              # nach git pull: neue Plugins einspielen
```

Beitreten in Content Manager über *Online → LAN* oder `IP:8081` (CSP nötig). Von außen: Ports 9600 TCP+UDP und 8081 TCP
weiterleiten.

## Bauen und testen

```bash
tools/build.sh                                               # .NET 11 SDK, Ausgabe out-linux-x64/
dotnet run --project BotDriverPlugin/Tool -c Release -- selftest
tools/bench.sh "was geändert"                                # 18 Rennen offline, Vergleich in tools/bench-results.tsv
```

## Lizenz

AGPL-3.0 wie AssettoServer. Alle Änderungen stehen in der Git-Historie (ab `e92d253`).
