# BotDriverPlugin (Physik-Fork `bots-physics`): Übergabe

Fokus: das Grundgerüst (Server-Anbindung) und der aus AC nachgebaute Physik-Fahrer. Die Geschichte des kinematischen Plugins
steht auf `main` und ist hier bewusst weggelassen.

## Grundgerüst
- **Core-Patch im AssettoServer-Kern:** `IExternalAiController` (ein Plugin steuert einen KI-Slot, die Traffic-KI lässt ihn in Ruhe),
  `SessionManager.OnAiLapCompleted` (Bot-Runden zählen offiziell), Rennen mit einem Spieler + Bots werden nicht übersprungen.
  Dateien: `EntryCar`, `EntryCarAi`, `AiBehavior`, `SessionManager`, `ACTcpClient`.
- **Plugin-Verträge:** `AssettoServer/Server/Extensions/PluginContracts.cs` (`ISharedSettings`, `IPlayerRating`, `IDrivenCars`,
  `IAdminWebAccess`). Plugins kennen sich nicht, jedes läuft allein (eigene AssemblyLoadContexts). Gemeinsamer Quellcode in `Shared/`
  (Track: `fast_lane.ai`, `TrackMap`; Config: `ConfigWriter`) — Änderungen dort mit allen Plugins bauen.
- **Plugin-Hülle (`BotDriverPlugin/`):**
  - `BotDriverModule` (DI), `BotDriverSlotFilter` (Bot-Slots für Spieler sperren/freigeben), `BotDriverService` (Tick: Welt vorrücken,
    Posen senden, Wetter/Grip, Start-Ampel → `GreenAt`/`StartRule`), `.Live` (`IDrivenCars`), `.Telemetry`, `.Dashboard`, `.Admin`.
  - `BotRace` (gemeinsamer Zustand: Welt, Strecke, Slots, Session, Lock), `GridDirector` (Sessions, Startaufstellung, Boxen, Session-Ende),
    `FieldStrength` (Stärke → KI-Level, Kalibrierungs-Cache `cache/raceai/`, Schalter), `BotSlot` (Pose → `CarStatus`: Position,
    Rotation, Geschwindigkeit, Raddrehzahl, Gang, Drehzahl, Lichter), `TrackData` (Linie, Grid, Boxengasse, Strecken-Meshes),
    `BotDriverCommandModule` (`/bots…`), `BotsController` (`/api/bots/*`), `BotDriverConfiguration` (alte Schlüssel = `object?`-Stubs,
    der YAML-Lader duldet keine unbekannten).
- **Core (`Core/`, ohne Server-Abhängigkeit):** `RaceWorld` (Welt, Bots, Start, Posen, Kontakte), `CarDataLoader`/`AcdReader`/`CarSpec`
  (Autodaten aus `data.acd` wie Content Manager), `RoadSurface` (Fahrbahn- und Gras-Meshes), `StrengthCalibration`, `LapEstimator`.
- **Tool (`Tool/`):** `selftest`, `sim` (Rennen/Hotlap offline, `--poses`, `--trace`, `--contacts`), `car`, `physstep`, `strength`, `grid`, `height`.

## Physik-Fahrer (Stand 2026-10-23, Update_47)

### Wo, wie
- Worktree `~/Projekte/AssettoServer-AI-Race-physics`, Branch `bots-physics` (gepusht). `main` (`~/Projekte/AssettoServer-AI-Race`) bleibt
  das kinematische Plugin; dort arbeitet evtl. eine andere Sitzung (WebPortal/Shared) — nur eigene Dateien committen.
- Nutzer-Regeln: kurz auf Deutsch antworten, Code/Commits Englisch, keine Zwischenmeldungen; Builds/Sims **immer `nice -n 19`**
  (Nutzer spielt AC am selben PC), alle Kerne ok. Nach jedem getesteten Schritt committen + pushen (Co-Authored-By-Zeilen).
  Update-Pakete: neuer Ordner `~/Downloads/Server_Files/Update_<n>_Physik_Test/plugins/BotDriverPlugin/BotDriverPlugin.dll` + `LIESMICH.txt`
  (zuletzt **Update_48**). Bauen: `tools/build.sh` (-> `out-linux-x64/`). Prüfen: `Tool selftest`, `sim ... --poses`, `tools/bench.sh`, `tools/passtest.sh`.
- Echter Server laut Nutzer: Spitze 2 % CPU, 400 MB RAM bei voller Bot-Zahl → Rechenzeit ist kein Thema, Details dürfen mehr kosten.
- AC-Binary-Reverse: `acs.exe` + `acs.pdb` unter `/mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa`; REA-MCP (Ghidra) zum
  Dekompilieren, Adressen über `llvm-pdbutil dump --publics` (VA = 0x140001000 + Offset), `llvm-objdump -d` für Assembler-Suche.
  Funde stehen in `UEBERGABE_AC_KI.md` (Fahrer) und in den Kommentaren von `Core/Physics/TyreHeat.cs` (Reifen).

### Aufbau
- `Core/Physics/`: `Vehicle.cs` (4-Rad-Modell, 300 Hz), `Tyre.cs` (SCTM v10 + Druck/Thermik-Daten), `TyreHeat.cs` (AC-Wärmemodell 12×3+Kern,
  Druck), `Suspension.cs`, `Envelope.cs` (Messung, `Calibrate` = Grip auf Kunos-Plan), `VehicleData.cs`.
- `Core/RaceWorld.Kunos.cs`: Kunos-Fahrer (PlanSpeed=getLABraking, Awareness=stepCarsAwareness2, Push/Genom, Humanizer, Decide=stepSteer/
  stepGasBrake, Hilfen inkl. Stabilitätshilfe `ExtraMz`), `SampleRoad` (Curbs ≤25 cm, Gras-Höhe ≤30 cm).
- `Core/RaceWorld.cs`: Welt, Start (`GridStart`, `Launch`, `GridRevs`), Posen (`GetPose`: Höhe/Nicken/Wanken aus dem Modell `st.H/Pitch/Roll`
  über der Fahrbahnebene, `RollSign` −1 per Client-Emulation geprüft; `GearAndRpm` sendet N solange die Kupplung beim Anfahren schleift),
  Kontakte (`ResolveContacts`/`Touch`: gedrehte Umrisse, SAT, Stoß e=0,3 + Reibung 0,3 am Mittelpunkt der Überlappfläche, Herausschieben
  ≤5 cm/Schritt; Spieler = gleich schwer, nur der Bot reagiert, `ExternalCar.Bounce` 0,2 s; `WallHit` am Auslaufrand; `OnContact` vom Client
  nur, wenn der Server die Berührung nicht selbst sah). `RaceWorld.Damage.cs`: `ContactEvent` (zählt ab 1,1 m/s), `Zone`, Schaden.
  `RaceWorld.Pit.cs`: Box, Durchfahrtsstrafe. `RaceWorld.Tyres.cs`: Mischung, `ChoosePressures`, Heizdecken.
- AC-Client und Fremdautos (acs.exe `NetCarStateProvider::step` 0x14011bd00, `getSmoothBodyMatrix` 0x140119310, `initPhysicsValues`
  0x140119fe0): Körperhöhe = Raycast-Boden unter der Mitte + (gesendetes Y − Boden unter gesendeter Position); Räder an (±TRACK/2, BASEY,
  (1−CG)·WB / −CG·WB), Raycast je Rad, Kontakt nur ±0,1 m um BASEY − Radius. `sim --poses` emuliert das ("client wheels").
- Stärke: `AiStrength` % = KI-Level (`StrengthCalibration.PaceFor`), Kalibrierung nur für Zielrunden.

### Update 48 (2026-10-23, nach Nutzer-Test von 47: Rutschen in Kurven, Boxengasse seitlich/durch Wände, stuck an der Wand)
- Stabilitätshilfe `ExtraMz` zielt jetzt auf Pfad-Gierrate + `StabilityBetaGain`·β (vorher hielt sie einen Drift bei Pfad-Gierrate fest;
  TM Kurve 2250: −11° mit Gegenlenken jede Runde). Sim TM: Körper gegen Fahrtrichtung >8° 3,6 % → 0,3 %.
- Boxengasse: seitlicher Versatz in die Box nur so schnell wie mit Lenken möglich (≤ ~15°, `PitLateralSpeed`), Pose dreht mit (vorher bis 20°
  quer geschoben); Höhe = Boden unter dem Auto + CgHeight statt Linienhöhe. `sim --poses` zeigt „pit lane:“.
- Fahrzeugmodell: Aufbau auf −0,25/+0,6 m um die Ruhehöhe und ±8° Nicken/Wanken begrenzt (`Hold`); quer im Knäuel mit Vollgas pumpte er sich auf
  (H 500 m, Last 10⁷ N, dann NaN → Absturz). NaN-Schutz in `Drive` (Modell neu ab Stand). NS-GT3-Sim: Kontakte etwa halbiert.
- `WatchStuck`: quer stehend (>34°) oder an der Wand gehalten, nach `RecoverAfter` = 4 s mit Gaswunsch: dreht mit ~60°/s auf die Fahrbahnrichtung
  und rückt von der Wand weg (Geist, wenn `GhostAfter` > 0). Sim meldet „stuck …“ (5 s gestanden).

### Offen (Update 47 noch nicht vom Nutzer im Spiel getestet)
Update 47: Einsinken (gesendete Höhe war die KI-Linie, 12–20 cm zu tief; Sim: Räder außerhalb Federweg 71–100 % → 2–5 %), Bots hinter
stehenden/langsamen Spielern (Awareness: `freeSpeed` statt Momentan-Differenz, früh ausscheren, Abstand nach Zeit bis seitlich frei; nächstes
Auto in eigener Spur zuerst; gedrehte Umrisse), Kontakte physikalisch statt Verschieben. Bench: held TM 1,28 → 0,78 s, NS 18,1 → 16,3 s,
Kontakte sichtbar mehr (vorher still weggeschoben), passtest etwa gleich. Selftest: `PlayerTest`, `ContactTest`.
1. **Kontakte im Spiel ansehen** — mehr Berührungen beim Überholen (Seit-an-Seit 1–3 m/s), ab und zu dreht ein Heck-Treffer ein Auto;
   TM ab und zu Ghost (stehend >20 s). Falls zu viel: Restitution/Reibung (`ContactRestitution`), Kunos-Seitenabstand beim Überholen.
2. **Rauch am Start** — falls weiter: Zeitpunkt klären (Aufstellung oder Anfahren), Telemetrie (`/bots_debug on`).
3. Fahrzeugmodell im Zweikampf: ndSlip vorne bis 1,6, Gieren ±5° (NS), Querstellung >8° ~2 %, nicht untersucht.
4. Kunos-Fahrer überholt zögerlich (passtest ~10/40); Hinterachse beim harten Anbremsen + Runterschalten nervös.
5. Nicht nachgebaut: Druck auf Federrate/Flanke (Nutzer unsicher, ob sinnvoll), Graining/Blasen; KI setzt nie zurück (AC auch nicht).
