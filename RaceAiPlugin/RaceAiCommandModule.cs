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

    public RaceAiCommandModule(RaceAiService service)
    {
        _service = service;
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
            sb.Append($"\n{b.Name} ({slot.EntryCar.Model}) {b.Driver.Level:F0} %, aggr. {b.Driver.Aggression * 100:F0}, laps {b.LapsCompleted}, best {best}, " +
                      $"fuel {b.Fuel:F0} l, tyres {b.Car.TyreGripAt(b.TyreVirtualKm) * 100:F0} %, stops {b.PitStops}, " +
                      $"mistakes {b.MistakeCount}, spins {b.SpinCount}, damage {Core.RaceWorld.BodyDamagePercent(b):F0} %");
        }
        Reply(sb.ToString());
    }

    [Command("raceai_strength", "raceai_level"), RequireAdmin]
    public void SetStrength(float strength, float spread = -1)
    {
        strength = Math.Clamp(strength, 50, 110);
        _service.SetStrength(strength, spread >= 0 ? spread : null);
        Reply($"Race AI strength set to {strength:F0} %" + (spread >= 0 ? $" +/- {spread:F0} %" : ""));
    }

    /// <summary>Night test: all bots show left / right indicator, hazards, headlight flash and brake lights for about 6 s each.</summary>
    [Command("raceai_lighttest", "raceai_signaltest"), RequireAdmin]
    public void LightTest()
    {
        Reply(_service.StartSignalTest()
            ? "Race AI signal test: left indicator, right indicator, hazards, flash, brake lights (about 6 s each). Tip: /settime 22:00 for night"
            : "Race AI is not active.");
    }

    /// <summary>Switches a feature on or off until the server restarts: errors, spins, grass, contacts, damage, blueflags, yellowflags, flash.</summary>
    [Command("raceai_set"), RequireAdmin]
    public void SetFeature(string feature, string value)
    {
        bool on = value.ToLowerInvariant() is "on" or "1" or "true" or "an" or "ein";
        Reply(_service.SetFeature(feature.ToLowerInvariant(), on)
            ? $"Race AI: {feature} {(on ? "on" : "off")}"
            : "Unknown feature. Use: errors, spins, grass, contacts, damage, blueflags, yellowflags, flash");
    }

    [Command("raceai_aggression"), RequireAdmin]
    public void SetAggression(float aggression)
    {
        aggression = Math.Clamp(aggression, 0, 100);
        _service.SetAggression(aggression);
        Reply($"Race AI aggression set to {aggression:F0}");
    }
}
