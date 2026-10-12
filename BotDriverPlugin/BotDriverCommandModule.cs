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
            sb.Append($"\n{b.Name} ({slot.EntryCar.Model}) {b.Driver.Level:F0} % (AI level {b.Driver.Pace * 100:F1}), aggr. {b.Driver.Aggression / 0.95f * 100:F0}, laps {b.LapsCompleted}, best {best}, " +
                      $"fuel {b.Fuel:F0} l, tyres {Core.RaceWorld.CompoundName(b)} {Core.RaceWorld.WearGrip(b, b.TyreVirtualKm) * 100:F0} %, stops {b.PitStops}, " +
                      $"contacts {b.ContactCount}, damage {Core.RaceWorld.BodyDamagePercent(b):F0} %");
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

    /// <summary>Switches a feature on or off until the server restarts.</summary>
    [Command("bots_set", "raceai_set"), RequireAdmin]
    public void SetFeature(string feature, string value)
    {
        bool on = value.ToLowerInvariant() is "on" or "1" or "true" or "an" or "ein";
        Reply(_service.Field.SetFeature(feature.ToLowerInvariant(), on)
            ? $"BotDriver: {feature} {(on ? "on" : "off")}"
            : "Unknown feature. Use: errors, push, contacts, damage, highbeams");
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

    /// <summary>Debug logging: /bots_debug on [bot name or car number], /bots_debug off.</summary>
    [Command("bots_debug", "raceai_debug"), RequireAdmin]
    public void Debug(string state, [Remainder] string? bot = null)
    {
        bool on = state.ToLowerInvariant() is "on" or "1" or "an" or "ein" or "true";
        Reply(_service.SetDebug(on, bot));
    }

    [Command("bots_aggression", "raceai_aggression"), RequireAdmin]
    public void SetAggression(float aggression)
    {
        aggression = Math.Clamp(aggression, 0, 100);
        _service.Field.SetGlobalAggression(aggression);
        Reply($"Bot aggression set to {aggression:F0}");
    }
}
