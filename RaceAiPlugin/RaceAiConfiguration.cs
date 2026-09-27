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

public enum StrengthReference
{
    Field,
    Car
}

public enum StrengthDistribution
{
    Even,
    Random
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
    [YamlMember(Description = "AI strength in percent for this driver. Empty = from AiStrength / AiStrengthSpread")]
    public float? Strength { get; set; }
    [YamlMember(Description = "Deprecated and ignored, use Strength", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
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

    [YamlMember(Description = "AI strength in percent of the best lap the car can do on this track (100 = at the limit, 95 = lap time / 0.95). " +
                              "Tip: your own best lap / the 100 % time from the log = your strength")]
    public float AiStrength { get; set; } = 94;

    [YamlMember(Description = "+/- range around AiStrength that is spread over the bots, e.g. 94 +/- 3 gives bots from 91 to 97 %")]
    public float AiStrengthSpread { get; set; } = 3;

    [YamlMember(Description = "What 100 % means: Field = the median best lap of all bot cars (same strength = same lap time in every car, like a BoP), " +
                              "Car = the best lap of each car itself (faster cars stay faster)")]
    public StrengthReference AiStrengthReference { get; set; } = StrengthReference.Field;

    [YamlMember(Description = "How the range is spread: Even (evenly spaced values, shuffled over the bots) or Random")]
    public StrengthDistribution AiStrengthDistribution { get; set; } = StrengthDistribution.Even;

    [YamlMember(Description = "Deprecated and ignored, use AiStrength", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public float? AiLevel { get; set; }

    [YamlMember(Description = "Deprecated and ignored, use AiStrengthSpread", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public float? AiLevelVariation { get; set; }

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

    [YamlMember(Description = "Fuel consumption (uses FUEL_RATE of server_cfg.ini). Fuel weight slows the car down")]
    public bool Fuel { get; set; } = true;

    [YamlMember(Description = "Tyre wear with the car's Kunos wear curves (uses TYRE_WEAR_RATE of server_cfg.ini)")]
    public bool TyreWear { get; set; } = true;

    [YamlMember(Description = "Tyre wear factor: Kunos virtual km per real km at average load. Higher = tyres wear out faster")]
    public float TyreWearFactor { get; set; } = 0.15f;

    [YamlMember(Description = "Bots stop for new tyres when worn tyres would have less grip than this (and new tyres pay off until the end)")]
    public float TyreChangeGrip { get; set; } = 0.95f;

    [YamlMember(Description = "Pit stops (fuel, tyres, mandatory stop of RACE_PIT_WINDOW_START/END). Needs the track's ai/pit_lane.ai and AC_PIT_x positions")]
    public bool PitStops { get; set; } = true;

    [YamlMember(Description = "Pit lane speed limit (km/h)")]
    public float PitSpeedKmh { get; set; } = 80;

    [YamlMember(Description = "Fuel for this many laps in practice / qualifying (races: fuel for the race distance, limited by the tank)")]
    public float PracticeFuelLaps { get; set; } = 4;
    public float QualifyingFuelLaps { get; set; } = 2;

    [YamlMember(Description = "Announce bot pit stops in chat")]
    public bool AnnouncePitStops { get; set; } = true;

    [YamlMember(Description = "Log every bot lap to the server log")]
    public bool LogLaps { get; set; } = true;
}
