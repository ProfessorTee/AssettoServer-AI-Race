using BotDriverPlugin.Core;
using AssettoServer.Server.Configuration;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace BotDriverPlugin;

public enum BotSessionStart
{
    Pits,
    Track
}

public enum BotGridOrder
{
    /// <summary>Like real racing: qualifying result (or the player start position setting when there was no qualifying).</summary>
    Qualifying,
    /// <summary>Weakest bots in front, strongest at the back: they have to fight their way through the field.</summary>
    SlowestFirst,
    /// <summary>Bots in random order.</summary>
    Random
}

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
    /// <summary>Evenly spaced values over the range (Linear is the same).</summary>
    Even = 0,
    Linear = 0,
    Random = 1
}

public enum BotSessionMode
{
    /// <summary>Bots drive laps (and set lap times).</summary>
    Drive,
    /// <summary>Bots stay parked in their pit box.</summary>
    Parked
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class DriverEntryConfiguration
{
    [YamlMember(Description = "Entry list slot index (0 = first [CAR_0])")]
    public int Slot { get; set; }
    [YamlMember(Description = "Driver name. Empty = DRIVERNAME from entry_list.ini or a name from the name list")]
    public string? Name { get; set; }
    [YamlMember(Description = "Three letter nation code, e.g. GER")]
    public string? Nation { get; set; }
    [YamlMember(Description = "AI strength in percent for this driver. Empty = from AiStrength / AiStrengthSpread")]
    public float? Strength { get; set; }
    [YamlMember(Description = "AI aggression 0-100 for this driver (AC AI_AGGRESSION). Empty = global AiAggression")]
    public float? Aggression { get; set; }

    // no longer used (settings of the kinematic bots); kept so older configuration files still load
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? Level { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? Personality { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? Clone { get; set; }
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class BotDriverConfiguration : IValidateConfiguration<BotDriverConfigurationValidator>
{
    [YamlMember(Description = "Entry list slots (0-based) that are driven by racing AI. Empty = every entry with AI=fixed or AI=auto in entry_list.ini. " +
                              "Keep EnableAi in extra_cfg.yml switched off, otherwise the traffic AI takes over these slots.")]
    public List<int> BotSlots { get; set; } = [];

    [YamlMember(Description = "AI strength in percent of the lap the AC AI drives at level 100 % (95 = lap time / 0.95). Turned into the AC AI level per car")]
    public float AiStrength { get; set; } = 94;

    [YamlMember(Description = "+/- range around AiStrength that is spread over the bots, e.g. 94 +/- 3 gives bots from 91 to 97 %")]
    public float AiStrengthSpread { get; set; } = 3;

    [YamlMember(Description = "What 100 % means: Field = the median best lap of all bot cars (same strength = same lap time in every car, like a BoP), " +
                              "Car = each car's own best lap")]
    public StrengthReference AiStrengthReference { get; set; } = StrengthReference.Field;

    [YamlMember(Description = "How the range is spread: Even (evenly spaced, e.g. 80 +/- 10 with 5 bots = 70, 75, 80, 85, 90, shuffled over the bots) or Random")]
    public StrengthDistribution AiStrengthDistribution { get; set; } = StrengthDistribution.Even;

    [YamlMember(Description = "AI aggression 0-100 like AC's AI_AGGRESSION: how readily a bot commits to a pass with the next narrow part close")]
    public float AiAggression { get; set; } = 50;

    [YamlMember(Description = "Random spread of the aggression per bot (+/-)")]
    public float AiAggressionVariation { get; set; } = 10;

    [YamlMember(Description = "Per-slot overrides (name, nation, strength, aggression)")]
    public List<DriverEntryConfiguration> Drivers { get; set; } = [];

    [YamlMember(Description = "Names used for bots without an explicit name. Empty = built-in list of invented names")]
    public List<string> Names { get; set; } = [];

    [YamlMember(Description = "Name prefix shown in front of every bot name, e.g. '[AI] '")]
    public string NamePrefix { get; set; } = "AI-";

    [YamlMember(Description = "Content Manager 'starting position' for races that are not started from a qualifying result: Default, First, Last, Middle, Random")]
    public PlayerGridPosition PlayerGridPosition { get; set; } = PlayerGridPosition.Default;

    [YamlMember(Description = "Order of the bots on the race grid: Qualifying (result, like real racing), SlowestFirst (weakest in front, strongest at the back), Random. Players keep their position")]
    public BotGridOrder BotGridOrder { get; set; } = BotGridOrder.Qualifying;

    [YamlMember(Description = "Shuffle the bots among themselves on the grid of races that are not started from a qualifying result")]
    public bool RandomizeBotGrid { get; set; } = true;

    [YamlMember(Description = "What bots do in practice sessions: Drive or Parked")]
    public BotSessionMode Practice { get; set; } = BotSessionMode.Drive;

    [YamlMember(Description = "What bots do in qualifying sessions: Drive or Parked")]
    public BotSessionMode Qualifying { get; set; } = BotSessionMode.Drive;

    [YamlMember(Description = "Path of the Assetto Corsa installation. Used when car data, the racing line or the start grid are not in the server's content folder, e.g. 'C:/Program Files (x86)/Steam/steamapps/common/assettocorsa'")]
    public string? AssettoCorsaPath { get; set; }

    [YamlMember(Description = "Optional grid json written by 'BotDriverTool grid --out ...' (start positions, pit boxes, start/finish line). Only needed when the track's kn5 files are not available")]
    public string? GridFile { get; set; }

    [YamlMember(Description = "Use the track's data/ai_hints.ini (HINT_ radius factors, MAXSPEED_) like the Kunos AI")]
    public bool UseTrackHints { get; set; } = true;

    [YamlMember(Description = "Scale on the AI's extra lateral grip (ai.ini ULTRA_GRIP of the car, AC default 1.2). 1.0 = like the AC AI")]
    public float PhysicsCorneringGrip { get; set; } = 1f;

    [YamlMember(Description = "The AC AI's push variation (its level drifts ±4 % over minutes) and genome (each bot a little stronger in some parts of the lap)")]
    public bool PushVariation { get; set; } = true;

    [YamlMember(Description = "The AC AI's random braking mistakes (every 20-120 s up to 2 s of locking up / overbraking)")]
    public bool HumanErrors { get; set; } = true;

    [YamlMember(Description = "Most bots on track at once (-1 = all bot slots). Admin: /bots_count <number|on|off>")]
    public int MaxBots { get; set; } = -1;

    [YamlMember(Description = "Detailed logging for troubleshooting: every 10 s a line per bot, the bots' laps and pit stops in the log and a telemetry file per " +
                              "session in bot-debug/ (position, heading, speed, inputs, tyres, fuel, damage, the AI's plan, twice a second per bot). Admin: /bots_debug on|off [bot]")]
    public bool Debug { get; set; }

    [YamlMember(Description = "Height of the car position above the racing line (m). AssettoServer's traffic AI uses 0")]
    public float HeightOffset { get; set; } = 0f;

    [YamlMember(Description = "Cool-down lap after the chequered flag: share of the corner speed (top speed at most 120 km/h, throttle at most half)")]
    public float CoolDownPace { get; set; } = 0.6f;

    [YamlMember(Description = "Headlights on during the day")]
    public bool DaytimeLights { get; set; } = false;

    [YamlMember(Description = "Fuel consumption (uses FUEL_RATE of server_cfg.ini). Fuel weight slows the car down")]
    public bool Fuel { get; set; } = true;

    [YamlMember(Description = "Tyre wear with the car's Kunos wear curves (uses TYRE_WEAR_RATE of server_cfg.ini)")]
    public bool TyreWear { get; set; } = true;

    [YamlMember(Description = "Tyre wear on top of TYRE_WEAR_RATE: 1.0 = 100 % (a GT3 set lasts about 7 Nordschleife laps), 2.0 = twice as fast")]
    public float TyreWearFactor { get; set; } = 1.0f;

    [YamlMember(Description = "Pit stops (fuel, tyres at 95-97 % grip like the AC AI, repairs, mandatory stop of RACE_PIT_WINDOW_START/END). Needs the track's ai/pit_lane.ai and AC_PIT_x positions")]
    public bool PitStops { get; set; } = true;

    [YamlMember(Description = "Pit lane speed limit (km/h)")]
    public float PitSpeedKmh { get; set; } = 80;

    [YamlMember(Description = "Fuel for this many laps in practice / qualifying (races: fuel for the race distance, limited by the tank)")]
    public float PracticeFuelLaps { get; set; } = 4;
    public float QualifyingFuelLaps { get; set; } = 2;

    [YamlMember(Description = "Players may take any bot's car: when all player slots of a car are taken, a bot leaves and hands its slot over. " +
                              "false = bot cars are only for bots")]
    public bool PlayersCanTakeBotSlots { get; set; } = true;

    [YamlMember(Description = "Touches between bots: they bounce apart and take damage")]
    public bool BotContacts { get; set; } = true;

    [YamlMember(Description = "Bots take damage from contacts (visible damage zones, slower car) and repair it in the pits. Uses DAMAGE_MULTIPLIER of server_cfg.ini")]
    public bool BotDamage { get; set; } = true;

    [YamlMember(Description = "Bot damage on top of DAMAGE_MULTIPLIER: 1.0 = 100 %")]
    public float BotDamageFactor { get; set; } = 1.0f;

    [YamlMember(Description = "Grip loss of the bots on a wet track: 1.0 = 100 % (up to about -30 % grip in heavy rain), 0 = rain doesn't slow them down")]
    public float RainGripLoss { get; set; } = 1.0f;

    [YamlMember(Description = "At night bots use their high beams when nobody (player or bot) is within HighBeamRange ahead, and dip them as soon as somebody is")]
    public bool HighBeams { get; set; } = true;

    [YamlMember(Description = "Distance ahead (m) that has to be free for high beams")]
    public float HighBeamRange { get; set; } = 85;

    [YamlMember(Description = "Height correction for bots parked in their pit box (m, + = higher, - = lower)")]
    public float ParkHeightAdjust { get; set; } = 0;

    [YamlMember(Description = "Language of the chat messages: de or en. Empty = the one set in ServerToolsPlugin (else en)")]
    public string? ChatLanguage { get; set; }

    [YamlMember(Description = "Emergency: a bot standing this long (s) becomes a ghost for a few seconds (no collisions, also for players with CSP 0.2.8+) " +
                              "and a car facing the wrong way is turned round. 0 = off")]
    public float GhostAfterSeconds { get; set; } = 25;

    [YamlMember(Description = "Practice / qualifying start: Pits = the bots leave their boxes one after the other (players first), Track = spread over the track at speed")]
    public BotSessionStart SessionStart { get; set; } = BotSessionStart.Pits;

    [YamlMember(Description = "Seconds before the first bot leaves the pits in qualifying (players get a clear pit lane and track first) / in practice")]
    public float QualifyingBotDelaySeconds { get; set; } = 30;
    public float PracticeBotDelaySeconds { get; set; } = 10;

    [YamlMember(Description = "About this many seconds between two bots leaving the pits")]
    public float PitReleaseIntervalSeconds { get; set; } = 6;

    [YamlMember(Description = "When practice / qualifying time is up, bots on a timed lap get that lap estimated and go to the box at once, " +
                              "so the next session doesn't wait for them")]
    public bool EstimateLapsAtSessionEnd { get; set; } = true;

    [YamlMember(Description = "Log every bot lap to the server log (while debugging: Debug or /bots_debug on)")]
    public bool LogLaps { get; set; } = true;

    // no longer used (settings of the kinematic bots and their racecraft); kept so older configuration files still load
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? AiLevel { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? AiLevelVariation { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? PhysicsDriving { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? SlipstreamStrength { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? EdgeMargin { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? SideMargin { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? PlayerSideMargin { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? PlayerOverlap { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? FlashLights { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? FlashLightsDaytime { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? ImpatienceSeconds { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? PitLimiterFlash { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RecordingsFolder { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RejoinSeconds { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? TakeOverDisconnectedPlayers { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? TakeoverNameSuffix { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? FlashStartDelaySeconds { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? BlueFlags { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? YellowFlags { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? AnnounceOvertakes { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? TyreChangeGrip { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? HumanErrorsBelow { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? HumanErrorsFull { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? Spins { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? LineErrors { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? GrassMoments { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RainCaution { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? PersonalLines { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RealisticStart { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RubberBanding { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RubberBandingAhead { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RubberBandingBehind { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RubberBandingDistance { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? RubberBandingTime { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? UsePersonalities { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? Personalities { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? YellowFlagChat { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? UnstuckSeconds { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? AnnouncePitStops { get; set; }
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public object? IncidentLogFolder { get; set; }
}
