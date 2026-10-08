# AssettoServer AI Race – Regeln für alle Sitzungen

Repo: Fork von AssettoServer (AGPL-3.0) mit vier eigenen Plugins. Antworten auf Deutsch, kurz; Code/Kommentare/Commits Englisch.

## Aufteilung (jede Sitzung arbeitet in ihrem Ordner)
| Ordner | Zuständig für |
|---|---|
| `BotDriverPlugin/` | Bots: Fahrphysik, Racecraft, Fehler, Pit, Duelle, Klone, Übernahme bei Verbindungsabbruch. Test-Tool `BotDriverPlugin/Tool` |
| `WebPortalPlugin/` | Seiten `/join` `/live` `/stats` `/admin`, Live-Feed, Admin-API `/api/admin/*` |
| `ServerToolsPlugin/` | Rotation, Klassen, Statistik/Safety Rating, Echtwetter, Neustart, gemeinsame Einstellungen, Skripte |
| `DriverRecorderPlugin/` | Aufzeichnung (`/rec`), CSP-Skript |
| `AssettoServer/` (Kern) | nur wenn nötig, siehe unten |

## Regeln
- Plugins kennen sich nicht. Zusammenarbeit nur über die Verträge in `AssettoServer/Server/Extensions/PluginContracts.cs`
  (`ISharedSettings`, `IPlayerRating`, `IDrivenCars`, `IAdminWebAccess`) oder über die HTTP-APIs (`/api/bots`, `/api/tools`, ...).
  Ein Plugin muss allein laufen; fehlt ein anderes, wird der Teil ausgeblendet.
- `Shared/` wird von mehreren Plugins mitkompiliert: Änderungen dort mit allen betroffenen Plugins bauen.
- Kern und `PluginContracts.cs` nur ändern, wenn es nicht anders geht; Verträge nur erweitern, nicht brechen.
- Neue Einstellung = Property in der Konfigurationsklasse des Plugins (alte Namen nicht umbenennen: die Übernahme aus
  `plugin_race_ai_cfg.yml` hängt an den Namen).
- Bauen: `tools/build.sh` (alles). Bots prüfen: `dotnet run --project BotDriverPlugin/Tool -c Release -- selftest` und `sim`.
- Update-Pakete nach `~/Downloads/Server_Files/Update_<n>/` (neuer Ordner je Update, alte nicht anfassen), mit LIESMICH.txt.
- Commit/Push nur auf Wunsch des Nutzers bzw. wie bisher nach fertigem, getestetem Schritt.
