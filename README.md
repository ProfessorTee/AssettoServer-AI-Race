# AssettoServer AI Race – Renn-KI für AssettoServer

KI-Gegner für Assetto Corsa auf einem **AssettoServer**, wie im Einzelspieler, aber online. Bots fahren Training, Qualifying
und Rennen mit und stehen in der echten Startaufstellung. Ihre Rundenzeiten erscheinen in Leaderboard und Ergebnissen.
Freunde können dazukommen und jedes Auto wählen. Wenn nötig, macht ein Bot Platz.

Erstes Ziel: **Nordschleife** (`ks_nordschleife`, Layout `nordschleife`) mit **GT3**-Autos.

> **Entstehung:** Dieses Projekt hat **Claude**, eine KI von [Anthropic](https://www.anthropic.com), im Auftrag von und
> zusammen mit [ProfessorTee](https://github.com/ProfessorTee) entwickelt (Claude Code, Modell `claude-opus-5-5`).
> Claude hat den Core-Patch, die Plugins BotDriver, WebPortal, ServerTools und DriverRecorder, das Test-Werkzeug `BotDriverTool`, die Tests, die Build-Skripte und die
> Dokumentation geschrieben. ProfessorTee hat mit dem echten Spiel getestet, die Richtung vorgegeben und Rückmeldung gegeben.
>
> Basis ist [AssettoServer](https://github.com/compujuckel/AssettoServer) von compujuckel und Mitwirkenden, Lizenz
> **AGPL-3.0** (siehe `LICENSE`). Die ursprüngliche README steht in [`README.AssettoServer.md`](README.AssettoServer.md).

## Was die Bots können

- Sie fahren auf der Kunos-Ideallinie (`fast_lane.ai`) und nutzen **Fahrzeugdaten wie Content Manager** direkt aus `data.acd`:
  Gewicht, Motor, Getriebe, Reifen, Aero.
- **Stärke in %** mit ± Streuung (`AiStrength`, `AiStrengthSpread`). Die Kalibrierung rechnet das pro Auto in eine Zielzeit um.
- **Wie Menschen:** Schwächere Bots bremsen früher, gehen später aufs Gas und machen Fehler: zu spät gebremst, zu viel Gas am
  Kurvenausgang, ab und zu zwei Räder auf dem Gras, selten ein Dreher mit Warnblinker.
- **Racecraft:**
  - Windschatten, Angriff, Verteidigung, Innenbahn-Vorrecht.
  - Ungeduld mit Lichthupe, wenn ein Bot hinter einem langsameren Auto festhängt.
  - Blaue Flagge: Der Überrundete fährt an den Rand und setzt den Blinker.
  - Gelbe Flagge: Warnblinker, langsamer, kein Überholen.
  - Leichte Berührungen in engen Zweikämpfen.
- **Endurance:** Sprit, Reifenverschleiß, **Schaden** mit Reparatur und **Boxenstopps** über `pit_lane.ai`, mit Strategie.
- **Wetter:** Regen macht die Strecke rutschig. Optional kommt echtes Wetter von Open-Meteo über CSP WeatherFX (Sol/Pure).
- **Fahrer-Klone:** Das Plugin `DriverRecorderPlugin` zeichnet Spieler auf, die mit `/rec on` zustimmen (CSP-Skript, das der Server
  mitschickt). Daraus entsteht ein Klon, der ihre Linie und ihr Tempo fährt. Verliert ein Spieler im Rennen die Verbindung, fährt
  sein Klon sein Auto weiter, bis er zurück ist. Siehe [`DriverRecorderPlugin/README.md`](DriverRecorderPlugin/README.md).
- **Admin-Seite** im Browser mit Live-Karte, Beitreten-Seite für Freunde, Live-Timing und Neustart/Update aus der Ferne.
- Jede Funktion lässt sich in der yml des Plugins abschalten und als Admin auch live per `/bots_set`.

Die Doku jedes Plugins steht in seinem Ordner. Der Stand für Entwickler: [`BotDriverPlugin/UEBERGABE.md`](BotDriverPlugin/UEBERGABE.md).

## Aufbau

Vier Plugins, jedes läuft allein und ergänzt die anderen, wenn sie da sind (Schnittstellen im Kern, `AssettoServer/Server/Extensions/`).

| Pfad | Inhalt |
|---|---|
| `AssettoServer/…` | **Core-Patch**: externe KI-Slots, offizielle Runden für Bots, Preset-Ebenen, Uhr-Sync, Plugin-Schnittstellen, Übernahme alter Konfiguration |
| [`BotDriverPlugin/`](BotDriverPlugin/README.md) | Die Bots: Fahren, Duelle, Klone, Fahrerwechsel. `Core/` ist die KI ohne AssettoServer-Abhängigkeit, `Tool/` das Offline-Werkzeug |
| [`WebPortalPlugin/`](WebPortalPlugin/README.md) | Webseiten: `/join`, `/live`, `/stats`, `/admin` |
| [`ServerToolsPlugin/`](ServerToolsPlugin/README.md) | Rotation, Klassen, Statistik/Safety Rating, echtes Wetter, Neustart, gemeinsame Einstellungen |
| [`DriverRecorderPlugin/`](DriverRecorderPlugin/README.md) | Zeichnet Spieler mit Zustimmung auf (`/rec on`), Grundlage der Fahrer-Klone |
| `Shared/` | Quellcode, den mehrere Plugins mitkompilieren (Streckengeometrie, Klassen, Admin-Zugriff, Config-Schreiber) |
| `examples/nordschleife-gt3/` | Beispielkonfiguration: 4 Spieler-Slots, 16 Bots |
| `tools/` | Build, Testserver ohne .NET SDK (`setup-testserver.sh`, `update-server.sh`, `start-server.sh`), vorgebaute DLLs |

Alte Server mit `RaceAiPlugin` laufen ohne Änderung weiter: `RaceAiPlugin` in `EnablePlugins` lädt die neuen Plugins, und
`plugin_race_ai_cfg.yml` wird beim ersten Start auf `plugin_bot_driver_cfg.yml`, `plugin_web_portal_cfg.yml` und
`plugin_server_tools_cfg.yml` aufgeteilt. Der alte Ordner `plugins/RaceAiPlugin` wird nicht mehr geladen und kann weg.

## Schnellstart (Linux, ohne .NET SDK)

```bash
tools/setup-testserver.sh /pfad/zu/steamapps/common/assettocorsa   # lädt AssettoServer v0.0.55-pre42 und richtet alles ein
tools/install-desktop-entry.sh                                      # Starter ins Startmenü und auf den Desktop
tools/server-desktop.sh                                             # Server + Admin-Seite starten
```

Nach einem `git pull` spielt `tools/update-server.sh` die neuen Plugins ein und ergänzt die Konfiguration.

Beitreten in Content Manager: *Online → LAN* oder die Favoriten mit `IP:8081`. CSP muss installiert sein.
Für Freunde von außen: im Router die Ports 9600 TCP+UDP und 8081 TCP an deinen PC weiterleiten.

## Bauen (sauberer Weg)

Mit dem .NET 11 SDK: `tools/build.sh` (oder `tools/build.sh win-x64`) baut Server und Plugins nach `out-linux-x64/`.

```bash
dotnet run --project BotDriverPlugin/Tool -- selftest
```

## Lizenz

AGPL-3.0, wie AssettoServer. Alle Änderungen gegenüber dem Original stehen in der Git-Historie (Commits ab `e92d253`).
