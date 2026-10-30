# AssettoServer AI Race – racing AI for AssettoServer

**English** · [Deutsch](README.de.md)

AI opponents for Assetto Corsa on an **AssettoServer**, like in single player, but online. Bots take part in practice,
qualifying and the race and line up on the real starting grid. Their lap times show up in the leaderboard and results.
Friends can join and pick any car. If needed, a bot makes room.

First target: the **Nordschleife** (`ks_nordschleife`, layout `nordschleife`) with **GT3** cars.

> **Physics rewrite:** On the branch [`bots-physics`](https://github.com/ProfessorTee/AssettoServer-AI-Race/tree/bots-physics)
> the bots drive with a real vehicle model and the Kunos driver rebuilt from `acs.exe` instead of kinematically. Work in progress.

> **Origin:** This project was developed by **Claude**, an AI by [Anthropic](https://www.anthropic.com), on behalf of and
> together with [ProfessorTee](https://github.com/ProfessorTee) (Claude Code, model `claude-opus-5-5`).
> Claude wrote the core patch, the plugins BotDriver, WebPortal, ServerTools and DriverRecorder, the test tool `BotDriverTool`,
> the tests, the build scripts and the documentation. ProfessorTee tested with the real game, set the direction and gave feedback.
>
> It is based on [AssettoServer](https://github.com/compujuckel/AssettoServer) by compujuckel and contributors, license
> **AGPL-3.0** (see `LICENSE`). The original README is in [`README.AssettoServer.md`](README.AssettoServer.md).

## What the bots can do

- They drive the Kunos racing line (`fast_lane.ai`) and use **car data like Content Manager**, read directly from `data.acd`:
  weight, engine, gearbox, tyres, aero.
- **Strength in %** with ± spread (`AiStrength`, `AiStrengthSpread`). Calibration turns this into a target lap time per car.
- **Human-like:** weaker bots brake earlier, get on the throttle later and make mistakes: braking too late, too much throttle on
  corner exit, now and then two wheels on the grass, rarely a spin with hazard lights.
- **Racecraft:**
  - Slipstream, attacking, defending, inside-line priority.
  - Impatience with headlight flashing when a bot is stuck behind a slower car.
  - Blue flag: the lapped car moves to the side and signals.
  - Yellow flag: hazard lights, slower, no overtaking.
  - Light contact in close battles.
- **Endurance:** fuel, tyre wear, **damage** with repairs and **pit stops** via `pit_lane.ai`, with strategy.
- **Weather:** rain makes the track slippery. Optionally real weather from Open-Meteo via CSP WeatherFX (Sol/Pure).
- **Driver clones:** the plugin `DriverRecorderPlugin` records players who opt in with `/rec on` (CSP script sent by the server).
  This produces a clone that drives their line and pace. If a player loses connection during a race, their clone keeps driving
  their car until they are back. See [`DriverRecorderPlugin/README.md`](DriverRecorderPlugin/README.md).
- **Admin page** in the browser with live map, join page for friends, live timing and remote restart/update.
- Every feature can be switched off in the plugin's yml, and live via `/bots_set` as admin.

Each plugin's documentation is in its folder. Developer status: [`BotDriverPlugin/UEBERGABE.md`](BotDriverPlugin/UEBERGABE.md) (German).

## Structure

Four plugins; each runs on its own and complements the others when present (interfaces in the core, `AssettoServer/Server/Extensions/`).

| Path | Contents |
|---|---|
| `AssettoServer/…` | **Core patch**: external AI slots, official laps for bots, preset layers, clock sync, plugin interfaces, migration of old configuration |
| [`BotDriverPlugin/`](BotDriverPlugin/README.md) | The bots: driving, battles, clones, takeover on disconnect. `Core/` is the AI without AssettoServer dependency, `Tool/` the offline tool |
| [`WebPortalPlugin/`](WebPortalPlugin/README.md) | Web pages: `/join`, `/live`, `/stats`, `/admin` |
| [`ServerToolsPlugin/`](ServerToolsPlugin/README.md) | Rotation, classes, statistics/safety rating, real weather, restart, shared settings |
| [`DriverRecorderPlugin/`](DriverRecorderPlugin/README.md) | Records players with consent (`/rec on`), basis of the driver clones |
| `Shared/` | Source compiled into several plugins (track geometry, classes, admin access, config writer) |
| `examples/nordschleife-gt3/` | Example configuration: 4 player slots, 16 bots |
| `tools/` | Build, test server without .NET SDK (`setup-testserver.sh`, `update-server.sh`, `start-server.sh`), prebuilt DLLs |

Old servers with `RaceAiPlugin` keep working unchanged: `RaceAiPlugin` in `EnablePlugins` loads the new plugins, and
`plugin_race_ai_cfg.yml` is split into `plugin_bot_driver_cfg.yml`, `plugin_web_portal_cfg.yml` and
`plugin_server_tools_cfg.yml` on first start. The old folder `plugins/RaceAiPlugin` is no longer loaded and can be deleted.

## Quick start (Linux, without .NET SDK)

```bash
tools/setup-testserver.sh /path/to/steamapps/common/assettocorsa   # downloads AssettoServer v0.0.55-pre42 and sets everything up
tools/install-desktop-entry.sh                                      # launcher in the start menu and on the desktop
tools/server-desktop.sh                                             # start server + admin page
```

After a `git pull`, `tools/update-server.sh` installs the new plugins and extends the configuration.

Joining in Content Manager: *Online → LAN* or favourites with `IP:8081`. CSP must be installed.
For friends from outside: forward ports 9600 TCP+UDP and 8081 TCP on your router to your PC.

## Building (the clean way)

With the .NET 11 SDK: `tools/build.sh` (or `tools/build.sh win-x64`) builds server and plugins into `out-linux-x64/`.

```bash
dotnet run --project BotDriverPlugin/Tool -- selftest
```

## License

AGPL-3.0, like AssettoServer. All changes compared to the original are in the Git history (commits from `e92d253`).
