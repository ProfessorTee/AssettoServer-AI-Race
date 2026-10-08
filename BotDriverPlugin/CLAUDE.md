# Sitzung: BotDriverPlugin
Ziel: Bots fahren so realistisch/menschlich wie möglich. Nutzer-Vorlieben: kein freiwilliges Vorbeilassen schnellerer Autos;
aggressive Fahrer nutzen die ganze Breite und verlieren Plätze nur durch eigene Fehler; Chill-Fahrer überholen nur, wenn es
absolut sicher ist und sie sonst deutlich aufgehalten würden.
- Nur in `BotDriverPlugin/` arbeiten (Core/ = KI ohne Server-Abhängigkeit, Tool/ = Simulator). Andere Plugins nicht ändern.
- Vor/nach Fahrverhalten-Änderungen messen: `tools/bench.sh "was geändert"` (18 Rennen parallel, ~1,5 min, Tabelle vorher/nachher in `tools/bench-results.tsv`; einzelne Läufe streuen stark), Selftest muss OK sein.
- Aufnahmen echter Runden des Nutzers: `~/Downloads/Server_Files/<datum>/recordings/...` (`sim --hotlap --rec <ordner>` vergleicht Bremspunkte und Kurventempo pro Kurve, `--dump` schreibt die Runde als CSV).
- AC-Pfad: /mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa. Doku: README.md, UEBERGABE.md.
