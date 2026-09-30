# DriverRecorderPlugin – Fahrweise von Spielern aufzeichnen

Zeichnet auf, wie echte Spieler fahren, damit die Race AI daraus **Fahrer-Klone** bauen kann. Ein Klon fährt die Linie und die
Geschwindigkeiten des Spielers, Kurve für Kurve, mit seiner Streuung. Verliert ein Spieler im Rennen die Verbindung, fährt sein Klon
sein Auto weiter, bis er zurück ist (Einstellung `TakeOverDisconnectedPlayers` der Race AI).

> Entwickelt von **Claude** (Anthropic) zusammen mit ProfessorTee, wie das RaceAiPlugin. Lizenz AGPL-3.0.

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
  - RaceAiPlugin
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
- **Klon als Bot:** In `plugin_race_ai_cfg.yml` unter `Drivers`: `- Slot: 5` und `Clone: <Name oder SteamID>`.
- **Übernahme:** Verliert ein Spieler mit sauberen Runden im Rennen die Verbindung, übernimmt sein Klon das Auto an derselben
  Stelle, in derselben Runde. Die Runden zählen für den Spieler. Andere Spieler können das Auto solange nicht nehmen.
- **Fahrerwechsel wie im Langstreckenrennen** (Race AI, CSP-Skript `driverswap.lua` vom Server):
  - **Rückkehr:** Tritt der Spieler wieder bei, fährt sein Klon weiter. Der Spieler wird in ein freies Ersatzauto in der Box gesetzt
    (irgendein freier Spieler-Platz) und kann zuschauen; ein Banner zeigt Position und wann der Klon an die Box kommt. Der Klon fährt
    bei der nächsten Boxeneinfahrt rein, hält in seiner Box, und der Spieler wird automatisch in sein Auto gesetzt und fährt weiter.
  - **`/bot` oder `!bot`** (Pause, lange Rennen): in die eigene Box fahren und anhalten, der Klon übernimmt dort, der Spieler schaut
    aus einem Ersatzauto zu. Während der Klon zur Box fährt, lässt `/bot` ihn weiterfahren.
  - **`/play` oder `!play`**: der Klon kommt bei der nächsten Boxeneinfahrt rein, dann übernimmt der Spieler wieder.
  - Gibt es kein freies Ersatzauto oder kein CSP, übernimmt der Spieler beim Beitreten sofort (ohne CSP: nach dem Fahrerwechsel-Hinweis
    den Server verlassen und neu beitreten). Kommt er zum Wechsel nicht innerhalb von 90 s, fährt der Klon weiter.
- Im Dashboard (Reiter KI → „Fahrer-Klone“) steht, wer wie viele Runden aufgezeichnet hat.
