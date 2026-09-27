using AssettoServer.Server.Configuration;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace RaceAiPlugin;

public enum PlayerGridPosition
{
    /// <summary>Keep the order AssettoServer uses (entry list order or qualifying result).</summary>
    Default,
    /// <summary>Players start in front of all bots.</summary>
    First,
    /// <summary>Players start behind all bots.</summary>
    Last,
    /// <summary>Players and bots are shuffled.</summary>
    Random,
    /// <summary>Players start in the middle of the field.</summary>
    Middle
}

public enum BotSessionMode
{
    /// <summary>Bots drive laps (and set lap times).</summary>
    Drive,
    /// <summary>Bots stay parked in their pit box.</summary>
    Parked
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class BotDriverConfiguration
{
    [YamlMember(Description = "Entry list slot index (0 = first [CAR_0])")]
    public int Slot { get; set; }
    [YamlMember(Description = "Driver name. Empty = DRIVERNAME from entry_list.ini or a name from the name list")]
    public string? Name { get; set; }
    [YamlMember(Description = "Three letter nation code, e.g. GER")]
    public string? Nation { get; set; }
    [YamlMember(Description = "AI level 0-100 for this driver. Empty = global AiLevel with variation")]
    public float? Level { get; set; }
    [YamlMember(Description = "AI aggression 0-100 for this driver. Empty = global AiAggression")]
    public float? Aggression { get; set; }
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class RaceAiConfiguration : IValidateConfiguration<RaceAiConfigurationValidator>
{
    [YamlMember(Description = "Entry list slots (0-based) that are driven by racing AI. Empty = every entry with AI=fixed or AI=auto in entry_list.ini. " +
                              "Keep EnableAi in extra_cfg.yml switched off, otherwise the traffic AI takes over these slots.")]
    public List<int> BotSlots { get; set; } = [];

    [YamlMember(Description = "AI level 0-100 like in Content Manager (100 = drives at the car's limit, 90 ≈ 2-3 % slower)")]
    public float AiLevel { get; set; } = 95;

    [YamlMember(Description = "Content Manager 'AI level variation': every bot gets a random level between AiLevel - AiLevelVariation and AiLevel")]
    public float AiLevelVariation { get; set; } = 5;

    [YamlMember(Description = "AI aggression 0-100 like in Content Manager (how early bots attack, how closely they follow, how much they defend)")]
    public float AiAggression { get; set; } = 50;

    [YamlMember(Description = "Random spread of the aggression per bot (+/-)")]
    public float AiAggressionVariation { get; set; } = 10;

    [YamlMember(Description = "Per-slot overrides (name, nation, level, aggression)")]
    public List<BotDriverConfiguration> Drivers { get; set; } = [];

    [YamlMember(Description = "Names used for bots without an explicit name. Empty = built-in list of invented names")]
    public List<string> Names { get; set; } = [];

    [YamlMember(Description = "Name prefix shown in front of every bot name, e.g. '[AI] '")]
    public string NamePrefix { get; set; } = "";

    [YamlMember(Description = "Content Manager 'starting position' for races that are not started from a qualifying result: Default, First, Last, Middle, Random")]
    public PlayerGridPosition PlayerGridPosition { get; set; } = PlayerGridPosition.Default;

    [YamlMember(Description = "Shuffle the bots among themselves on the grid of races that are not started from a qualifying result")]
    public bool RandomizeBotGrid { get; set; } = true;

    [YamlMember(Description = "What bots do in practice sessions: Drive or Parked")]
    public BotSessionMode Practice { get; set; } = BotSessionMode.Drive;

    [YamlMember(Description = "What bots do in qualifying sessions: Drive or Parked")]
    public BotSessionMode Qualifying { get; set; } = BotSessionMode.Drive;

    [YamlMember(Description = "Path of the Assetto Corsa installation. Used when car data, the racing line or the start grid are not in the server's content folder, e.g. 'C:/Program Files (x86)/Steam/steamapps/common/assettocorsa'")]
    public string? AssettoCorsaPath { get; set; }

    [YamlMember(Description = "Optional grid json written by 'RaceAiTool grid --out ...' (start positions, pit boxes, start/finish line). Only needed when the track's kn5 files are not available")]
    public string? GridFile { get; set; }

    [YamlMember(Description = "Use the track's data/ai_hints.ini (slower sections, max speeds) like the Kunos AI")]
    public bool UseTrackHints { get; set; } = true;

    [YamlMember(Description = "Slipstream strength 0-1 (share of aero drag removed directly behind another car)")]
    public float SlipstreamStrength { get; set; } = 0.35f;

    [YamlMember(Description = "Distance bots keep to the track edges (m)")]
    public float EdgeMargin { get; set; } = 0.4f;

    [YamlMember(Description = "Extra lateral space bots keep to other cars (m)")]
    public float SideMargin { get; set; } = 0.5f;

    [YamlMember(Description = "Height of the car position above the racing line (m). AssettoServer's traffic AI uses 0")]
    public float HeightOffset { get; set; } = 0f;

    [YamlMember(Description = "Speed factor for the cool-down lap after the chequered flag")]
    public float CoolDownPace { get; set; } = 0.6f;

    [YamlMember(Description = "Headlights on during the day")]
    public bool DaytimeLights { get; set; } = false;

    [YamlMember(Description = "Announce in chat when a bot overtakes a player or a player overtakes a bot (with the track section from data/sections.ini)")]
    public bool AnnounceOvertakes { get; set; } = false;

    [YamlMember(Description = "Log every bot lap to the server log")]
    public bool LogLaps { get; set; } = true;
}
