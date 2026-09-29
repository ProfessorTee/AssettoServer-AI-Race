# AssettoServer AI Race – Renn-KI für AssettoServer

KI-Gegner für Assetto Corsa auf einem **AssettoServer**, wie im Einzelspieler, aber online. Bots fahren Training, Qualifying
und Rennen mit und stehen in der echten Startaufstellung. Ihre Rundenzeiten erscheinen in Leaderboard und Ergebnissen.
Freunde können dazukommen und jedes Auto wählen. Wenn nötig, macht ein Bot Platz.

Erstes Ziel: **Nordschleife** (`ks_nordschleife`, Layout `nordschleife`) mit **GT3**-Autos.

> **Entstehung:** Dieses Projekt hat **Claude**, eine KI von [Anthropic](https://www.anthropic.com), im Auftrag von und
> zusammen mit [ProfessorTee](https://github.com/ProfessorTee) entwickelt (Claude Code, Modell `claude-opus-5-5`).
> Claude hat den Core-Patch, das `RaceAiPlugin`, das Test-Werkzeug `RaceAiTool`, die Tests, die Build-Skripte und die
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
- **Dashboard** zum Steuern im Browser, mit Live-Karte, Beitreten-Seite für Freunde und Neustart/Update aus der Ferne.
- Jede Funktion lässt sich in `plugin_race_ai_cfg.yml` abschalten und als Admin auch live per `/raceai_set`.

Die ausführliche Doku mit allen Einstellungen und Befehlen steht in [`RaceAiPlugin/README.md`](RaceAiPlugin/README.md).
Der Stand für Entwickler steht in [`RaceAiPlugin/UEBERGABE.md`](RaceAiPlugin/UEBERGABE.md).

## Aufbau

| Pfad | Inhalt |
|---|---|
| `AssettoServer/…` | Kleiner **Core-Patch**: `IExternalAiController` (ein Plugin steuert einen KI-Slot), offizielle Runden für Bots, Rennen mit nur einem Spieler |
| `RaceAiPlugin/` | Das Plugin. `Core/` ist die eigentliche KI, ohne AssettoServer-Abhängigkeit |
| `DriverRecorderPlugin/` | Zweites Plugin: zeichnet Spieler mit Zustimmung auf (CSP-Online-Skript), Grundlage der Fahrer-Klone |
| `RaceAiTool/` | Offline-Werkzeug: `selftest`, `sim` (Rennen simulieren), `strength`, `car`, `grid` |
| `RaceAiPlugin/example/nordschleife-gt3/` | Beispielkonfiguration: 4 Spieler-Slots, 16 Bots |
| `race-ai/` | Test-Build ohne .NET 11 SDK: gepatchte DLLs, `setup-testserver.sh`, `start-server.sh`, Hilfsskripte |

## Schnellstart (Linux, ohne .NET SDK)

```bash
race-ai/setup-testserver.sh /pfad/zu/steamapps/common/assettocorsa   # lädt AssettoServer v0.0.55-pre42 und richtet alles ein
race-ai/install-desktop-entry.sh                                      # „Race AI Server“ ins Startmenü und auf den Desktop
race-ai/raceai-desktop.sh                                             # Server + Dashboard (Live-Karte, Bots steuern) starten
```

Nach einem `git pull` spielt `race-ai/update-server.sh` das neue Plugin ein und ergänzt die Konfiguration.

Beitreten in Content Manager: *Online → LAN* oder die Favoriten mit `IP:8081`. CSP muss installiert sein.
Für Freunde von außen: im Router die Ports 9600 TCP+UDP und 8081 TCP an deinen PC weiterleiten.

## Bauen (sauberer Weg)

Mit dem .NET 11 SDK:

```bash
dotnet publish AssettoServer/AssettoServer.csproj -c Release -r linux-x64      # oder win-x64
dotnet publish RaceAiPlugin/RaceAiPlugin.csproj -c Release -r linux-x64
dotnet publish DriverRecorderPlugin/DriverRecorderPlugin.csproj -c Release -r linux-x64
dotnet run --project RaceAiTool -- selftest
```

## Lizenz

AGPL-3.0, wie AssettoServer. Alle Änderungen gegenüber dem Original stehen in der Git-Historie (Commits ab `e92d253`).
