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
    [YamlMember(Description = "Deprecated and ignored, use Strength", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public float? Level { get; set; }
    [YamlMember(Description = "AI aggression 0-100 for this driver. Empty = global AiAggression")]
    public float? Aggression { get; set; }
    [YamlMember(Description = "Personality name from Personalities (e.g. DiveBomber, Chill). Empty = random by Share")]
    public string? Personality { get; set; }
    [YamlMember(Description = "Clone of a recorded player (Steam ID or name, DriverRecorder plugin): the bot drives his line and speeds")]
    public string? Clone { get; set; }
}

/// <summary>A driving style. All values are optional; see the defaults in the reference configuration.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class PersonalityConfiguration
{
    public string Name { get; set; } = "Balanced";
    [YamlMember(Description = "How often this personality is picked (relative to the others)")]
    public float Share { get; set; } = 1;
    [YamlMember(Description = "Added to the aggression (-100..100)")]
    public float Aggression { get; set; }
    [YamlMember(Description = "-1..1: -1 smooth and careful (earlier, squeezes the pedal), 0 normal, 1 extremely late, stamps on the brakes, dives from far back")]
    public float BrakeBehavior { get; set; }
    [YamlMember(Description = "Old name of BrakeBehavior (0..1), still read")]
    public float? LateBraking { get => null; set { if (value is { } v) BrakeBehavior = v; } }
    [YamlMember(Description = "0..1: attacks and defends on the inside; 0.5 and above never tries round the outside")]
    public float InsideLine { get; set; }
    [YamlMember(Description = "Tyre wear multiplier (1.0 = 100 %)")]
    public float TyreWear { get; set; } = 1;
    [YamlMember(Description = "New tyres when worn tyres would have less grip than this (percent, e.g. 93 = at 7 % grip loss). 0 = TyreChangeGrip")]
    public float TyreChangeAt { get; set; }
    [YamlMember(Description = "Fuel use multiplier (1.0 = 100 %)")]
    public float FuelUse { get; set; } = 1;
    [YamlMember(Description = "0..1: 0 normal, 1 very smooth: gentle on brake pedal and throttle, brakes earlier and softer, lift and coast. Saves tyres (up to 30 %) and fuel (up to 8 %), costs a little time")]
    public float Smoothness { get; set; }
    [YamlMember(Description = "0..1: calm under pressure when somebody sits in the slipstream for long (1 = no extra mistakes)")]
    public float Composure { get; set; } = 0.5f;
    [YamlMember(Description = "0..1: weaves behind the car in front after a while in its slipstream")]
    public float Weaving { get; set; } = 0.4f;
    [YamlMember(Description = "Mistake multiplier (1.0 = 100 %)")]
    public float Mistakes { get; set; } = 1;
    [YamlMember(Description = "Line error multiplier (1.0 = 100 %): missed apex, running wide, early braking points")]
    public float LineErrors { get; set; } = 1;
    [YamlMember(Description = "0..1: patience behind a slower car. 0 = flashes after a few seconds, 1 = waits long and hardly flashes")]
    public float Patience { get; set; } = 0.5f;
    [YamlMember(Description = "-1..1: line through the corners. 1 = late apex (V: stays outside, turns in late and sharp, straight exit), -1 = round (U: earlier, softer, wide exit). Empty = default of this personality")]
    public float? ApexStyle { get; set; }
    [YamlMember(Description = "-1..1: 1 uses every centimetre including the kerbs, -1 stays well clear of the edges")]
    public float? TrackUse { get; set; }
    [YamlMember(Description = "0..1: early and hard on the throttle out of corners; now and then too early: runs wide, wheels on the grass")]
    public float? ExitGreed { get; set; }
    [YamlMember(Description = "Extra room (m) left to a car alongside: positive gives room and backs out early, negative squeezes and holds on")]
    public float? Room { get; set; }
    [YamlMember(Description = "0..1: how readily overtakes are tried (from further back, with less speed advantage, in tighter gaps)")]
    public float? Attack { get; set; }
    [YamlMember(Description = "0..1: how hard a position is defended; low values let a clearly faster car by")]
    public float? Defend { get; set; }
    [YamlMember(Description = "-1..1: the start. 1 quick reaction and aggressive launch (often wheelspin), -1 slow and careful")]
    public float? Launch { get; set; }
    [YamlMember(Description = "0..1: how far over the track edge an overtake may go: 1 = two wheels on the grass, also mid-corner")]
    public float? GrassPass { get; set; }
    [YamlMember(Description = "0..1: where overtakes are tried. 1 = anywhere, 0 = on the straights, in a corner only past a car off its line")]
    public float? CornerPass { get; set; }
    [YamlMember(Description = "-1..1: shift point. 1 = into the rev limiter before each upshift, 0 = the car's shift point, -1 = short shifting")]
    public float? ShiftRpm { get; set; }

    public Personality ToPersonality()
    {
        // values not in the configuration (older files): from the built-in personality of the same name
        var d = Personality.BuiltIn(Name) ?? new Personality();
        return new()
        {
            Name = Name, Aggression = Aggression / 100f, BrakeBehavior = Math.Clamp(BrakeBehavior, -1, 1), InsideLine = InsideLine, TyreWear = TyreWear, TyreChangeAt = TyreChangeAt,
            FuelUse = FuelUse, Smoothness = Smoothness, Composure = Composure, Weaving = Weaving, Mistakes = Mistakes, LineErrors = LineErrors,
            Patience = Math.Clamp(Patience, 0, 1),
            ApexStyle = Math.Clamp(ApexStyle ?? d.ApexStyle, -1, 1), TrackUse = Math.Clamp(TrackUse ?? d.TrackUse, -1, 1),
            ExitGreed = Math.Clamp(ExitGreed ?? d.ExitGreed, 0, 1), Room = Math.Clamp(Room ?? d.Room, -0.3f, 2f),
            Attack = Math.Clamp(Attack ?? d.Attack, 0, 1), Defend = Math.Clamp(Defend ?? d.Defend, 0, 1), Launch = Math.Clamp(Launch ?? d.Launch, -1, 1),
            GrassPass = Math.Clamp(GrassPass ?? d.GrassPass, 0, 1), CornerPass = Math.Clamp(CornerPass ?? d.CornerPass, 0, 1),
            ShiftRpm = Math.Clamp(ShiftRpm ?? d.ShiftRpm, -1, 1)
        };
    }

    public static List<PersonalityConfiguration> Defaults() => Personality.Defaults().Select(d => new PersonalityConfiguration
    {
        Name = d.Personality.Name, Share = d.Share, Aggression = d.Personality.Aggression * 100, BrakeBehavior = d.Personality.BrakeBehavior,
        InsideLine = d.Personality.InsideLine, TyreWear = d.Personality.TyreWear, TyreChangeAt = d.Personality.TyreChangeAt, FuelUse = d.Personality.FuelUse,
        Smoothness = d.Personality.Smoothness, Composure = d.Personality.Composure, Weaving = d.Personality.Weaving, Mistakes = d.Personality.Mistakes,
        LineErrors = d.Personality.LineErrors, Patience = d.Personality.Patience, ApexStyle = d.Personality.ApexStyle, TrackUse = d.Personality.TrackUse,
        ExitGreed = d.Personality.ExitGreed, Room = d.Personality.Room, Attack = d.Personality.Attack, Defend = d.Personality.Defend, Launch = d.Personality.Launch,
        GrassPass = d.Personality.GrassPass, CornerPass = d.Personality.CornerPass, ShiftRpm = d.Personality.ShiftRpm
    }).ToList();
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class BotDriverConfiguration : IValidateConfiguration<BotDriverConfigurationValidator>
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

    [YamlMember(Description = "How the range is spread: Even (evenly spaced, e.g. 80 +/- 10 with 5 bots = 70, 75, 80, 85, 90, shuffled over the bots) or Random")]
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
    public List<DriverEntryConfiguration> Drivers { get; set; } = [];

    [YamlMember(Description = "Names used for bots without an explicit name. Empty = built-in list of invented names")]
    public List<string> Names { get; set; } = [];

    [YamlMember(Description = "Name prefix shown in front of every bot name, e.g. '[AI] '")]
    public string NamePrefix { get; set; } = "AI-";

    [YamlMember(Description = "Content Manager 'starting position' for races that are not started from a qualifying result: Default, First, Last, Middle, Random")]
    public PlayerGridPosition PlayerGridPosition { get; set; } = PlayerGridPosition.Default;

    [YamlMember(Description = "Order of the bots on the race grid: Qualifying (result, like real racing), SlowestFirst (weakest in front, strongest at the back), Random. Players keep their positions")]
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

    [YamlMember(Description = "Use the track's data/ai_hints.ini (slower sections, max speeds) like the Kunos AI")]
    public bool UseTrackHints { get; set; } = true;

    [YamlMember(Description = "Slipstream: 1.0 = 100 % (normal), 0 = off, 2.0 = twice as strong")]
    public float SlipstreamStrength { get; set; } = 1.0f;

    [YamlMember(Description = "Distance bots keep to the track edges (m)")]
    public float EdgeMargin { get; set; } = 0.4f;

    [YamlMember(Description = "Most bots on track at once (-1 = all bot slots). Admin: /bots_count <number|on|off>")]
    public int MaxBots { get; set; } = -1;

    [YamlMember(Description = "Detailed logging for troubleshooting: every 10 s a line per bot (phase, speed, pit, tyres, offset). Admin: /bots_debug on|off [bot]")]
    public bool Debug { get; set; }

    [YamlMember(Description = "Extra lateral space bots keep to other cars (m)")]
    public float SideMargin { get; set; } = 0.5f;

    [YamlMember(Description = "Lateral space bots keep to players (m). Larger than SideMargin because a player's position arrives with a delay")]
    public float PlayerSideMargin { get; set; } = 1.0f;

    [YamlMember(Description = "A player counts as alongside while his car overlaps within this many metres (front/rear). Bots then leave him room " +
                              "and don't turn in on him. 0 = like a bot")]
    public float PlayerOverlap { get; set; } = 3.0f;

    [YamlMember(Description = "Height of the car position above the racing line (m). AssettoServer's traffic AI uses 0")]
    public float HeightOffset { get; set; } = 0f;

    [YamlMember(Description = "Speed factor for the cool-down lap after the chequered flag")]
    public float CoolDownPace { get; set; } = 0.6f;

    [YamlMember(Description = "Headlights on during the day")]
    public bool DaytimeLights { get; set; } = false;

    [YamlMember(Description = "Flash the headlights at a car that holds them up (at night high beam, during the day the lights)")]
    public bool FlashLights { get; set; } = true;

    [YamlMember(Description = "Also flash during the day (otherwise only when the lights are on anyway)")]
    public bool FlashLightsDaytime { get; set; } = true;

    [YamlMember(Description = "Seconds stuck behind a slower car until a bot is fully impatient (closer, earlier and more determined attacks, no crashes). 0 = off")]
    public float ImpatienceSeconds { get; set; } = 25f;

    [YamlMember(Description = "Bots flash their headlights in the pit lane while the pit limiter is on, like real GT3 cars (and the players' cars with CSP)")]
    public bool PitLimiterFlash { get; set; } = true;

    [YamlMember(Description = "Folder of the DriverRecorder plugin with the recorded laps of the players (for clones)")]
    public string RecordingsFolder { get; set; } = "recordings";

    [YamlMember(Description = "No longer used (rejoining into the own car was removed); kept so older configuration files still load")]
    public int RejoinSeconds { get; set; } = 60;

    [YamlMember(Description = "When a player disconnects during a race, a bot drives his car to the flag (like him when he was recorded, else as fast as his best lap)")]
    public bool TakeOverDisconnectedPlayers { get; set; } = true;

    [YamlMember(Description = "Added to the player's name while a bot drives his car")]
    public string TakeoverNameSuffix { get; set; } = " (KI)";

    [YamlMember(Description = "No flashing of the lights in the first seconds of a race, while the field is bunched up")]
    public float FlashStartDelaySeconds { get; set; } = 90f;

    [YamlMember(Description = "Blue flags: lapped bots move over to the side and indicate when the leaders come through")]
    public bool BlueFlags { get; set; } = true;

    [YamlMember(Description = "Yellow flags: bots slow down, don't overtake and switch the hazard lights on before a stopped or crawling car")]
    public bool YellowFlags { get; set; } = true;

    [YamlMember(Description = "Grip loss of the bots on a wet track: 1.0 = 100 % (up to about -30 % grip in heavy rain), 0 = rain doesn't slow them down")]
    public float RainGripLoss { get; set; } = 1.0f;

    [YamlMember(Description = "Announce in chat when a bot overtakes a player or a player overtakes a bot (with the track section from data/sections.ini)")]
    public bool AnnounceOvertakes { get; set; } = false;

    [YamlMember(Description = "Fuel consumption (uses FUEL_RATE of server_cfg.ini). Fuel weight slows the car down")]
    public bool Fuel { get; set; } = true;

    [YamlMember(Description = "Tyre wear with the car's Kunos wear curves (uses TYRE_WEAR_RATE of server_cfg.ini)")]
    public bool TyreWear { get; set; } = true;

    [YamlMember(Description = "Tyre wear on top of TYRE_WEAR_RATE: 1.0 = 100 % (a GT3 set lasts about 7 Nordschleife laps), 2.0 = twice as fast")]
    public float TyreWearFactor { get; set; } = 1.0f;

    [YamlMember(Description = "Bots stop for new tyres when worn tyres would have less grip than this (and new tyres pay off until the end)")]
    public float TyreChangeGrip { get; set; } = 0.95f;

    [YamlMember(Description = "Pit stops (fuel, tyres, mandatory stop of RACE_PIT_WINDOW_START/END). Needs the track's ai/pit_lane.ai and AC_PIT_x positions")]
    public bool PitStops { get; set; } = true;

    [YamlMember(Description = "Pit lane speed limit (km/h)")]
    public float PitSpeedKmh { get; set; } = 80;

    [YamlMember(Description = "Fuel for this many laps in practice / qualifying (races: fuel for the race distance, limited by the tank)")]
    public float PracticeFuelLaps { get; set; } = 4;
    public float QualifyingFuelLaps { get; set; } = 2;

    [YamlMember(Description = "Players may take any bot's car: when all player slots of a car are taken, a bot leaves and hands its slot over. " +
                              "The bot comes back when the player leaves. false = bot slots are closed to players")]
    public bool PlayersCanTakeBotSlots { get; set; } = true;

    [YamlMember(Description = "Human errors for weaker bots: braking too late (running wide) or too early, too much throttle on the exit (the rear steps out). " +
                              "Weaker bots then lose part of their time to mistakes instead of crawling through the corners")]
    public bool HumanErrors { get; set; } = true;

    [YamlMember(Description = "Bots below this strength (%) make human errors, more the weaker they are")]
    public float HumanErrorsBelow { get; set; } = 87;

    [YamlMember(Description = "At this strength (%) and below the errors are at their maximum")]
    public float HumanErrorsFull { get; set; } = 75;

    [YamlMember(Description = "A bad slide can end in a spin: the bot stops (hazard lights), waits for a gap, turns round and rejoins. Needs HumanErrors")]
    public bool Spins { get; set; } = true;

    [YamlMember(Description = "Imprecise lines of weaker bots: missed apex, turning in too early and running wide, braking too early, hesitating on the throttle. Scales with HumanErrors")]
    public bool LineErrors { get; set; } = true;

    [YamlMember(Description = "Bots sometimes run wide with two wheels on the grass")]
    public bool GrassMoments { get; set; } = true;

    [YamlMember(Description = "Light touches between bots in close fights (no big crashes)")]
    public bool BotContacts { get; set; } = true;

    [YamlMember(Description = "Bots take damage from contacts (visible damage zones, slower car) and repair it in the pits. Uses DAMAGE_MULTIPLIER of server_cfg.ini")]
    public bool BotDamage { get; set; } = true;

    [YamlMember(Description = "Bot damage on top of DAMAGE_MULTIPLIER: 1.0 = 100 %")]
    public float BotDamageFactor { get; set; } = 1.0f;

    [YamlMember(Description = "In the wet the bots drive more carefully, make more mistakes, take a rain line away from the water and can aquaplane")]
    public bool RainCaution { get; set; } = true;

    [YamlMember(Description = "At night bots use their high beams when nobody (player or bot) is within HighBeamRange ahead, and dip them as soon as somebody is")]
    public bool HighBeams { get; set; } = true;

    [YamlMember(Description = "Distance ahead (m) that has to be free for high beams")]
    public float HighBeamRange { get; set; } = 85;

    [YamlMember(Description = "Height correction for bots parked in their pit box (m, + = higher, - = lower)")]
    public float ParkHeightAdjust { get; set; } = 0;

    [YamlMember(Description = "Every bot drives its own line through the corners (by personality and driver: late or early apex, on the kerbs or clear of the edges) instead of all on the AI line")]
    public bool PersonalLines { get; set; } = true;
    [YamlMember(Description = "Realistic starts: reaction times, clutch, now and then wheelspin or a bogged launch, instead of a perfect launch for everybody")]
    public bool RealisticStart { get; set; } = true;
    [YamlMember(Description = "Rubber band to the players, 0-100 % (0 = off): bots ahead of the nearest player get slower, bots behind faster (like CSP's AI rubberbanding)")]
    public float RubberBanding { get; set; }
    [YamlMember(Description = "Rubber band at 100 %: at most this much slower (%) for bots ahead of the players")]
    public float RubberBandingAhead { get; set; } = 3;
    [YamlMember(Description = "Rubber band at 100 %: at most this much faster (%) for bots behind the players")]
    public float RubberBandingBehind { get; set; } = 6;
    [YamlMember(Description = "Rubber band: full effect from this distance to the nearest player (m)")]
    public float RubberBandingDistance { get; set; } = 200;
    [YamlMember(Description = "Rubber band: seconds until the full effect")]
    public float RubberBandingTime { get; set; } = 60;
    [YamlMember(Description = "Driver personalities: every bot gets one (random by Share, or Drivers[].Personality). false = everybody Balanced")]
    public bool UsePersonalities { get; set; } = true;

    [YamlMember(Description = "The personalities. Change the values, add your own or delete some. Empty = built-in set (Balanced, DiveBomber, Chill, FuelSaver)")]
    public List<PersonalityConfiguration> Personalities { get; set; } = [];

    [YamlMember(Description = "Chat message 'Yellow flag in sector X' when a car spins, crashes or stops on the track")]
    public bool YellowFlagChat { get; set; } = true;

    [YamlMember(Description = "Language of the chat messages: de or en. Empty = the one set in ServerToolsPlugin (else en)")]
    public string? ChatLanguage { get; set; }

    [YamlMember(Description = "Stuck in a cluster of standing cars for this long (s): the front car goes first, the others go round")]
    public float UnstuckSeconds { get; set; } = 4;

    [YamlMember(Description = "Emergency: a bot standing this long (s) becomes a ghost for a few seconds (no collisions, also for players with CSP 0.2.8+) " +
                              "and drives out of the jam. 0 = never")]
    public float GhostAfterSeconds { get; set; } = 25;

    [YamlMember(Description = "Practice / qualifying start: Pits = the bots leave their boxes one after the other (players first), Track = spread over the track at speed")]
    public BotSessionStart SessionStart { get; set; } = BotSessionStart.Pits;

    [YamlMember(Description = "Seconds before the first bot leaves the pits in qualifying (players get a clear pit lane and track first) / in practice")]
    public float QualifyingBotDelaySeconds { get; set; } = 30;
    public float PracticeBotDelaySeconds { get; set; } = 10;

    [YamlMember(Description = "About this many seconds between two bots leaving the pits")]
    public float PitReleaseIntervalSeconds { get; set; } = 6;

    [YamlMember(Description = "When practice / qualifying time is up, bots on a timed lap get that lap estimated and go to the box at once, " +
                              "so the session doesn't wait for them. false = they finish their lap like players")]
    public bool EstimateLapsAtSessionEnd { get; set; } = true;

    [YamlMember(Description = "Announce bot pit stops in chat")]
    public bool AnnouncePitStops { get; set; } = true;

    [YamlMember(Description = "Log every bot lap to the server log")]
    public bool LogLaps { get; set; } = true;
}
