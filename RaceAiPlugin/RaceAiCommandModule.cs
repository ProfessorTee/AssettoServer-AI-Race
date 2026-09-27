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
                      $"fuel {b.Fuel:F0} l, tyres {b.Car.TyreGripAt(b.TyreVirtualKm) * 100:F0} %, stops {b.PitStops}");
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

    [Command("raceai_aggression"), RequireAdmin]
    public void SetAggression(float aggression)
    {
        aggression = Math.Clamp(aggression, 0, 100);
        _service.SetAggression(aggression);
        Reply($"Race AI aggression set to {aggression:F0}");
    }
}
