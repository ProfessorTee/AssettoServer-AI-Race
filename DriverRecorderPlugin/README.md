# DriverRecorderPlugin – Fahrweise von Spielern aufzeichnen

Zeichnet auf, wie echte Spieler fahren, damit die Race AI daraus **Fahrer-Klone** bauen kann. Ein Klon fährt die Linie und die
Geschwindigkeiten des Spielers, Kurve für Kurve, mit seiner Streuung. Verliert ein Spieler im Rennen die Verbindung, fährt sein Klon
sein Auto weiter, bis er zurück ist (Einstellung `TakeOverDisconnectedPlayers` der Race AI).

> Entwickelt von **Claude** (Anthropic) zusammen mit ProfessorTee, wie das BotDriverPlugin. Lizenz AGPL-3.0.

## So funktioniert es

- **Client:** Der Server schickt jedem Spieler mit **CSP** ein kleines Lua-Skript (`lua/driverrecorder.lua`, Online-Skript).
  Niemand muss etwas installieren. Ohne CSP wird nichts aufgezeichnet.
- **Nur mit Zustimmung:** Das Skript sendet erst, nachdem der Spieler im Chat `/rec on` geschrieben hat. Beim Beitreten gibt es
  einen Hinweis. Oben links steht dann ein rotes **REC** mit der Zahl der sauberen Runden (mit diesem Auto auf dieser Strecke)
  und der besten davon.
  - `/rec on` – zustimmen, Aufzeichnung an (gilt auch für spätere Besuche)
  - `/rec off` – aus, gespeicherte Runden bleiben
  - `/rec delete` – alle eigenen Aufzeichnungen löschen, Aufzeichnung aus
  - `/rec` oder `/rec info` – was aufgezeichnet wird und wie viele Runden es gibt
- **Daten:** 20-mal pro Sekunde Gas, Bremse, Kupplung, Lenkwinkel, Gang, Position, Tempo, Streckenposition, dazu
  Boxengasse/ABS/TC/neben der Strecke; einmal pro Sekunde Reifentemperaturen, Reifenverschleiß, Reifendruck und Sprit.
  Das sind genau die Werte, die das normale AC-Netzwerkprotokoll nicht überträgt (dort gibt es z. B. keinen Bremsdruck).
- **Runden:** Der Server schneidet die Daten an der Ziellinie in Runden und speichert jede Runde als gzip-CSV:
  `recordings/<SteamID>/<Strecke>-<Layout>/<Auto>/<Datum>_<Rundenzeit ms>_valid|invalid.csv.gz`.
  Gültig ist eine Runde, wenn sie vollständig ist, keine Cuts hat, nicht durch die Box ging und nicht länger neben der Strecke war.
  Pro Spieler, Strecke und Auto bleiben die letzten `MaxLapsPerCar` Runden (erst ungültige, dann die ältesten werden gelöscht).
  `recordings/optin.json` enthält, wer zugestimmt hat.

## Einrichtung

`extra_cfg.yml`:

```yaml
EnablePlugins:
  - BotDriverPlugin
  - DriverRecorderPlugin
EnableClientMessages: true
```

`cfg/plugin_driver_recorder_cfg.yml` (optional, das sind die Standardwerte):

```yaml
SampleHz: 20            # Messungen pro Sekunde (5-50)
RecordingsFolder: recordings
JoinHint: true          # Hinweis beim Beitreten
MaxLapsPerCar: 40
Language: de            # de oder en
```

## Was die Race AI daraus macht

- **Profil:** Aus den sauberen Runden (bis 4 % langsamer als die beste, höchstens 12) entsteht alle 4 m entlang der Ideallinie:
  Tempo, seitlicher Abstand zur Ideallinie (seine Linie) und wie stark der von Runde zu Runde schwankt, Gas und Bremse.
- **Klon als Bot:** In `plugin_bot_driver_cfg.yml` unter `Drivers`: `- Slot: 5` und `Clone: <Name oder SteamID>`.
- **Übernahme:** Verliert ein Spieler im Rennen die Verbindung, fährt ein Bot sein Auto an derselben Stelle ins Ziel (BotDriverPlugin).
  Mit sauberen Runden fährt dieser Bot wie der Spieler (sein Klon). Die Runden zählen für den Spieler.
