using AssettoServer.Server.Configuration;
using System.Text;
using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using JetBrains.Annotations;
using Qmmands;

namespace BotDriverPlugin;

[UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
public class BotDriverCommandModule : ACModuleBase
{
    private readonly BotDriverService _service;

    public BotDriverCommandModule(BotDriverService service)
    {
        _service = service;
    }

    [Command("bots", "raceai")]
    public void Status()
    {
        if (!_service.Enabled)
        {
            Reply("No bots on this server.");
            return;
        }

        var sb = new StringBuilder("Bots:");
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

    [Command("bots_strength", "raceai_strength", "raceai_level"), RequireAdmin]
    public void SetStrength(float strength, float spread = -1)
    {
        strength = Math.Clamp(strength, 50, 110);
        _service.Field.SetGlobalStrength(strength, spread >= 0 ? spread : _service.Field.CurrentSpread);
        Reply($"Bot strength set to {strength:F0} %" + (spread >= 0 ? $" +/- {spread:F0} %" : ""));
    }

    /// <summary>Night test: all bots show left / right indicator, hazards, headlight flash and brake lights for about 6 s each.</summary>
    [Command("bots_lighttest", "raceai_lighttest", "raceai_signaltest"), RequireAdmin]
    public void LightTest()
    {
        Reply(_service.StartSignalTest()
            ? "Bot signal test: left indicator, right indicator, hazards, flash, brake lights, high beams (about 6 s each). Tip: /settime 22:00 for night"
            : "Bots are not active.");
    }

    /// <summary>Switches a feature on or off until the server restarts.</summary>
    [Command("bots_set", "raceai_set"), RequireAdmin]
    public void SetFeature(string feature, string value)
    {
        bool on = value.ToLowerInvariant() is "on" or "1" or "true" or "an" or "ein";
        Reply(_service.Field.SetFeature(feature.ToLowerInvariant(), on)
            ? $"BotDriver: {feature} {(on ? "on" : "off")}"
            : "Unknown feature. Use: errors, lines, spins, grass, contacts, damage, blueflags, yellowflags, flash, highbeams, raincaution");
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

    [Command("bots_grid", "raceai_grid"), RequireAdmin]
    public void SetGrid(string order)
    {
        Reply(_service.Grid.SetGridOrder(order)
            ? $"BotDriver: bot grid order {order} from the next race"
            : "Use: /bots_grid Qualifying | SlowestFirst | Random");
    }

    /// <summary>Number of bots: /bots_count 8, /bots_count off, /bots_count on (all).</summary>
    [Command("bots_count", "raceai_bots"), RequireAdmin]
    public void SetBots(string count)
    {
        string c = count.Trim().ToLowerInvariant();
        int? limit = c is "on" or "all" or "an" or "alle" ? null : c is "off" or "aus" or "0" ? 0 : int.TryParse(c, out var n) ? Math.Max(0, n) : -2;
        Reply(limit == -2 ? "Use: /bots_count <number> | on | off" : _service.SetBotLimit(limit));
    }

    /// <summary>Safety car: /bots_sc on [km/h], /bots_sc off.</summary>
    [Command("bots_sc", "bots_safetycar", "raceai_sc", "raceai_safetycar"), RequireAdmin]
    public void SafetyCar(string state, float kmh = 0)
    {
        bool on = state.ToLowerInvariant() is "on" or "1" or "an" or "ein" or "true";
        Reply(_service.SetSafetyCar(on, kmh > 0 ? kmh : null));
    }

    /// <summary>Debug logging: /bots_debug on [bot name or car number], /bots_debug off.</summary>
    [Command("bots_debug", "raceai_debug"), RequireAdmin]
    public void Debug(string state, [Remainder] string? bot = null)
    {
        bool on = state.ToLowerInvariant() is "on" or "1" or "an" or "ein" or "true";
        Reply(_service.SetDebug(on, bot));
    }

    /// <summary>Duel against a recorded player's line: /bots_duel name [bot name or car number] [pace %], /bots_duel off.</summary>
    [Command("bots_duel", "raceai_duel", "duel"), RequireAdmin]
    public void Duel([Remainder] string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) { Reply("/bots_duel <player> [bot|car#] [pace %] or /bots_duel off"); return; }
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

    /// <summary>Rubber band to the players: /bots_rubber 50 (0 = off).</summary>
    [Command("bots_rubber", "raceai_rubber", "raceai_rubberband"), RequireAdmin]
    public void Rubber(float percent)
    {
        Reply(_service.Field.SetRubberBand(percent));
    }

    [Command("bots_aggression", "raceai_aggression"), RequireAdmin]
    public void SetAggression(float aggression)
    {
        aggression = Math.Clamp(aggression, 0, 100);
        _service.Field.SetGlobalAggression(aggression);
        Reply($"Bot aggression set to {aggression:F0}");
    }
}
