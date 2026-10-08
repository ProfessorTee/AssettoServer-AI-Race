# Sitzung: WebPortalPlugin
Webseiten (`Pages/admin.html`, `*PageHtml.cs`), Live-Feed (`LiveFeed.cs`, `RaceView.cs`), Admin-API.
- Nur in `WebPortalPlugin/` arbeiten. Bot-Daten kommen aus `/api/bots/*` bzw. `IDrivenCars`, Rotation/Statistik aus `/api/tools/*`
  bzw. `IPlayerRating` – fehlen sie, muss die Seite trotzdem laufen (Teil ausblenden).
- Prüfen: Testserver starten, Endpunkte per curl, Seiten-Skripte auf Syntax (Node), Screenshot mit `firefox --headless --screenshot`.
