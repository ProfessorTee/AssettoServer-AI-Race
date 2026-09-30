using System.Text;
using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using JetBrains.Annotations;
using Qmmands;

namespace RaceAiPlugin;

[UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
public class RaceAiCommandModule : ACModuleBase
{
    private readonly RaceAiService _service;
    private readonly TrackRotation _rotation;
    private readonly PlayerStats _stats;

    public RaceAiCommandModule(RaceAiService service, TrackRotation rotation, PlayerStats stats)
    {
        _stats = stats;
        _service = service;
        _rotation = rotation;
    }

    [Command("raceai", "bots")]
    public void Status()
    {
        if (!_service.Enabled)
        {
            Reply("Race AI is not active on this server.");
            return;
        }

        var sb = new StringBuilder("Race AI bots:");
        foreach (var slot in _service.Slots.Where(s => s.Active))
        {
            var b = slot.Bot;
            string best = b.BestLapSeconds < 1e6 ? TimeSpan.FromSeconds(b.BestLapSeconds).ToString(@"m\:ss\.fff") : "-";
            sb.Append($"\n{b.Name} ({slot.EntryCar.Model}) {b.Driver.Level:F0} %, {b.Driver.Personality.Name}, aggr. {b.Driver.Aggression * 100:F0}, laps {b.LapsCompleted}, best {best}, " +
                      $"fuel {b.Fuel:F0} l, tyres {b.Car.TyreGripAt(b.TyreVirtualKm) * 100:F0} %, stops {b.PitStops}, " +
                      $"mistakes {b.MistakeCount}, spins {b.SpinCount}, damage {Core.RaceWorld.BodyDamagePercent(b):F0} %");
        }
        Reply(sb.ToString());
    }

    [Command("raceai_strength", "raceai_level"), RequireAdmin]
    public void SetStrength(float strength, float spread = -1)
    {
        strength = Math.Clamp(strength, 50, 110);
        _service.SetGlobalStrength(strength, spread >= 0 ? spread : _service.CurrentSpread);
        Reply($"Race AI strength set to {strength:F0} %" + (spread >= 0 ? $" +/- {spread:F0} %" : ""));
    }

    /// <summary>Night test: all bots show left / right indicator, hazards, headlight flash and brake lights for about 6 s each.</summary>
    [Command("raceai_lighttest", "raceai_signaltest"), RequireAdmin]
    public void LightTest()
    {
        Reply(_service.StartSignalTest()
            ? "Race AI signal test: left indicator, right indicator, hazards, flash, brake lights, high beams (about 6 s each). Tip: /settime 22:00 for night"
            : "Race AI is not active.");
    }

    /// <summary>Switches a feature on or off until the server restarts.</summary>
    [Command("raceai_set"), RequireAdmin]
    public void SetFeature(string feature, string value)
    {
        bool on = value.ToLowerInvariant() is "on" or "1" or "true" or "an" or "ein";
        Reply(_service.SetFeature(feature.ToLowerInvariant(), on)
            ? $"Race AI: {feature} {(on ? "on" : "off")}"
            : "Unknown feature. Use: errors, lines, spins, grass, contacts, damage, blueflags, yellowflags, flash, highbeams, raincaution, realweather");
    }

    /// <summary>Driver change: the clone takes over at the next stop in the box (or drives on instead of coming in).</summary>
    [Command("bot")]
    public void Bot()
    {
        if (Client == null) { Reply("Only for players."); return; }
        Reply(_service.CommandBot(Client));
    }

    /// <summary>Driver change: the clone comes into the pits so the player takes over again.</summary>
    [Command("play")]
    public void Play()
    {
        if (Client == null) { Reply("Only for players."); return; }
        Reply(_service.CommandPlay(Client));
    }

    [Command("raceai_nexttrack"), RequireAdmin]
    public void NextTrack(string? track = null)
    {
        Reply(_rotation.Active
            ? _rotation.StartChange("admin", track) ? $"Track change to {track ?? _rotation.NextTrack()} started" : "A track change is already running"
            : "No track rotation (rotation.yml)");
    }

    [Command("raceai_grid"), RequireAdmin]
    public void SetGrid(string order)
    {
        Reply(_service.SetGridOrder(order)
            ? $"Race AI: bot grid order {order} from the next race"
            : "Use: /raceai_grid Qualifying | SlowestFirst | Random");
    }

    /// <summary>Number of bots: /raceai_bots 8, /raceai_bots off, /raceai_bots on (all).</summary>
    [Command("raceai_bots"), RequireAdmin]
    public void SetBots(string count)
    {
        string c = count.Trim().ToLowerInvariant();
        int? limit = c is "on" or "all" or "an" or "alle" ? null : c is "off" or "aus" or "0" ? 0 : int.TryParse(c, out var n) ? Math.Max(0, n) : -2;
        Reply(limit == -2 ? "Use: /raceai_bots <number> | on | off" : _service.SetBotLimit(limit));
    }

    /// <summary>Safety car: /raceai_sc on [km/h], /raceai_sc off.</summary>
    [Command("raceai_sc", "raceai_safetycar"), RequireAdmin]
    public void SafetyCar(string state, float kmh = 0)
    {
        bool on = state.ToLowerInvariant() is "on" or "1" or "an" or "ein" or "true";
        Reply(_service.SetSafetyCar(on, kmh > 0 ? kmh : null));
    }

    /// <summary>Debug logging: /raceai_debug on [bot name or car number], /raceai_debug off.</summary>
    [Command("raceai_debug"), RequireAdmin]
    public void Debug(string state, [Remainder] string? bot = null)
    {
        bool on = state.ToLowerInvariant() is "on" or "1" or "an" or "ein" or "true";
        Reply(_service.SetDebug(on, bot));
    }

    /// <summary>Best laps on the current track: /top (this week), /top all.</summary>
    [Command("top")]
    public void Top(string which = "week")
    {
        bool week = !(which.ToLowerInvariant() is "all" or "alltime" or "allzeit" or "ever");
        var list = _stats.Top(_service.TrackKeyName, week, 10);
        if (list.Count == 0)
        {
            Reply(week ? "No lap times this week yet. /top all for all time." : "No lap times yet.");
            return;
        }
        var sb = new StringBuilder(week ? $"Best laps this week ({PlayerStats.Week(DateTime.UtcNow)}):" : "Best laps of all time:");
        for (int i = 0; i < list.Count; i++)
            sb.Append($"\n{i + 1}. {PlayerStats.Fmt(list[i].Ms)} {list[i].Name} ({list[i].Car})");
        Reply(sb.ToString());
    }

    /// <summary>Profile with safety rating: /profile, /profile name, /sr.</summary>
    [Command("profile", "sr", "stats")]
    public void Profile([Remainder] string? name = null)
    {
        Reply(_stats.ProfileText(Client?.Guid ?? 0, name));
    }

    /// <summary>Duel against a recorded player's line: /raceai_duel name [bot name or car number] [pace %], /raceai_duel off.</summary>
    [Command("raceai_duel", "duel"), RequireAdmin]
    public void Duel([Remainder] string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) { Reply("/raceai_duel <player> [bot|car#] [pace %] or /raceai_duel off"); return; }
        if (parts[0].ToLowerInvariant() is "off" or "aus" or "stop") { if (_service.StopDuel() is { } err) Reply(err); return; }
        float pace = 100;
        if (parts.Count > 1 && parts[^1].EndsWith('%') && float.TryParse(parts[^1].TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
        {
            pace = p;
            parts.RemoveAt(parts.Count - 1);
        }
        string player = parts[0];
        string? bot = parts.Count > 1 ? string.Join(' ', parts.Skip(1)) : null;
        if (_service.StartDuel(player, bot, pace) is { } error) Reply(error);
    }

    [Command("raceai_aggression"), RequireAdmin]
    public void SetAggression(float aggression)
    {
        aggression = Math.Clamp(aggression, 0, 100);
        _service.SetGlobalAggression(aggression);
        Reply($"Race AI aggression set to {aggression:F0}");
    }
}
