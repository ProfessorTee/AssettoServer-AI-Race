# Übergabe: Wie fährt die Kunos-KI in Assetto Corsa? (Stand 11.10.2026)

Reverse Engineering von `acs.exe` (AC 1.16, sha256 `0df569c8…3fd1`) mit Ghidra 12.1 über REA. Ziel: verstehen, warum die
AC-KI schnell und sauber fährt, und prüfen, was davon ins BotDriverPlugin passt. Abschnitte 0–10: Analyse; 11: die
Physik-Bots im Branch `bots-physics`.

## Methode (zum Nachprüfen)
- `acs.pdb` liegt neben der exe. Ghidra übernimmt die Symbole nicht automatisch, deshalb die Namen aus den Publics:
  `llvm-pdbutil dump --publics acs.pdb`. Die Adresse ist dann `0x140001000 + Offset aus "addr = 0001:<offset>"`.
- Wichtige Funktionen:

| Funktion | Adresse | Inhalt |
|---|---|---|
| `AIDriver::AIDriver` (Konstruktor) | `0x140298c60` | Standardwerte, liest `ai.ini`, „Genome“, Setup, KERS |
| `AIDriver::acquireControls` | `0x14029baa0` | Ablauf pro Physikschritt |
| `AIDriver::stepCarsAwareness2` | `0x14029f8d0` | andere Autos, Nebeneinander, Überholseite |
| `AIDriver::getEngagedDesiredOffset` | `0x14029d3e0` | Abstand zu Autos nebenan |
| `AIDriver::stepGasBrake` | `0x1402a08e0` | Zielgeschwindigkeit, Gas, Bremse, Wiedereinfädeln |
| `AIDriver::getLABraking` | `0x14029d650` | Bremsen mit Vorausschau |
| `AIDriver::stepSteer` | `0x1402a2630` | Lenkung |
| `AIDriver::stepSuperhuman` | `0x1402a2a60` | Fahrhilfen und unsichtbare Kräfte |
| `AIDriver::stepHumanizer` | `0x1402a19e0` | zufällige Fehler |
| `AIDriver::stepPushVariations` | `0x1402a1f20` | Angriffslust ändert sich über die Zeit |
| `AIDriver::getLateralPush` | `0x14029dd10` | effektive „Push“-Stufe |
| `AISplineRecorder::loadHints` | `0x140295670` | `ai_hints.ini` |
| Kurvenformel / Querbedarf | `0x1402cb0f0` / `0x1402cb0b0` | siehe unten |
| Kurvenradius aus dem Spline | `0x1402a6bb0` | Umkreis aus 3 Punkten im Abstand ±15 m |

- Feld `car+0x3238` ist der Index des Autos (der Konstruktor nutzt `index % 5` zum Versetzen der Schritte). „≠ 0“ heißt also
  „nicht das Auto des Spielers“. Das ist eine Schlussfolgerung, keine direkte Beobachtung: Humanizer, Push-Schwankung und Genome
  gelten nur für Gegner.
- Mit `llvm-objdump -d` über die ganze exe lassen sich Schreibzugriffe auf ein Feld suchen (`grep "+ 0x9c], "`) und über die
  Publics der Funktion zuordnen. So wurden die Feldbedeutungen unten abgesichert.

## 0. Einstellungen: `AI_LEVEL` und `AI_AGGRESSION`
`RaceManager::initOffline` liest beide Werte aus `race.ini`. Die Funktion bei `0x140133e00` legt den `AIDriver` an:
- **Level** `ai+0x28` = clamp(globales Level % × Auto-Level % / 10000, 0..1). Ist das globale Level 0, gilt nur das
  Auto-Level / 100. Wirkung:
  - Grundwert von Push, Push-Schwankung (5.) und Genome.
  - Kurven-Zielspeed: − (1 − Push) × 80 km/h.
  - Gas-Obergrenze: `1 − (0,9 − Level) × 2` unter 0,9.
  - Bremsverzögerung mit Vorausschau × Push.
- **Aggression** `ai+0x1c` = clamp(`AI_AGGRESSION` / 100, 0..1) × 0,95. Log-Ausgabe: `AI AGGRESSION: %f`. Wirkt nur beim
  Überholen (6.):
  - Die KI darf eine Seite auch dann nehmen, wenn die Engstelle zu nah ist. Bedingung: v / Kurven-Zielspeed < Aggression,
    sie hat also Reserve.
  - Sie darf nicht rutschen: Schlupf unter 0,8 + 0,2 × Aggression.
  - Mit Aggression 0 überholt sie nur, wenn sie den Gegner sicher vor der nächsten Engstelle einholt.
- Pseudocode stammt von Ghidra und ist kein Originalquelltext. Feldnamen sind aus der Verwendung erschlossen.

## Was die AC-KI so schnell macht

### 1. Unsichtbare Physik-Hilfen (`stepSuperhuman`, läuft in jedem Physikschritt)
Über die vtable von `RigidBodyODE` bestätigt: `+0x28 = getMass`, `+0xd8 = addLocalForce`. Kunos nennt die beiden Kräfte
„Magic“: Im Debug-Fenster `FormAI` gibt es den Schalter „Magic: ON/OFF“. Er schreibt `ai+0x134` (per `dynamic_cast` auf
`AIDriver`), Standard ON. Ist er aus, entfallen Linien-Feder und Bremshilfe. TC, ABS und „kein Abriss“ bleiben.
- **Feder zur Linie:** seitliche Kraft = Masse × clamp(Abstand zur Ziel-Linie, ±2 m) × 1 (bzw. 2, wenn ein Auto nebenan ist).
  Das sind bis zu 2 bzw. 4 m/s² zurück zur Linie. Bedingungen: schneller als 20 km/h, Abstand unter 4 m (6 m), nicht in der
  Boxengasse, mehr als 1 s seit dem letzten Kontakt. `car+0x3508` ist die Physikzeit, die `Car::onCollisionCallBack`
  (`0x140274738`) bei jeder Kollision schreibt. Auf der Linie selbst wirkt keine Kraft. Es ist also kein Zusatzgrip, aber die KI
  kann Fehler in der Linie nicht „bezahlen“.
- **Bremshilfe:** Liegt die KI weniger als 50 m vor ihrem Brems-Zielpunkt und ist zu schnell, wirkt eine Längskraft von
  −5 × Masse × clamp(0,3 × Übergeschwindigkeit, 0..1), also bis zu 5 m/s² **zusätzliche Verzögerung**. Das Vorzeichen ist
  geprüft: Im Auto-Koordinatensystem zeigt +z nach vorn (z. B. `cameras.ini` des Abarth, Haubenkamera FORWARD z = +0,96),
  −z ist also nach hinten.
- **Traktionskontrolle:** Drehen die angetriebenen Räder (Antriebsart aus `car+0x890`) stärker durch als die Schwelle
  `ai+0x354`, wird das Gas auf 0,2 × Push gekappt. Die Schwelle kommt aus dem Grip- und Übersteuer-Modell des Autos,
  bei einer bestimmten Getriebeart × 1,3.
- **Perfektes ABS pro Rad:** Die Reifen liegen ab `car+0xb28` mit Abstand `0x858`. `stepSuperhuman` setzt `Reifen+0x41c` auf 0,
  wenn das Rad blockiert oder durchdreht (Schlupf > 1) und mehr als 5 % der Last trägt. Das Feld heißt laut Debug-Ausgabe in
  `Tyre::step` (`0x140283800`) `absOverride`. Das Bremsmoment ist `brakeTorque × absOverride`, das blockierende Rad wird also
  komplett gelöst. Ausnahme: während eines Humanizer-Fehlers (dann darf die KI blockieren).
- **UltraGrip: 20 % mehr Grip, und „kein Abriss“:**
  - `ai+0x144` setzt der Konstruktor per `setUltraGrip(1.2)`. Das Argument steht im Register xmm1, Konstante bei
    `0x1404ae7ec` = 1,2. Ghidra zeigt den Float-Parameter nicht an, deshalb stand hier vorher fälschlich 1,0.
  - CSP bestätigt den Wert: In der Lua-API heißt es zu `physics.setExtraAIGrip` „Changes extra AI grip (120% by default)“.
  - `stepGasBrake` schreibt den Wert in `Reifen+0x538`. In `Tyre::addTyreForces` (`0x14027e5e9`) ist das der Faktor auf μ.
  - **μ × 1,2** gilt beim Bremsen, beim Rollen, mit Auto nebenan und im Begrenzer, also bei Anbremsen, Einlenken und
    Zweikampf.
  - Beim Gasgeben ohne Auto nebenan und unter dem Begrenzer steht dort **1,001**. Das sind 0,1 % mehr μ, aber trotzdem gilt
    das „kein Abriss“-Flag (siehe unten).
  - Die KI **plant** mit normalem μ (Kurvenformel in 2.) und **hat** 120 %. Diese 20 % Reserve sind der wichtigste Grund,
    warum sie selten Fehler macht und hart, aber sicher bremst.
  - Ab μ-Faktor > 1 setzt `addTyreForcesV10` (Reifenmodell ab Version 10) ein Flag im Modell-Input. `SCTM::solve`
    (`0x14044bc20`) setzt dann `FALLOFF_LEVEL` auf 1,0. Laut `SCTM::getPureFY` (`0x14044ba70`) verliert der Reifen über dem
    Spitzenschlupf deshalb **keinen Grip mehr**.
  - Am Kurvenausgang unter Gas kann die KI also nicht abreißen oder übersteuern.
  - Ältere Reifenmodelle, Version 5–9: `BrushSlipProvider::getSlipForce` (`0x1402b3190`) nutzt die Falloff-Stufe
    (`+0x24`) nur, solange der μ-Faktor ≤ 1 ist. Darüber gilt ebenfalls 1,0, also derselbe Effekt. Unter Version 5 gibt es
    keinen Falloff.

**Auf `main` nicht übertragbar** (kinematische Bots). Im Physik-Fork `bots-physics` sind diese Hilfen nachgebaut (`RaceWorld.Kunos.cs`). Wichtig ist das
aber für Vergleiche: Die Rundenzeit der AC-KI ist keine faire Messlatte für „menschliche“ Pace.

### 2. Kurvengeschwindigkeit aus echter Reifenphysik
- `v = sqrt( r·g · (μ + tan θ) / (1 − μ·tan θ) )` (`0x1402cb0f0`). r ist der Radius aus 3 Spline-Punkten (±15 m, mit dem
  aktuellen Seitenversatz) × `HINT_` aus `ai_hints.ini`.
- θ ist die Querneigung (Camber). Wichtig: AC liest sie nicht einfach aus der Datei, sondern **misst sie bei jedem Laden neu**.
  `AISplineRecorder::load` (`0x1402952c0`) ruft `AISpline::calculateNormals` (`0x1402a5e10`) auf:
  - Raycasts 0,6 m links und rechts der Linie.
  - θ = 0,7·atan(Δh außen / 0,6) + 0,3·atan(Δh innen / 0,6).
  - Vorzeichen relativ zur Kurvenrichtung: **positiv = Fahrbahn steigt nach außen an**, also zur Kurve hin geneigt.
  - Die gespeicherte Datei enthält dieselben Werte (beim Speichern wird ebenfalls neu gemessen). Im Feld „direction“ der Datei
    (±1) steht die Kurvenrichtung.
  - Nordschleife: in Kurven (r < 120 m) ist θ in 1860 von 1865 Punkten > 0.
- Bei derselben Messung wird die **Reibung der Oberfläche** unter der Linie gespeichert (`FRICTION` der Surface, Mittel aus
  links und rechts, Payload `+0x40`). Sie multipliziert μ.
- μ (`0x14027a510`) kommt aus dem Reifenmodell: Lastempfindlichkeit, Abtrieb, Gewichtsverlagerung über
  Schwerpunkthöhe und Spurweite, `[PHYSICS_HINTS] AERO_HINT` aus `ai.ini`.
- Zielspeed = v_Kurve − (1 − Push) × 22,2 m/s. Bei Push 0,9 sind das also **8 km/h weniger in jeder Kurve, als absoluter Wert**,
  nicht als Prozent. Danach Minimum mit `MAXSPEED_`. Beim Vorausschau-Bremsen gilt mindestens 50 % von v_Kurve.
- Steht die KI außen neben der Linie: Ziel → v / (1 + 0,055 × Abstand). Untersteuern (Schlupf vorne > hinten, über 30 km/h):
  Ziel × (1 − 0,2 × Differenz × `[UNDERSTEER] HINT`).

- Speicherlayout eines Spline-Punkts (0x54 Byte, aus `AISpline::loadVersion7` `0x1402a8b80`, Dateiwerte in Klammern):
  - `+0x00` Speed, `+0x04` Radius, `+0x08`/`+0x0c` Breite links/rechts, `+0x10` Camber (neu gemessen), `+0x14` Kurvenrichtung,
    `+0x18` Normale, `+0x24` Vorwärtsrichtung (aus den Punkten neu berechnet), `+0x30` Länge, `+0x34` Gas, `+0x38` Bremse,
    `+0x3c` Steigung (geht als `grade·g` in die Bremsrechnung), `+0x40` Oberflächen-Reibung (Standard 1).
  - `+0x44` ist ein Faktor für das „Schlängeln“ (4.), `+0x48` unbekannt, `+0x4c` Flag „Tempolimit 80 km/h“. Diese drei
    schreibt `acs.exe` nirgends. Sie bleiben 0 und wirken in Vanilla AC also nicht. Vermutlich Haken für Erweiterungen:
    `fast_lane_payloads.bin` in manchen Strecken stammt von CSP und wird von `acs.exe` nicht gelesen.

### 3. Bremsen mit Vorausschau (`getLABraking`)
Die KI läuft in 4-m-Schritten bis zu 300 m voraus. Für jeden Punkt rechnet sie die Kurvengeschwindigkeit aus (s. o.) und
integriert die eigene Geschwindigkeit nach vorne: `v -= 4/v · max(0, push·Verzögerung + Luftwiderstand)`. Die Verzögerung
enthält Bremskraft und Reibkreis, wird mit `BRAKEHINT_` × `[PEDALS] BRAKE_HINT` multipliziert und mit Push skaliert.
Langsamere Autos voraus kommen als zusätzliche Zielpunkte dazu, nämlich deren Tempo 7–8 m dahinter. Bremsdruck =
(v − v_Ziel) × 0,5, also proportional. Das entspricht im Kern `RaceWorld.LineSpeedLimit`. Bei uns gibt es nichts Neues außer
den Punkten unter 6.

### 4. Gas, Lenkung, Takt
- Gas: P-Regler 0,6 × (v_Ziel − v) / Beschleunigung plus Vorsteuerung. Ziel höchstens v + 15 m/s. Bremst die KI, gilt
  Gas − 4 × Bremse. Gas-Obergrenze: 1 bei Level ≥ 0,9, sonst `1 − (0,9 − Level) × 2` (Level 0,8 ergibt 0,8).
- Lenkung (`getSteerAtDistance` `0x14029de70`): „Pure Pursuit“. Der Zielpunkt liegt auf dem Spline im Abstand
  3 m + clamp(v × 0,018) × `[LOOKAHEAD] BASE` (Standard 12, im Boxenmodus 4), mit Versatz = Ziel-Versatz + „Schlängeln“.
  Soll-Lenkung = asin(seitlicher Anteil der Richtung zum Zielpunkt) × 7, dann PID mit `[STEER] STEER_GAIN` × 0,27.
  Die Änderung ist auf 0,4 pro Schritt begrenzt, der Lenkwinkel auf Schräglaufwinkel vorne ±10° (über 30 km/h).
- „Schlängeln“ (`stepSinOffset`): sin(t) × (Breite links + rechts) × 0,1 × clamp(v·0,2 − 1) × Payload `+0x44`/300. Weil
  `+0x44` in Vanilla 0 ist, fährt die KI genau auf der Linie.
- Kurven-Erkennung: Eine „Kurve“ ist ein Punkt mit Radius × HINT ≤ 250 m (`getDistanceToNextCorner`). Die Distanz dorthin
  steuert DRS und KERS (8.).
- Entscheidungen (Lenken, Gas, Bremse, Gänge, Turbo, DRS, KERS) laufen nur in jedem 5. Physikschritt mit dt × 5, versetzt
  über `index % 5`. Die Hilfen aus 1. laufen in jedem Schritt.

### 5. „Menschliches“ (nur Gegner, nicht Auto 0)
- **Genome:** 20 Zufallsfaktoren 0,98–1,02 pro Auto und Sitzung, einer je 5 % der Runde. Sie multiplizieren den Push, also
  Kurven- und Bremsleistung. Jedes Auto ist damit in anderen Streckenteilen etwas stärker. Log-Ausgabe: `AI GENOME:`.
- **Push-Schwankung:** Alle 30–600 s neues Ziel = Level ± 0,04, linear überblendet.
- **Humanizer:** alle 20–120 s ein Fehler von 0–2 s. In der Zeit: Bremsdruck × 2 (Blockieren oder Überbremsen), die
  ABS aus 1. ist aus. Ein Zuviel-Gas-Fehler (Gas × 1–1,5 solange `ai+0x9c` > 0) ist vorbereitet, aber **tot**. In der
  ganzen exe gibt es nur zwei Schreibzugriffe auf `+0x9c` einer AIDriver-Instanz, beide in `stepHumanizer`: auf 0 setzen und
  herunterzählen. Die übrigen Treffer gehören zu fremden Klassen (Bremslichter, Tastatur).
- Die Timer stellt `onNewSession` zu jeder Sitzung zurück. Nächster Fehler = jetzt + 20 s + Zufall × 100 s.
- Weitere Zufallswerte im Konstruktor: Herunterschalt-Drehzahl ±7,5 %. `ai+0x204` = 0,95–0,97 ist die Reifen-Schwelle für
  den Boxenstopp: `updateTyres` rechnet daraus `tyreStopMinValueVKM` aus der Verschleißkurve der Mischung, also Stopp bei
  95–97 % Restgrip.
- Mit Autos nebenan sinkt der Push leicht: × 0,9 bei Autos auf beiden Seiten, × (1 − 0,1·(n−1)) bei n nebeneinander,
  × (1 − 0,001·n) bei n Autos in der Nähe. `DANGER_` aus `ai_hints.ini` senkt den Push um bis zu 20 %, wenn die KI abseits
  der Linie oder nebeneinander fährt.

### 6. Zweikampf (`stepCarsAwareness2`, `getEngagedDesiredOffset`)
- Pro Auto: Position im eigenen Koordinatensystem, Winkel, Abstand entlang des Splines vor und zurück, Annäherung.
- „Nebeneinander“ heißt: seitlich unter 8 m, längs unter 15 m, Winkel zum Auto über 25°. Dann hält die KI einen seitlichen
  Abstand von (1 + 0,03 × Annäherung) × 3 halben Breiten (≈ 1,5 Autobreiten von Mitte zu Mitte). Bei Autos auf beiden Seiten
  fährt sie in die Mitte.
- Auto voraus innerhalb `(v·0,07 + 8) · (1 + 0,05·n)` m und seitlich unter 8 m: Die KI wählt die Seite mit mehr freiem Platz
  neben dem Gegner. Ziel-Versatz = Versatz des Gegners ± 3,75 halbe Breiten. Diese Seite nimmt sie nur, wenn:
  - genug Platz da ist: mehr als (1 + 0,2·n) × 2,5 halbe Breiten,
  - die Zeit für den Seitenwechsel (Abstand / 3 m/s, beim Nebeneinander 4 m/s) kürzer ist als die Zeit bis zum Aufprall,
    TTI = (Lücke − 6 m) / Annäherung,
  - die **Einhol-Strecke** = 0,8 × v × (Lücke − 3 m) / Annäherung kürzer ist als der Weg bis zur nächsten Engstelle auf dieser
    Seite. Engstelle heißt: dort ist die Breite kleiner als 2 halbe Breiten, gesucht in 10-m-Schritten. Ausnahme: die Engstelle
    ist über 200 m entfernt, oder die Aggressions-Regel aus 0. greift.

  Sonst folgt sie mit dessen Tempo 7–8 m dahinter und nimmt etwas Gas weg.
- Der Seitenversatz ändert sich beim Ausweichen und Überholen mit höchstens 3–4 m/s, zurück zur Linie sehr langsam
  (0,002–0,01 m pro Schritt). Begrenzt wird er auf Streckenbreite minus halbe Breite.
- Fazit: Das Überholen ist **nicht** cleverer als bei uns. Die AC-KI gilt beim Überholen als schwach. Ihre Stärke kommt aus
  1.–4. Die indirekte Festlegung (begrenzte Seitengeschwindigkeit, Zeitvergleich) entspricht ungefähr der Seitenwechsel-Sperre,
  die wir schon ohne Gewinn probiert haben (siehe Memory „raceai-overtaking-analysis“).

### 7. Zurück auf die Strecke (`stepGasBrake`)
Neben der Strecke (über 2 halbe Breiten außerhalb): Ziel ≤ 20 + 10 × (Fahrtrichtung · Streckenrichtung) m/s. Bis 12 m vom
Rand: anhalten und warten, solange ein Auto in weniger als 5 s ankommt (`getClosestFollowingCarTTI`). Unter 50 km/h ohne
Benzin bzw. Motor (Feld `car+0x710`): an den Rand fahren, 4 m daneben.

### 8. Rennstart, Technik, Strategie
- **Start** (`stepGasBrakeStart`): Vor Grün hält die KI Bremse 0,85 und regelt die Drehzahl auf `[GEARS] UP`.
  - Reaktionszeit: zufällig 50–400 ms, ausgewürfelt 0,5–1 s vor dem Start.
  - Bis 50 km/h erlaubt die TC mehr Schlupf: Grenze (20 − v)·10 + 4 km/h, Gas auf 0,5 gekappt.
- **Gänge**: hochschalten bei Drehzahl > `UP` (Standard 6000), Vollgas (> 0,99) und Schlupf < 1. Runter bei Gas ≤ 0,98 und
  Drehzahl < `UP` × `DOWN`/100 (± 7,5 % Zufall pro Gegner), ab Gang 3. Mindestens 1 s zwischen zwei Schaltungen.
- **Turbo** (verstellbar, `stepTurbo`): Training 80 %. Quali: Out-Lap 50 %, Quali-Runden 100 %, danach 50 %. Rennen 100 %.
  Unter 10 l Sprit 100 %.
- **DRS**: auf, wenn die nächste Kurve über 200 m entfernt ist. Danach 5 s Sperre.
- **KERS**: an, wenn die nächste Kurve über 100 m entfernt ist und der Gang < höchster − 2. Strategie aus den längsten
  Geraden, bis 50 % der Streckenlänge.
- **Push-to-Pass** (Rennen): ab 50 km/h auf einer Geraden, wenn die verbleibenden Ladungen pro Runde reichen.
- **Endübersetzung**: Hängt die KI im letzten Gang im Begrenzer, wählt sie alle 10 s die nächstlängere aus `setup.ini`.
- **Flügel**: zu Beginn jeder Sitzung zufällig im Bereich ± `[WING_VARIATION]`.
- **Sprit/Boxen**:
  - Rennen: Sprit für Rundenzahl + 1, × 1,2. Reicht der Tank nicht, 2 Stints mit zufälliger Länge (ca. ⅓–⅔ der Distanz).
  - Training/Quali: 0–120 s in der Box warten, dann 3–7 Runden (Turbo-Quali: Runden + 2), Sprit für + 3 Runden × 1,1.
    Das Spielerauto bekommt immer 10 Runden, 5 s.
- **Reifen**: `ai_tyres.ini` gibt `MAX_KM` pro Mischung. In der Quali nimmt die KI die griffigste Mischung, deren `MAX_KM`
  über 2,5 Runden liegt. Stopp bei 95–97 % Restgrip (5.). Bevorzugte Mischung: `tyres.ini [COMPOUND_DEFAULT]`.
- **Boxenausfahrt**: Kommt ein Auto in weniger als 10 s, wartet die KI.
- **Seitenlinien**: Gibt es `data/side_l.csv`/`side_r.csv` und sind sie neuer als `fast_lane.ai`, rechnet AC die
  Streckenbreiten daraus neu und speichert sie.

### 9. Drag-Rennen (`AIDriverDrag`)
Eigene einfache Klasse ohne „Magic“. Die Reaktionszeit ist (bestes Level − eigenes Level) × Zufall × 50 ms. Gas 1, bei
Schlupf über der Grenze 0,5. Die Schaltlogik ist wie oben.

## 10. Custom Shaders Patch (CSP)
CSP steckt in `dwrite.dll` (139 MB, ohne Symbole, Strings teils verschlüsselt). Eine vollständige Analyse wie bei `acs.exe`
ist deshalb nicht realistisch. Ausgewertet wurden:
- die Optionen in `extension/config/new_behaviour.ini`,
- die Standardwerte im Lade-Code (Disassembly, `0x18122bc67` ff.),
- die Lua-API in `extension/internal/lua-sdk/ac_track_script/lib.lua`,
- das eingebettete Plugin `ai_tweaks.luac`.

**Was CSP an der KI ändert** (`[AI_TWEAKS]`, Werte beim Nutzer; Beschreibungen aus der ini):
- `NEW_LATERAL_GRIP_ESTIMATION=1`: neue Schätzung des Quergrips. Ersetzt also das μ aus 2. (sonst Probleme bei Dreirädern).
- `FIX_GRIP_MULT=1`: „Updated AI assist (assist-to-AIs)“, ändert die UltraGrip-Hilfe aus 1., damit Autos nicht umkippen.
- `REDUCE_STABILIZATION=0` (aus): würde die Stabilisierung, also die „Magic“-Kräfte, verringern. Laut Beschreibung macht das
  die KI lebendiger, aber **langsamer** und anfälliger für Dreher. Das bestätigt, wie viel Tempo aus den Hilfen kommt.
- `FIX_LOW_LEVEL=1`: Schaltfehler bei Level < 80 % behoben.
- `TRAJECTORIES_FOR_DUMMIES=1`: KIs unter Level 90 % fahren auf einer einfacheren Linie weiter weg von der Ideallinie.
- `CAUTIOUS_STEERING=1`: vorsichtiger lenken in engen Kurven.
- `REMOVE_STEER_LOOKAHEAD_SPEED_LIMIT=1`: besseres Lenken über 200 km/h. Kunos begrenzt den Lookahead über clamp(v·0,018).
- `ACT_IMPATIENT=1`: ungeduldig, wenn blockiert. Details nicht lesbar (verschlüsselte Strings).
- `START_REDUCE_REDLINING`, `START_STRAIGHTEN_TRAJECTORY`: Start ohne Begrenzer-Drehzahl, gerade losfahren.
- `FIX_UPSHIFT_THRESHOLD=1`, `ADJUST_SHIFTING_FOR_SLOW=1`: Schaltpunkte korrigiert bzw. an langsame Abschnitte angepasst.
- `NEW_AI_SMOOTHING=1`: glättet nur Anzeige und Sound der Eingaben, nicht das Fahren.
- `[RAIN] AVOID_SIDES=1`: bei Nässe weiter zur Streckenmitte, je nach Nässe. `AVOID_PUDDLES` (experimentell, aus).

**Versteckte Optionen, Standardwerte aus dem Lade-Code:**
- `CLASSIC_*`: CSP hat die Kunos-Bremsplanung nachgebaut und parametrisiert. Die Standardwerte decken sich genau mit 2./3.:
  `CLASSIC_STEP=4` m, `CLASSIC_HORIZON=300` m, `CLASSIC_RADIUS_SPAN=15` m, `CLASSIC_PUSH_PENALTY=80` (km/h),
  `CLASSIC_DEMAND_GAIN=0.5`, `CLASSIC_MAGIC_BRAKE=1` (die Bremshilfe aus 1.), `CLASSIC_RETARGET=0`. Das ist eine unabhängige
  Bestätigung unserer Analyse.
- `SPEED_PROFILE=0` (aus): ein neuer Geschwindigkeitsplaner von CSP. Parameter:
  - `WINDOW=600` m, `CURVATURE_SPAN=20` m, `CURVATURE_METHOD=1`, `CURVATURE_SMOOTHING=3`,
  - `LOOKAHEAD_TIME=0.25` s, `BRAKE_GAIN=0.8`, `TRAIL_LIMIT=1`, `LAT_GRIP_MULT=1.03`,
  - `MARGIN`, `BRAKE_MODEL`, `PUSH_MODE`, `TARGET_MODE`, `GAS_LOOKAHEAD_TIME`, `BRAKE_GRIP_MULT`, `MAGIC_BRAKE`.

  Auch hier wird also mit 3 % mehr Quergrip geplant.

**Rennfunktionen in CSP** (beim Nutzer aus):
- `[AI_RACE_RUBBERBANDING]`: Level-Offset bis −3 % für Autos vor dem Spieler und +10 % dahinter. Wirkt voll nach 60 s
  und ab 200 m Abstand.
- `[AI_RACE_ADAPTATION]`: Varianz, die sich über die Zeit ändert und die Zeiten verbessern soll, experimentell.
- `[AI_RACE_COOLDOWN]`: Auslaufrunde mit Gas ≤ 0,2 und ≤ 120 km/h.
- `[AI_RACE_RETIREMENT]`: bei Schaden in die Box, ggf. teleportieren.
- `[AI_FLOOD]`: Verkehr im Track-Day-Modus. Anschubkraft 100 kN bis 80 km/h, „Shuffle“ begrenzt Gas, Tempo (80–160 km/h)
  und Folgeabstand zufällig.

**Eingebettetes Plugin `ai_tweaks.luac`:** Hat die Strecke `ai/ext_alt_fast_lane_*.ai` und erlaubt eigene Splines
(`customAISplinesAllowed`), bekommt jede KI zufällig eine dieser Alternativ-Linien (`physics.setAISpline`). Auf den installierten
Strecken gibt es keine solchen Dateien.

**Lua-API** (Steuerung pro Auto, zeigt die internen Stellschrauben):
- Fahrverhalten: `setAILevel` (0–2), `setAIAggression` (Launcher-Wert × 0,95), `setAICaution` (0–16, Abstand zum Vordermann),
  `setAILookaheadBase`, `setAILookaheadGasBrake`, `setAIBrakeHint`, `setAIAeroHint`, `setAITyresHint`, `setExtraAIGrip`
  (Standard 1,2), `setTyresAIMultiplier` (der Reifenfaktor aus 1.).
- Linie und Tempo: `setAISplineOffset`/`AbsoluteOffset`, `setAIThrottleLimit`, `setAITopSpeed`, `setAISteerMultiplier`,
  `setAIStopCounter` (Bremsen nach Unfall).
- Fehler: `setAIBrakeMistake(time)` und `setAITractionMistake(time, severity)`. Das ist der in Vanilla tote Zuviel-Gas-Fehler
  (`ai+0x9c`/`+0xb0`), den CSP von außen auslösen kann.
- Sonstiges: Boxenstopps, Reifenwahl, Regenreifen.

**Für BotDriver interessant:**
- `TRAJECTORIES_FOR_DUMMIES`: Schwache Fahrer bekommen eine andere, einfachere Linie statt nur langsamer zu sein. Passt zu
  unserem `PersonalLineOffset`/`LineScale`.
- `AVOID_SIDES` bei Regen: Linie zur Mitte, abhängig von der Nässe. Prüfen, ob `RainCaution` das schon tut.
- Rubberbanding: vom Nutzer nicht gewünscht (kein künstliches Vorbeilassen), nur der Vollständigkeit halber.
- `setAICaution`: Kunos und CSP haben einen eigenen Faktor für den Folgeabstand, ähnlich unserem `followGap`.

## Was BotDriver schon genauso macht
- `HINT_` (als Faktor auf den Radius, bei uns `vLim·sqrt(HintFactor)`, also gleich) und `MAXSPEED_`. Siehe
  `Shared/Track/TrackInfo.cs:42`.
- Bremsen mit Vorausschau (`LineSpeedLimit`), Grip mit Abtrieb (`CarSpec.CornerLimit`), Schaltpunkte aus `ai.ini`,
  Pace-Rauschen über die Zeit (`PaceNoise`, 20–40 s), Fehler, Wiedereinfädeln nach Drehern.

## Vorschläge, nach erwartetem Nutzen sortiert
Jeden Vorschlag einzeln mit `tools/passtest.sh` (Überholen) und `tools/bench.sh` (Pace, Kontakte) gegen die Baseline messen.
Selftest muss grün bleiben.

1. **Genome pro Abschnitt.** `RaceBot` bekommt 20 Faktoren 0,98–1,02, Mittelwert auf 1 normiert, damit die Kalibrierung stimmt.
   In `LineSpeedLimit` mit `pace` und `brakePace` multiplizieren, Index `(int)(s / Line.Length * 20)`. Erwartung: Gleich starke
   Bots sind abschnittsweise unterschiedlich schnell. Das schafft Überhol-Chancen, ohne dass jemand Platz macht. Passt zum
   offenen Punkt „Bot gegen Bot zu wenig Überholungen“.
2. **Camber in der Kurvenformel.** `RacingLine.Camber` wird gelesen, aber nur für die sichtbare Rollneigung genutzt
   (`RaceWorld.cs:2381`). Kunos rechnet `(μ + tan θ) / (1 − μ tan θ)`.
   - Vorzeichen geklärt: Wert aus der Datei × Richtung ist die absolute Rollneigung (so speichert es `FastLaneFile`). Für
     die Kurvenformel braucht man die Neigung relativ zur Kurve, also den Dateiwert ohne Richtung, bzw.
     `Camber × direction` noch einmal. Positiv heißt: zur Kurve hin geneigt.
   - Größenordnung (μ = 1,35):
     - Nordschleife: Median θ = 0,067 rad, das sind **+7 % Kurvenspeed**, p90 +13 %. Karussell θ = 0,31 rad, rechnerisch
       +49 %, Kunos dämpft mit HINT 0,8.
     - Trial Mountain: Median +2 %, p90 +9–10 %.
   - Achtung: Unsere Bots sind gegen die Aufnahmen kalibriert. Camber verschiebt vor allem die Verteilung zwischen den
     Kurven: gebankte Kurven schneller, flache relativ langsamer. Danach `GripFactor` bzw. die Kalibrierung neu prüfen und mit
     `sim --hotlap --rec` Kurve für Kurve vergleichen.
3. **`BRAKEHINT_` und `DANGER_` einlesen.** 131 bzw. 183 Einträge in den installierten Strecken, bisher ignoriert. `BRAKEHINT`
   multipliziert die Bremsverzögerung im Bereich. `DANGER` LEFT/RIGHT senkt den Push um bis zu 20 % bei Zweikampf oder
   abseits der Linie, z. B. an Mauern. Achtung: Das Parsen liegt in `Shared/Track/TrackInfo.cs`, also außerhalb von
   `BotDriverPlugin/`. Vorher klären, ob die Änderung dort erlaubt ist (siehe CLAUDE.md).
4. **Kurvenabzug absolut statt prozentual prüfen.** Kunos zieht pro Kurve km/h ab. Bei uns skaliert die Pace. Mit den Aufnahmen
   (`sim --hotlap --rec`) vergleichen, ob schwache Spieler in langsamen Kurven relativ mehr verlieren. Nur ändern, wenn die Daten
   das zeigen.
5. **Fehlerrate vergleichen.** Kunos: alle 20–120 s ein Brems-Fehler von bis zu 2 s. Mit `ErrorsFor` und den Fehlern in
   `RaceWorld.Human.cs` vergleichen. Nur zur Einordnung, eine Änderung ist nicht zwingend.

Nicht empfohlen: die Physik-Hilfen aus 1. (gibt es bei kinematischen Bots nicht) und das Überhol-Schema aus 6.
(entspricht bereits gescheiterten Versuchen).

## Offen
Für das Fahrverhalten ist nichts Wesentliches mehr offen. Alle `AIDriver`-Methoden aus der PDB sind gelesen, ebenso die
benötigten Teile von `AISpline`, `AISplineRecorder`, `Tyre`, `SCTM`, `BrushSlipProvider` und `RaceManager`. Nicht untersucht:
- Payload `+0x48`. Wird nie geschrieben, vermutlich unbenutzt.
- Die übrigen Regler im Debug-Fenster `FormAI` (z. B. `TRAIL_HINT`): nur Werkzeuge für Kunos, im normalen Spiel ohne Wirkung.
- CSP-Interna (siehe 10.): Was `NEW_LATERAL_GRIP_ESTIMATION`, `FIX_GRIP_MULT`, `ACT_IMPATIENT` und `SPEED_PROFILE` genau
  rechnen, ist nicht gelesen. `dwrite.dll` hat keine Symbole und verschlüsselt viele Strings. Ein Weg wäre, zur Laufzeit die
  CSP-Lua-API abzufragen (z. B. `ac.getCar(i).aiLevel`) oder per Debugger an den Lade-Code oben anzusetzen.
  `fast_lane_payloads.bin` ist der Payload-Cache von CSP (`CACHE_PAYLOADS=1`).

## 11. Bots mit Fahrphysik nach AC (Stand 12.10.2026, Branch `bots-physics`)
Eigener Branch `bots-physics` (Arbeitsordner `~/Projekte/AssettoServer-AI-Race-physics`); `main` bleibt das kinematische Plugin.
Schalter: `PhysicsDriving` in der Konfiguration (in diesem Branch Standard an) bzw. `sim --physics` / `strength --physics`
(Simulator und Bench ohne `--physics` fahren kinematisch). Ohne Schalter fährt alles
wie bisher (Supra-Hotlap kinematisch unverändert 1:50.3, Selftest grün, Plugin baut, Bench kinematisch im Rauschen gleich).
Ziel: so nah wie möglich an AC. Alles unten ist aus `acs.exe` gelesen (Adressen in Klammern), nicht geschätzt.

### 11.1 Reifen: AC-Reifenmodell 10 (`Core/Physics/Tyre.cs`)
`SCTM::solve` (`0x14044bc20`), `SCTM::getPureFY` (`0x14044ba70`), Kennwerte aus `Tyre::initCompounds` (`0x140280800`):
- D = DY_REF·(Fz/FZ0)^(LS_EXPY−1) (AC rechnet LS_MULT = DY_REF·FZ0^(1−LS_EXPY), `0x14027f770`), ältere Reifen DY0 + DY1·Fz/2000.
- Sturz: D_y / (1 + c·DCAMBER_0 − c²·DCAMBER_1), c negativ, wenn das Rad in seine Seitenkraft kippt. **Achtung:** Der Kommentar in
  tyres.ini („D·(1 − (c·DC0 + c²·DC1))“) stimmt nicht mit dem Code überein. Sturzschub: Schräglauf + sin(Sturz)·CAMBER_GAIN.
- Gleitgeschwindigkeit: beide D / (1 + v_gleit·SPEED_SENSITIVITY). Bremsen: D_x × (1 + BRAKE_DX_MOD).
- Kombinierter Schlupf s = |(tan α, κ)| / (1 + κ) (oder COMBINED_FACTOR-Norm). Spitze wächst linear mit der Last: tan(FRICTION_LIMIT_ANGLE)
  bei FZ0, tan(FRICTION_LIMIT_ANGLE·(1 + FLEX_GAIN)) bei 2·FZ0, × ((Grip − 1)·0,75 + 1). Längs CX_MULT-mal früher.
- Form: 1 − (1 − s/s_peak)³ bis zur Spitze, dann (1 − FALLOFF_LEVEL)/(1 + (s − s_peak)·FALLOFF_SPEED) + FALLOFF_LEVEL (Standard 0,92).
- Einlauflänge (`Tyre::stepRelaxationLength` `0x140284a40`): Faktor v·dt/L, L = 2·RL·(1 + 0,3·(Fz/FZ0 − 1)) bei kleinem Schlupf,
  RL bei voller Spitze, begrenzt 0,04..1, für Schlupf **und** Schräglauf. Unter 1 m/s: κ = 0,5·Gleitgeschw., α = 5,5·v_quer.
- Rollwiderstand (`addTyreForcesV10` `0x14027ed60`): Fz/1000·(RR0 + RR1·v²)·(1 + 0,001·RR_SLIP·Schlupf) — rutschende Reifen bremsen
  bis ~6× stärker. Radius wächst mit RADIUS_ANGULAR_K (mm pro rad/s).
- Annahme: warme Reifen auf PRESSURE_IDEAL und im Optimum der Temperaturkurve (Druck- und Temperaturterme fallen weg).

### 11.2 Fahrwerk (`Core/Physics/Suspension.cs`)
- `Suspension::step` (`0x1402c3390`): Feder = (x·PROGRESSIVE + SPRING_RATE)·x mit x = Weg + ROD_LENGTH (nur Druck), Packer ab
  PACKER_RANGE, Anschläge BUMPSTOP_UP/DN mit BUMP_STOP_RATE. Dämpfer (`0x1402b3280`) langsam/schnell, Druck/Zug mit Schwellen.
- Aufbau mit Hub, Nicken, Wanken; vier Naben (HUB_MASS) auf der Reifenfeder (RATE, DAMP). Radlasten kommen aus der Reifenfeder.
  Schwerpunkt: Nabe auf BASEY unter dem Schwerpunkt (AC `getCGHeight`), statisch ~0,47 m bei der Supra (vorher fälschlich 0,11).
- Lenker-Geometrie in 3D gelöst (Newton, 6 Gleichungen): DWB (zwei Dreieckslenker + Spurstange), STRUT (Federbein-Achse durch den
  Domlager-Punkt + Querlenker + Spurstange). Tabellen über Federweg × Zahnstange: Sturz, Spur (Ackermann, Bump-Steer), und per
  virtueller Arbeit, wie Seiten-/Längskräfte den Aufbau heben (Rollzentrum, Anti-Dive, Anti-Squat, Antrieb über die Nabe).
  STATIC_CAMBER und TOE_OUT (Spurstangenlänge) werden angewendet. AXLE (Starrachse): Rollzentrum auf Nabenhöhe, Sturz bleibt.
- Lenkung (`SteeringSystem::step` `0x1402b81b0`): Zahnstange = LINEAR_STEER_ROD_RATIO × Radwinkel-Befehl in Grad; die echten Radwinkel
  kommen aus der Geometrie. Ist die Zahnstange über dem erreichbaren Weg, bleibt der Winkel stehen (Anschlag).

### 11.3 Antrieb, Aero, Elektronik
- Motor mit Drehzahl-Freiheitsgrad: Kupplung (MAX_TORQUE) rutscht beim Anfahren (Motor auf Startdrehzahl, schließt erst, wenn das
  *Auto* schnell genug ist), nach Schaltvorgängen mit AUTOCLUTCH-Profil (POINT_0/1/2), beim Runterschalten AUTOBLIP (LEVEL).
  Begrenzer schneidet für 1/LIMITER_HZ. Turbos (`Turbo::step` `0x1402ae7c0`): Ziel (rpm·Gas/REFERENCE_RPM)^GAMMA, LAG_UP/LAG_DN pro
  Schritt, Wastegate bzw. ctrl_turbo. Sperrdiffs, AWD/AWD2 wie vorher.
- ABS/TC wie electronics.ini: Regelung im Takt RATE_HZ (ABS pro Rad, TC kappt das Gas für eine Periode, `0x14028f610`/`0x140290200`).
- Flügel einzeln (`Wing::step` `0x1402b2bc0`): CL/CD über Anstellwinkel (ANGLE + Nicken) und Bodenhöhe (LUT_GH_CL/CD), Kraft an der
  Flügelposition (Nickmoment). Widerstand × 0,8 wie bisher (Aufnahmen).
- Fahrbahn: Höhe unter jedem Rad aus den Physik-Meshes (Unebenheiten, Kerbs; > 10 cm = Artefakt, ignoriert), Reibung der Oberfläche
  aus surfaces.ini (Straße 0,96–0,98, Gras usw. über `OffTrack`).

### 11.4 Fahrer: der Kunos-Fahrer (`Core/RaceWorld.Physics.cs`)
- Lenkung (`stepSteer` `0x1402a2630`, `getSteerAtDistance` `0x14029de70`): Zielpunkt 3 m + clamp(v·0,018)·`[LOOKAHEAD] BASE`, Fehler
  7·asin(seitlicher Anteil), PID mit Kp = STEER_GAIN·0,27, Ki 0,02, Kd 0,02 (`0x14044e720`), Ausgabe ±0,9 des Volleinschlags, höchstens
  0,4 pro Entscheidung, Schräglauf vorne ±10° (Faktor 12/STEER_LOCK). Entscheidungen jeden 5. Physikschritt.
- Gas/Bremse (`stepGasBrake` `0x1402a08e0`): Gas = 0,6·(v_ziel − v)/(Antriebskraft/Traktion) + Haltegas, Ziel ≤ v + 15 m/s, Gas − 4×Bremse,
  im Begrenzer 0,5. Bremse bis zum Blockierpedal der ersten Achse × 0,99 (`0x14027c320`); die Bremskurve liefert unsere Racecraft.
  Untersteuer-Regel und Außen-Regel (0,055) wirken **nur aufs Gas** (wie bei Kunos).
- Hilfen (`stepSuperhuman` `0x1402a2a60`): Ultra-Grip = `ai.ini [ULTRA_GRIP] VALUE` (1,2) **nur auf die Seitenkraft** (`0x14027ed60`: nur
  Ausgang 0 wird multipliziert), 1,001 mit Gas allein; „kein Abriss“ immer. Perfektes ABS pro Rad (Schlupf > Spitze → keine Bremse),
  KI-TC (Antriebsräder über der Spitze → 20 % Gas; beim Start bis 50 km/h großzügig, 50 %), Feder zur Linie (m·clamp(Fehler, ±2)·1/2,
  aus 1 s nach Kontakt/Überlappung), Bremshilfe −5·m·clamp(0,3·Überspeed) in 50 m vor dem Bremspunkt.
- Planung: Kurven-μ wie Kunos (`0x14027a510`/`0x14027a260`): reine Reifen-D bei den Radlasten mit Querverlagerung (Schwerpunkt/Spur,
  0,9·μ), Abtrieb, Achsen gegenseitig begrenzt, Kippgrenze. Supra 1,16 g, Z4 GT3 1,59/1,95 g (80/200 km/h) — fast genau die p97-Werte
  aus deinen Aufnahmen. Der Ultra-Grip ist die Reserve.
- Gänge (`stepGears` `0x1402a1710`): hoch über `[GEARS] UP` bei Vollgas (oder im 1.), runter, wenn die Drehzahl im kleineren Gang unter
  UP × DOWN läge; DOWN = ai.ini DOWN/100 erst ab ai.ini VERSION 4, sonst 0,8, ±7,5 % pro Fahrer; ≥ 1 s zwischen Schaltungen.
- Genome: 20 Faktoren 0,98–1,02 je 5 % der Runde auf die Pace; neben einem Auto Pace × 0,9 (Kunos: Push × (1 − 0,1·(n−1))).
- Fehler: „zu spät gebremst“ = Kunos-Humanizer (Bremsdruck × 2, keine ABS-Hilfe), „Slide“ = zu viel Gas (Kunos' vorbereiteter,
  ungenutzter Gasfehler, KI-TC aus). Dreher (stehen, wenden) und Boxengasse bleiben kinematisch.
- Abweichungen von AC: unsere Racecraft (Ziele, Überholen, Strategie) statt Kunos' Zweikampf; Physik-Autos bekommen 0,5 m mehr
  Seitenabstand, weil sie Kurven ~1 m innen schneiden (Kunos-P-Lenkung); keine Reifentemperaturen/-drücke; DANGER_/BRAKEHINT_ (Shared).

### 11.5 Messung
Hotlap Trial Mountain, Stärke 100:

| Auto | Physik (AC-Fahrer) | dein bester Lauf |
|---|---|---|
| Supra | 1:54.6 | 1:56.8 |
| R34 | 2:02.5 | 1:55.6 |
| RX-7 | 1:52.1 | 1:58.0 |
| Z4 GT3 | 1:37.8 | 1:37.6 |
| GT-R GT3 | 1:37.1 | 1:37.0 |

Die AC-KI ist pro Auto unterschiedlich schnell (R34: Plan-μ 1,13 g, du fährst dort mehr). Stärken (Supra, Kalibrierung mit Physik):
95 % → 2:00.6, 90 % → 2:07.3, 85 % → 2:14.8 Ziel; Einzelbots treffen das Ziel (−0,3 bis −1,4 s). Unter ~80 % greift die Pace-Untergrenze.

Bench (`tools/bench.sh "..." --physics`, 20 Bots, je 12/6 Rennen): Kontakte TM 124 (kinematisch 122), NS 26 (14); Überholungen TM 447 (492);
„aufgehalten“ TM +3,6 s (+0,6), NS +29 s (+11): im Verkehr verlieren Physik-Bots mehr als kinematische. Rechenzeit ~1 s CPU pro Runde und Bot.

Werkzeuge: `car --physics` (Geometrie, Ackermann, Planungs-μ, Hüllkurve, Skid-Pad), `physstep` (Einzelschritte), `sim --contacts`
(Kontakt-Protokoll), Überlappungs-Protokoll im Sim-Ende.

### 11.6 Offen
- Verkehr: Physik-Bots verlieren im Pulk mehr Zeit (Folgen, Nebeneinander). Ursache noch nicht eingegrenzt.
- Reifentemperatur und -druck (AC simuliert beides), Verschleiß im Modell, Reifenschäden.
- R34/RX-7: AC-KI-Pace weicht von deiner ab (so ist die AC-KI); bei Bedarf Kalibrierung pro Auto gegen die Aufnahmen.
