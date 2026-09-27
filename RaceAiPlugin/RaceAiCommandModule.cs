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
            sb.Append($"\n{b.Name} ({slot.EntryCar.Model}) level {b.Driver.Level:F0}, aggression {b.Driver.Aggression * 100:F0}, laps {b.LapsCompleted}, best {best}");
        }
        Reply(sb.ToString());
    }

    [Command("raceai_level"), RequireAdmin]
    public void SetLevel(float level, float variation = -1)
    {
        level = Math.Clamp(level, 0, 100);
        _service.SetLevel(level, variation >= 0 ? variation : null);
        Reply($"Race AI level set to {level:F0}" + (variation >= 0 ? $" (variation {variation:F0})" : ""));
    }

    [Command("raceai_aggression"), RequireAdmin]
    public void SetAggression(float aggression)
    {
        aggression = Math.Clamp(aggression, 0, 100);
        _service.SetAggression(aggression);
        Reply($"Race AI aggression set to {aggression:F0}");
    }
}
