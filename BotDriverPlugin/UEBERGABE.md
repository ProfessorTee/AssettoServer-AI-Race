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

## Physik-Fahrer (Stand 2026-10-12, Commit e8dcb0b)

### Wo, wie
- Worktree `~/Projekte/AssettoServer-AI-Race-physics`, Branch `bots-physics` (gepusht). `main` (`~/Projekte/AssettoServer-AI-Race`) bleibt
  das kinematische Plugin; dort arbeitet evtl. eine andere Sitzung (WebPortal/Shared) — nur eigene Dateien committen.
- Nutzer-Regeln: kurz auf Deutsch antworten, Code/Commits Englisch, keine Zwischenmeldungen; Builds/Sims **immer `nice -n 19`**
  (Nutzer spielt AC am selben PC), alle Kerne ok. Nach jedem getesteten Schritt committen + pushen (Co-Authored-By-Zeilen).
  Update-Pakete: neuer Ordner `~/Downloads/Server_Files/Update_<n>_Physik_Test/plugins/BotDriverPlugin/BotDriverPlugin.dll` + `LIESMICH.txt`
  (zuletzt **Update_44**). Bauen: `tools/build.sh` (-> `out-linux-x64/`). Prüfen: `Tool selftest`, `sim ... --poses`, `tools/bench.sh`, `tools/passtest.sh`.
- Echter Server laut Nutzer: Spitze 2 % CPU, 400 MB RAM bei voller Bot-Zahl → Rechenzeit ist kein Thema, Details dürfen mehr kosten.
- AC-Binary-Reverse: `acs.exe` + `acs.pdb` unter `/mnt/GameDrive/SteamLibrary/steamapps/common/assettocorsa`; REA-MCP (Ghidra) zum
  Dekompilieren, Adressen über `llvm-pdbutil dump --publics` (VA = 0x140001000 + Offset), `llvm-objdump -d` für Assembler-Suche.
  Funde stehen in `UEBERGABE_AC_KI.md` (Fahrer) und in den Kommentaren von `Core/Physics/TyreHeat.cs` (Reifen).

### Aufbau
- `Core/Physics/`: `Vehicle.cs` (4-Rad-Modell, 300 Hz), `Tyre.cs` (SCTM v10 + Druck/Thermik-Daten), `TyreHeat.cs` (AC-Wärmemodell 12×3+Kern,
  Druck), `Suspension.cs`, `Envelope.cs` (Messung, `Calibrate` = Grip auf Kunos-Plan), `VehicleData.cs`.
- `Core/RaceWorld.Kunos.cs`: Kunos-Fahrer (PlanSpeed=getLABraking, Awareness=stepCarsAwareness2, Push/Genom, Humanizer, Decide=stepSteer/
  stepGasBrake, Hilfen inkl. Stabilitätshilfe `ExtraMz`), `SampleRoad` (Curbs ≤25 cm, Gras-Höhe ≤30 cm).
- `Core/RaceWorld.cs`: Welt, Start (`GridStart`, `Launch`, `GridRevs`), Posen (`GetPose`, `OnWheels` = 4-Rad-Höhe aus Mesh, geglättet 50 ms),
  Kontakte (`OnContact` → `Impulse`). `RaceWorld.Damage.cs`: `Impulse`, `ContactPoint`, `BotContact`, Schaden. `RaceWorld.Pit.cs`: Box,
  Durchfahrtsstrafe. `RaceWorld.Tyres.cs`: Mischung, `ChoosePressures`, Heizdecken.
- Stärke: `AiStrength` % = KI-Level (`StrengthCalibration.PaceFor`), Kalibrierung nur für Zielrunden.

### Offen (Update 44 noch nicht vom Nutzer im Spiel getestet)
1. **Rauch am Start** — Ursache unklar. Update 43 sendet in der Aufstellung Leerlauf und Raddrehzahl 0, Update 44 nimmt den Kunos-Start-
   Schlupf raus. Falls weiter Rauch: Zeitpunkt klären (vor/nach Ampel), Telemetrie (`/bots_debug on`) und `BotSlot.WriteStatus`
   (TyreAngularSpeed, Gear, Gas) prüfen; evtl. leitet CSP Schlupf aus Gas/Drehzahl ab.
2. **Curbs springen** — jetzt gedämpft (50 ms). Falls weiter unruhig: Federweg aus dem Modell (`st.Travel`, `st.Pitch/Roll`) für die Pose
   nehmen statt Mesh-Höhen; Vorzeichen am Client prüfen.
3. **Reaktion auf Treffer** — jetzt Impulse (1,3 × Schließgeschwindigkeit × reduzierte Masse). Am Client prüfen: zu stark/schwach?
   Spielermasse unbekannt (= Bot-Masse angenommen). `ResolveOverlaps` schiebt Autos weiterhin direkt (Teleport) → ggf. auch als Impuls.
4. Seit Update 44 im Sim mehr Querstellung (>8°: 2,0 % statt 0,8 %), nicht untersucht (Start-TC-Änderung? höheres Level?).
5. Kunos-Fahrer überholt zögerlich (passtest ~20/40); Hinterachse beim harten Anbremsen + Runterschalten nervös.
6. Nicht nachgebaut: Druck auf Federrate/Flanke (Nutzer unsicher, ob sinnvoll), Graining/Blasen.
