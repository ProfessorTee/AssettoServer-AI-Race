# Sitzung: ServerToolsPlugin
Rotation (`TrackRotation.cs`, rotation.yml), Klassen (`Shared/Presets`), Statistik/Safety Rating (`PlayerStats.cs`), Echtwetter,
Rennergebnis, Servername, Neustart; Skripte in `scripts/`, Klassen-Presets in `presets/classes/`.
- Nur in `ServerToolsPlugin/` arbeiten (Shared/Presets betrifft auch BotDriver: beide bauen). Muss ohne Bots und ohne Webseite laufen.
- Preset-Ebenen (cfg/ → presets/tracks/<t>/ → presets/classes/<c>/) liegen im Kern (`PresetOverlay.cs`).
