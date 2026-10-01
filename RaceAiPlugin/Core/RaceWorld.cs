using System.Numerics;

namespace RaceAiPlugin.Core;

public enum BotPhase
{
    /// <summary>Not on track (not sent to clients).</summary>
    Hidden,
    /// <summary>Standing on the grid, waiting for the start.</summary>
    Grid,
    Racing,
    /// <summary>Finished the race, slow cool-down lap.</summary>
    CoolDown,
    /// <summary>Standing still at a fixed position off the racing line (pit box). Not an obstacle for other bots.</summary>
    Parked
}

public sealed class DriverProfile
{
    /// <summary>Share of the car's grip the driver uses (0.85 = slow, 1.0 = limit).</summary>
    public float Pace { get; set; } = 0.97f;
    /// <summary>0 = never attacks/defends, 1 = dives into every gap.</summary>
    public float Aggression { get; set; } = 0.5f;
    /// <summary>1 = never makes mistakes.</summary>
    public float Consistency { get; set; } = 0.9f;
    /// <summary>0..1: human errors (late/early braking, sliding on the exit, spins). Weaker bots get more.</summary>
    public float Errors { get; set; }
    /// <summary>Driving style.</summary>
    public Personality Personality { get; set; } = Personality.Balanced;

    /// <summary>AI strength in percent (or AI level 0-100), for display.</summary>
    public float Level { get; set; } = 100;

    /// <summary>
    /// Builds a driver from Content Manager style settings: AI level 0-100 (100 = fastest) and aggression 0-100.
    /// Level 100 drives at the car's grip limit, every level point below costs 0.6 % of usable grip.
    /// </summary>
    /// <summary>
    /// Driver for an AI strength in percent of the car's best lap (see <see cref="StrengthCalibration"/>) and aggression 0-100.
    /// </summary>
    public static DriverProfile FromStrength(float strengthPercent, float pace, float aggression)
        => new()
        {
            Level = strengthPercent,
            Pace = pace,
            Aggression = Math.Clamp(aggression / 100f, 0, 1),
            Consistency = 0.75f + 0.23f * Math.Clamp((strengthPercent - 70) / 30f, 0, 1)
        };

    /// <summary>
    /// Error level for a strength: 0 at or above <paramref name="below"/> %, rising to 1 at <paramref name="full"/> %.
    /// </summary>
    // A slower driver mostly brakes earlier and softer and is later on the throttle out of a corner;
    // the speed through the corner itself drops much less (that's how a lap time gap between amateurs and pros looks).
    public static float CornerSkill(float pace) => pace >= 1 ? pace : 1 - (1 - pace) * 0.5f;
    // Braking itself stays fairly firm; slower drivers mostly brake earlier and then roll into the corner (see BrakeMargin),
    // and they wait longer before going to full throttle (corner plan), instead of pressing every pedal only half way.
    public static float BrakeSkill(float pace) => pace >= 1 ? pace : MathF.Max(0.55f, 1 - (1 - pace) * 0.8f);
    public static float ThrottleSkill(float pace) => pace >= 1 ? pace : MathF.Max(0.55f, 1 - (1 - pace) * 0.7f);
    /// <summary>Average metres a driver brakes too early and rolls towards the corner at corner speed.</summary>
    public static float BrakeMargin(float pace) => MathF.Pow(Math.Clamp(1 - pace, 0, 0.7f), 1.5f) * 100f;
    /// <summary>Average seconds after the apex before going back to full throttle.</summary>
    public static float ExitHesitation(float pace) => MathF.Pow(Math.Clamp(1 - pace, 0, 0.7f), 1.5f) * 2.0f;

    public static float ErrorsFor(float strengthPercent, float below = 87, float full = 75)
        => below <= full ? 0 : Math.Clamp((below - strengthPercent) / (below - full), 0, 1);

    public static DriverProfile FromLevel(float level, float aggression)
    {
        level = Math.Clamp(level, 0, 100);
        return new DriverProfile
        {
            Level = level,
            Pace = 1f - (100 - level) * 0.006f,
            Aggression = Math.Clamp(aggression / 100f, 0, 1),
            Consistency = 0.75f + 0.23f * Math.Clamp((level - 60) / 40f, 0, 1)
        };
    }
}

/// <summary>A car on track the bots have to race against (a human player). Updated from outside every tick.</summary>
public sealed class ExternalCar
{
    public int Id { get; init; }
    public float Length { get; set; } = 4.6f;
    public float Width { get; set; } = 2.0f;
    public bool Valid { get; internal set; }
    public float S { get; internal set; }
    public float Offset { get; internal set; }
    public float Speed { get; internal set; }
    /// <summary>Sideways speed (m/s, + = towards positive offset) and forward acceleration (m/s², from the last updates).</summary>
    public float LateralSpeed { get; internal set; }
    public float Accel { get; internal set; }
    /// <summary>World time (s) the position belongs to; the bots extrapolate from there to their own time.</summary>
    public double At { get; internal set; } = double.NaN;
    internal float PrevSpeed;
    internal double PrevAt = double.NaN;
    /// <summary>Completed laps of this car in the current race (set from outside, used for blue flags).</summary>
    public int Laps { get; set; }
    internal int HintIndex = -1;
}

public sealed class RaceBot
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public CarSpec Car { get; init; } = new();
    public DriverProfile Driver { get; init; } = new();
    public BotPhase Phase { get; set; } = BotPhase.Hidden;

    /// <summary>Unwrapped distance along the line (grows by the line length every lap).</summary>
    public double Distance { get; set; }
    public float Offset { get; set; }
    public float Speed { get; set; }
    public float LateralSpeed { get; internal set; }
    internal double ClampedAt = double.NaN;
    public float TargetOffset { get; set; }
    public float TargetSpeed { get; internal set; }
    public float Accel { get; internal set; }
    /// <summary>Pedals 0..1 (for the throttle / brake light shown to the clients).</summary>
    public float Throttle { get; internal set; }
    public float Brake { get; internal set; }
    internal float PrevTargetSpeed = float.NaN;

    // lap timing
    public int LapsCompleted { get; set; }
    public double LapStartTime { get; set; }
    public bool TimingValid { get; set; }
    public float LastLapSeconds { get; set; }
    public float BestLapSeconds { get; set; } = float.MaxValue;
    /// <summary>Race time (s) when the last lap was completed, measured from the start.</summary>
    public double TotalTime { get; set; }
    public double RaceStartTime { get; set; }
    internal long LapIndex;
    internal bool StartCrossed;

    // behaviour state
    internal int OvertakeTargetId = -1;
    internal double OvertakeSince;
    /// <summary>Since when the chosen side of the target is closed (the target covered it, or the track narrows).</summary>
    internal double OvertakeClosedSince = double.NaN;
    internal float OvertakeBestGap;
    internal double OvertakeCooldownUntil;
    internal int LastOvertakeTargetId = -1;
    /// <summary>0..1 share of aero drag removed by the car in front (slipstream).</summary>
    public float Draft { get; internal set; }
    internal float PaceNoise;
    internal float PressureEma;
    internal int OvertakeSide;
    internal double OvertakeSeparatedAt;
    internal double PaceNoiseUntil;
    internal double ReturnToLineAfter;
    internal double CautiousUntil;
    internal double MistakeUntil;
    internal double DefendUntil;
    internal float DefendOffset;
    internal bool InBrakingZone;

    public int Overtakes { get; internal set; }
    /// <summary>Debug counters: overtake moves started / rejected because no side was free.</summary>
    public int OvertakeAttempts { get; internal set; }
    public int OvertakeNoRoom { get; internal set; }
    public int OvertakeGiveUps { get; internal set; }
    public int Contacts { get; internal set; }

    public bool OnTrack => Phase is BotPhase.Grid or BotPhase.Racing or BotPhase.CoolDown;

    public Vector3 ParkPosition { get; internal set; }
    public Vector3 ParkForward { get; internal set; } = Vector3.UnitZ;
    /// <summary>Time the bot has been (nearly) standing still while racing, for the stuck detection.</summary>
    internal double StoppedSince = double.NaN;
    /// <summary>After being stuck behind a standing player for a long time the bot ignores players for a moment.</summary>
    internal double IgnorePlayersUntil;

    // ---- endurance
    /// <summary>Fuel in the tank (litres).</summary>
    public float Fuel { get; set; } = 30f;
    /// <summary>Tyre wear in Kunos "virtual km" of the current set.</summary>
    public float TyreVirtualKm { get; set; }
    /// <summary>Real km driven on the current set (for the warm-up of new tyres).</summary>
    public float TyreKm { get; set; } = 10f;
    /// <summary>Core tyre temperatures of the front and rear axle (°C).</summary>
    public float TyreTempFront { get; set; } = RaceWorld.TyreOptimum;
    public float TyreTempRear { get; set; } = RaceWorld.TyreOptimum;
    /// <summary>Laps still to be completed in this session including the current one (int.MaxValue = open end).</summary>
    public int RemainingLaps { get; set; } = int.MaxValue;
    public PitPhase Pit { get; internal set; }
    public string PitReason { get; internal set; } = "";
    public int PitStops { get; internal set; }
    public bool MandatoryPitDone { get; set; }
    public float PitBoxS { get; internal set; } = -1;
    public float PitBoxOffset { get; internal set; }
    internal float PitS;
    internal float PitLateral;
    internal double PitStoppedAt;
    internal bool PitServiced;
    internal double PitServiceUntil;
    internal float PitFuelToAdd;
    internal bool PitChangeTyres;
    internal double PitEnteredAt;
    internal long LastDecisionLap = long.MinValue;
    internal float FuelPerKmEma;
    internal float FuelAtLapStart = -1;
    internal float FuelAddedThisLap;
    internal bool PittedThisLap;
    /// <summary>Fuel used on the last complete lap (litres), 0 = not measured yet.</summary>
    public float LastLapFuel { get; internal set; }
    /// <summary>Grip factor from tyres (wear, temperature) and fuel load, 1 = new warm tyres and light car.</summary>
    public float CarGrip { get; internal set; } = 1f;
    internal float MassRatio = 1f;

    public bool InPitLane => Pit is PitPhase.InLane or PitPhase.Stopped;

    // ---- race craft: impatience, flags, signals
    /// <summary>0..1, grows while the bot is stuck behind a slower car.</summary>
    public float Impatience { get; internal set; }
    internal double BlockedSince = double.NaN;
    internal int BlockedById = -1;
    internal double LastBlockedAt;
    internal double FlashUntil;
    internal double NextFlashAt;
    public int FlashCount { get; internal set; }
    internal double YellowUntil;
    internal bool YellowHazards;
    internal double BlueFlagUntil;
    internal int BlueSide;
    /// <summary>-1 = left indicator, +1 = right indicator, 0 = off.</summary>
    public int Indicator { get; internal set; }

    // ---- human errors, contacts, damage (RaceWorld.Human.cs)
    public MistakeKind Mistake { get; internal set; }
    internal double MistakeStart, MistakeEnd, LockStart = double.NaN;
    internal float MistakeSide, MistakeSeverity, MistakeTargetOffset, MistakeDuration, SpinAngle;
    internal double SpinStandUntil, SpinClearSince = double.NaN;
    internal int SpinPhase;
    internal bool InCorner, CornerExitRolled, Overspeed, CornerSeen;
    /// <summary>Extra room beyond the track edge the car may use right now (grass), decays after a mistake.</summary>
    internal float EdgeAllowance;
    /// <summary>Visual yaw of the car body relative to its direction of travel (rad, + = towards +offset).</summary>
    public float Yaw { get; internal set; }
    internal float SteerExtraDeg, RearSlip = 1f;
    internal bool FrontLock;
    internal double MarginOverrideUntil;
    /// <summary>Stuck in a cluster of stopped cars: sorting it out.</summary>
    public bool Unstuck { get; internal set; }
    /// <summary>Emergency ghost (no collisions) until this time.</summary>
    public double GhostUntil { get; internal set; } = double.NegativeInfinity;
    public int GhostCount { get; internal set; }
    /// <summary>Seconds a car has been sitting close behind in the slipstream (pressure on this bot) and 0..1 effect of it.</summary>
    internal float PressureTime;
    public float Pressure { get; internal set; }
    /// <summary>Seconds this bot has been stuck in somebody's slipstream, and whether it's weaving right now.</summary>
    internal float DraftTime;
    public bool Weaving { get; internal set; }
    internal float WeaveCenter;

    // ---- clone of a real player (DriverRecorder recordings): drives his speed and line
    public CloneProfile? Clone { get; set; }
    /// <summary>Multiplier on the clone's speed (1 = like the player).</summary>
    public float ClonePace { get; set; } = 1f;
    /// <summary>This corner: how far from the player's average line (in standard deviations).</summary>
    internal float CloneZ;
    /// <summary>The applied share of <see cref="CloneZ"/>: changes slowly, so a new corner doesn't make the line jump.</summary>
    internal float CloneZNow;
    /// <summary>On its own line right now (a clone: the player's; a bot: its personal line), not overtaking, defending, ...: follows it exactly.</summary>
    internal bool OnOwnLine;
    /// <summary>The bot's personal line: the AI line widened (+) or narrowed (-) around the middle of the track, and its apex moved (m).</summary>
    internal float LineScale, ApexShift;
    internal bool StyleRolled;
    /// <summary>Statistics: defensive moves, cars let by on purpose.</summary>
    public int Defends { get; internal set; }
    public int LetBy { get; internal set; }
    public int GreedyExits { get; internal set; }

    // ---- how the current corner is driven (RaceWorld.Lines.cs)
    public bool PlanActive { get; internal set; }
    internal bool PlanRolled;
    internal float PlanStartS, PlanApexS, PlanEndS, PlanSign, PlanAmp, PlanPace = 1, PlanBrake = 1, PlanExitDelay, PlanBrakeMargin;
    /// <summary>This driver's version of his personality (0.6..1.4 x the style, his own bias), rolled once.</summary>
    internal float StyleScale, StyleBias;
    /// <summary>Pace added by the rubber band (+ faster, - slower).</summary>
    public float PaceBoost { get; internal set; }
    // start: when the clutch comes up, how the launch goes
    internal double LaunchAt = double.NaN;
    internal LaunchKind Launch;
    internal float LaunchSpin;
    public CornerLineKind PlanKind { get; internal set; }
    internal bool PlanLift;
    internal double PlanApexPassedAt = double.NaN, PlanLiftAt = double.NaN;
    internal float PlanLiftSpeed;
    /// <summary>Only lifting (no brake pedal) until this time: small corrections in a corner.</summary>
    internal double LiftOnlyUntil;
    /// <summary>Lateral shift from the corner plan applied to the target offset in the last Think.</summary>
    internal float LineShiftNow;
    internal float MisjudgeTowards;
    internal double LastContactAt = double.NegativeInfinity;
    internal int LastContactWith = -1;
    public int MistakeCount { get; internal set; }
    public int SpinCount { get; internal set; }
    public int ContactCount { get; internal set; }
    /// <summary>AC damage zones (front, rear, left, right, centre) in AC damage units (~impact km/h).</summary>
    public float[] DamageZones { get; } = new float[5];
    /// <summary>Suspension damage 0..1.</summary>
    public float Suspension { get; internal set; }
    /// <summary>Changes whenever the damage changes (so the server knows when to send an update).</summary>
    public int DamageVersion { get; internal set; }
    internal bool PitRepair;
    /// <summary>Nobody ahead within the high beam range: at night the bot may use its high beams.</summary>
    public bool ClearAhead { get; internal set; }
    internal double ClearAheadSince;
    /// <summary>Signal test: overrides indicator / hazards / flash for the night test.</summary>
    internal int ForcedSignal;
}

public enum MistakeKind
{
    None,
    /// <summary>Braked too late: arrives too fast, runs wide (maybe over the kerb onto the grass).</summary>
    LateBrake,
    /// <summary>Too much throttle on the exit: the rear steps out, countersteer, lift.</summary>
    Slide,
    /// <summary>Slide that turned into a spin: stops, hazards on, waits for a gap, turns round and rejoins.</summary>
    Spin,
    /// <summary>Ran wide on the exit with two wheels on the grass.</summary>
    Grass
}

public enum PitPhase
{
    None,
    /// <summary>Will turn into the pit lane at the next entry.</summary>
    Requested,
    InLane,
    Stopped
}

/// <summary>What a bot looks like to the outside world for one frame.</summary>
public readonly record struct BotPose(
    Vector3 Position,
    Vector3 Rotation,
    Vector3 Velocity,
    float Speed,
    float WheelAngleDeg,
    int Gear,
    int Rpm,
    bool Braking,
    float NormalizedPosition,
    byte Throttle = 0,
    bool Hazards = false,
    int Indicator = 0,
    bool Flash = false,
    bool HighBeam = false,
    float FrontTyreFactor = 1,
    float RearTyreFactor = 1);

public enum LaunchKind { Clean, Wheelspin, Bog }

public sealed class RaceWorldSettings
{
    /// <summary>Distance along the racing line where the start/finish line is.</summary>
    public float StartLineS { get; set; } = 0;
    /// <summary>Distance kept to the track edge (m).</summary>
    public float EdgeMargin { get; set; } = 0.4f;
    /// <summary>Every bot drives its own line (by personality and driver) instead of everybody on the AI line.</summary>
    public bool PersonalLines { get; set; } = true;
    /// <summary>Starts with reaction times, clutch, wheelspin and bogged launches instead of a perfect launch for everybody.</summary>
    public bool RealisticStart { get; set; } = true;
    /// <summary>
    /// Rubber band to the players (0..1 = 0..100 %): bots ahead of the nearest player get slower, bots behind faster, growing with the
    /// distance (full at <see cref="RubberBandDistance"/>) and over time (full after <see cref="RubberBandTime"/>), like CSP's.
    /// </summary>
    public float RubberBand { get; set; }
    /// <summary>At most this much slower (share of pace) ahead of the players / faster behind them, at RubberBand 1.</summary>
    public float RubberBandAhead { get; set; } = 0.03f;
    public float RubberBandBehind { get; set; } = 0.06f;
    public float RubberBandDistance { get; set; } = 200f;
    public float RubberBandTime { get; set; } = 60f;
    /// <summary>Extra lateral space kept to other cars (m).</summary>
    public float SideMargin { get; set; } = 0.5f;
    /// <summary>Lateral space kept to players (m): their position arrives with a delay and they don't drive exactly.</summary>
    public float PlayerSideMargin { get; set; } = 1.0f;
    /// <summary>A player counts as alongside while he overlaps within this many metres (m), bots then leave him room.</summary>
    public float PlayerOverlap { get; set; } = 3.0f;
    /// <summary>Safety car: all bots drive slowly (<see cref="SafetyCarSpeed"/>), don't overtake and weave on the straights to keep their tyres warm.</summary>
    public bool SafetyCar { get; set; }
    /// <summary>Top speed under the safety car (m/s).</summary>
    public float SafetyCarSpeed { get; set; } = 100 / 3.6f;
    /// <summary>Use the speed recorded in fast_lane.ai as an upper limit, scaled by this factor. 0 = off.</summary>
    public float SpeedHintScale { get; set; } = 0f;
    /// <summary>Height of the track's AC_START_x / AC_PIT_x dummies above the ground (m), subtracted when parking in the pit box.</summary>
    public float SpotHeightOffset { get; set; }
    /// <summary>High beams only when nobody is within this distance ahead (m).</summary>
    public float HighBeamRange { get; set; } = 85f;
    /// <summary>Fine tuning of the height of cars parked in the pit box (m, + = higher).</summary>
    public float ParkHeightAdjust { get; set; }
    /// <summary>Lift the car this far above the line (m).</summary>
    public float HeightOffset { get; set; } = 0f;
    public float CoolDownPace { get; set; } = 0.6f;
    /// <summary>Respect the track's ai_hints.ini (slower sections / max speeds), like the Kunos AI does.</summary>
    public bool UseTrackHints { get; set; } = true;
    /// <summary>Overall grip of the track surface (dynamic track grip, rain). 1 = dry, fully rubbered in.</summary>
    public float GripFactor { get; set; } = 1f;
    /// <summary>Maximum share of aero drag removed when directly behind another car (0 = no slipstream).</summary>
    public float SlipstreamStrength { get; set; } = 0.35f;
    /// <summary>Lapped bots move aside and indicate when the leaders come through (races only).</summary>
    public bool BlueFlags { get; set; }
    /// <summary>Slow down, no overtaking and hazard lights before a stopped / crawling car.</summary>
    public bool YellowFlags { get; set; } = true;
    /// <summary>Seconds stuck behind a slower car until a bot is fully impatient (0 = never).</summary>
    public float ImpatienceTime { get; set; } = 25f;
    /// <summary>No flashing of the lights in the first seconds of a race (everybody is bunched up).</summary>
    public float FlashStartDelay { get; set; } = 90f;
    /// <summary>Flash the headlights with the pit limiter on (like real GT3 cars).</summary>
    public bool PitLimiterFlash { get; set; } = true;
    /// <summary>Race start time (for the yellow flag detection, which is off during the start).</summary>
    public double RaceStartTime { get; set; } = double.NegativeInfinity;
    /// <summary>Slipstream range (m).</summary>
    public float SlipstreamRange { get; set; } = 40f;
    public int Seed { get; set; } = Environment.TickCount;

    // ---- endurance
    /// <summary>Fuel consumption multiplier (server FUEL_RATE / 100). 0 = no fuel use.</summary>
    public float FuelRate { get; set; } = 1f;
    /// <summary>Tyre wear multiplier (server TYRE_WEAR_RATE / 100). 0 = no wear.</summary>
    public float TyreWearRate { get; set; } = 1f;
    /// <summary>Virtual km per real km at average load (Kunos virtual km grow with tyre slip).</summary>
    public float TyreWearScale { get; set; } = 0.15f;
    /// <summary>Bots stop for new tyres when the grip of the worn tyres drops below this.</summary>
    public float TyreChangeGrip { get; set; } = 0.95f;
    public bool PitStops { get; set; } = true;
    /// <summary>Pit lane speed limit (m/s).</summary>
    public float PitSpeedLimit { get; set; } = 80 / 3.6f;
    /// <summary>Mandatory pit stop between these laps (race), 0/0 = none.</summary>
    public int PitWindowStart { get; set; }
    public int PitWindowEnd { get; set; }
    /// <summary>Litres a bot keeps as reserve when planning.</summary>
    public float FuelReserve { get; set; } = 2f;

    // ---- human behaviour (each can be switched off)
    /// <summary>Use <see cref="DriverProfile.Errors"/>: late/early braking, too much throttle on the exit.</summary>
    public bool HumanErrors { get; set; } = true;
    /// <summary>A bad slide can end in a spin.</summary>
    public bool Spins { get; set; } = true;
    /// <summary>Bots may run wide with wheels on the grass (beyond the track edge).</summary>
    public bool GrassMoments { get; set; } = true;
    /// <summary>Imprecise lines of weaker bots: missed apexes, early turn-in, running wide, early braking, hesitant throttle.</summary>
    public bool LineErrors { get; set; } = true;
    /// <summary>How far beyond the track edge (m, car centre) a bot may get.</summary>
    public float GrassAllowance { get; set; } = 1.2f;
    /// <summary>Light touches between bots in close fights.</summary>
    public bool BotContacts { get; set; } = true;
    /// <summary>Damage from contacts (slower car, repaired in the pits).</summary>
    public bool Damage { get; set; } = true;
    /// <summary>Damage multiplier (server DAMAGE_MULTIPLIER / 100).</summary>
    public float DamageRate { get; set; } = 1f;

    // ---- rain
    /// <summary>Track wetness and standing water (0..1, from the server's weather) and how hard it rains right now.</summary>
    public float Wetness { get; set; }
    /// <summary>Air and track temperature (°C), for the tyre temperatures.</summary>
    public float AmbientTemp { get; set; } = 20;
    public float RoadTemp { get; set; } = 28;
    /// <summary>The current session is a race (no out-lap tyre warming, etc.).</summary>
    public bool IsRace { get; set; } = true;
    public float Water { get; set; }
    public float RainIntensity { get; set; }
    /// <summary>Server extra_cfg RainTrackGripReductionPercent (0..0.5): the server already lowers the grip for everybody (slicks).</summary>
    public float ServerRainReduction { get; set; }
    /// <summary>Grip lost on a wet track (1 = normal).</summary>
    public float RainGripLoss { get; set; } = 1f;
    /// <summary>On slicks in the wet the bots drive more carefully, make more mistakes and can aquaplane.</summary>
    public bool RainCaution { get; set; } = true;
}

/// <summary>
/// The racing AI. Pure logic without any AssettoServer dependencies, so it can be tested offline.
/// Bots are kinematic: they move along the racing line with a lateral offset and a speed that is limited by
/// a simple grip/aero model, by the traffic around them and by their driver profile.
/// </summary>
public sealed partial class RaceWorld
{
    public RacingLine Line { get; }
    public RaceWorldSettings Settings { get; }
    public List<RaceBot> Bots { get; } = [];
    public List<ExternalCar> Externals { get; } = [];

    /// <summary>(bot, lap time in seconds)</summary>
    public event Action<RaceBot, float>? LapCompleted;
    /// <summary>(bot, stop time in seconds, litres added, tyres changed)</summary>
    public event Action<RaceBot, float, float, bool>? PitStopCompleted;
    public PitLane? PitLane { get; set; }

    /// <summary>Debug: bot id whose decisions are written to <see cref="Trace"/> twice a second.</summary>
    public int TraceBotId { get; set; } = -1;
    public Action<string>? Trace { get; set; }
    private double _lastTrace;

    private readonly Random _rng;
    private readonly List<Neighbor> _neighbors = [];
    private double _now;

    private struct Neighbor
    {
        public int Id;
        public bool IsBot;
        public float S, Offset, Speed, Length, Width;
        /// <summary>Forward acceleration (players only, bots: 0).</summary>
        public float Accel;
        /// <summary>Race progress in metres (laps * length + distance from the start line).</summary>
        public float Progress;
        public RaceBot? Bot;
        public ExternalCar? External;
    }

    public RaceWorld(RacingLine line, RaceWorldSettings? settings = null)
    {
        Line = line;
        Settings = settings ?? new RaceWorldSettings();
        _rng = new Random(Settings.Seed);
    }

    // ------------------------------------------------------------------ setup

    /// <summary>Places bots on a two-wide staggered grid behind the start line (only used when no real grid spots are known).</summary>
    public void PlaceOnGrid(IReadOnlyList<RaceBot> order, int firstSlot = 0, float rowSpacing = 8f, float firstRowGap = 6f, float lateralSpacing = 2.6f)
    {
        for (int k = 0; k < order.Count; k++)
        {
            int slot = firstSlot + k;
            PlaceAtGridSlot(order[k], slot, rowSpacing, firstRowGap, lateralSpacing);
        }
    }

    public void PlaceAtGridSlot(RaceBot bot, int slot, float rowSpacing = 8f, float firstRowGap = 6f, float lateralSpacing = 2.6f)
    {
        float back = firstRowGap + slot * rowSpacing * 0.5f;
        float side = (slot % 2 == 0 ? 1 : -1) * lateralSpacing / 2;
        PlaceAt(bot, Settings.StartLineS - back, side, BotPhase.Grid);
    }

    /// <summary>Places a bot at an exact world position (e.g. a real AC_GRID_x spot).</summary>
    public void PlaceAtWorld(RaceBot bot, Vector3 position, BotPhase phase)
    {
        var p = Line.Project(position);
        float s = p.S;
        // grid spots are behind the start line: keep the distance negative so the first crossing starts lap 1
        float rel = Line.Delta(Settings.StartLineS, s);
        PlaceAt(bot, Settings.StartLineS + rel, p.Offset, phase);
    }

    public void PlaceAt(RaceBot bot, double distance, float offset, BotPhase phase)
    {
        bot.Distance = distance;
        bot.Offset = offset;
        bot.TargetOffset = offset;
        bot.Speed = 0;
        bot.Phase = phase;
        bot.OvertakeTargetId = -1;
        bot.LapIndex = (long)Math.Floor((distance - Settings.StartLineS) / Line.Length);
        bot.TimingValid = false;
        bot.StartCrossed = false;
        bot.LapsCompleted = 0;
        bot.LastLapSeconds = 0;
        bot.BestLapSeconds = float.MaxValue;
        bot.TotalTime = 0;
        bot.Pit = PitPhase.None;
        bot.PitS = 0;
        bot.LastDecisionLap = long.MinValue;
        bot.PlanActive = false;
        bot.PlanRolled = false;
        bot.LineShiftNow = 0;
    }

    /// <summary>Parks a bot at a fixed position (e.g. its pit box). It is no longer an obstacle and does not drive.</summary>
    public void Park(RaceBot bot, Vector3 position, Vector3 forward)
    {
        bot.Phase = BotPhase.Parked;
        bot.ParkPosition = OnGround(position);
        bot.ParkForward = forward.LengthSquared() > 0.01f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        bot.Speed = 0;
        bot.OvertakeTargetId = -1;
    }

    /// <summary>
    /// Puts a track spot (AC_PIT_x dummy) onto the ground. Kunos places these dummies above the surface (the car is dropped from there);
    /// <see cref="RaceWorldSettings.SpotHeightOffset"/> is measured on the start grid spots, where the ground is the racing line.
    /// </summary>
    public Vector3 OnGround(Vector3 p)
    {
        // same height as during a pit stop in that box (pit lane surface), so parked and stopping cars sit alike
        if (PitLane is { } lane)
        {
            var (s, off) = lane.Project(p);
            var q = lane.PositionAt(s, off);
            if (new Vector2(p.X - q.X, p.Z - q.Z).Length() < 2f && MathF.Abs(q.Y - p.Y) < 3f)
                return new Vector3(p.X, q.Y + Settings.ParkHeightAdjust, p.Z);
        }
        return p - Vector3.UnitY * (Settings.SpotHeightOffset - Settings.ParkHeightAdjust);
    }

    /// <summary>Median height of the AC_START_x dummies above the racing line (the offset of the track's spots).</summary>
    public static float MeasureSpotHeight(RacingLine line, IEnumerable<Vector3> startSpots)
    {
        var h = startSpots.Select(p => line.Project(p).Height).Where(x => x > -0.5f && x < 3f).OrderBy(x => x).ToList();
        return h.Count == 0 ? 0f : h[h.Count / 2];
    }

    /// <summary>Spreads bots around the track (practice / qualifying), rolling at speed.</summary>
    public void SpreadOnTrack(IReadOnlyList<RaceBot> bots, double now)
    {
        _now = now;
        for (int k = 0; k < bots.Count; k++)
        {
            float s = Line.WrapS(Settings.StartLineS + 300 + k * Line.Length / Math.Max(1, bots.Count));
            PlaceAt(bots[k], s, 0, BotPhase.Racing);
            bots[k].Speed = MathF.Min(LineSpeedLimit(bots[k], 0, 0) * 0.8f, 40);
            bots[k].LapStartTime = now;
        }
    }

    /// <summary>Lights out: all bots on the grid start racing. Lap 1 is timed from this moment.</summary>
    public void StartRace(double now)
    {
        _now = now;
        foreach (var bot in Bots)
        {
            if (bot.Phase != BotPhase.Grid) continue;
            bot.Phase = BotPhase.Racing;
            bot.LapStartTime = now;
            bot.RaceStartTime = now;
            bot.TimingValid = true;
            // reaction time: 0.2-0.6 s, quicker for drivers who go for the start, slower for careful and inconsistent ones
            float launch = bot.Clone != null ? 0 : Math.Clamp(bot.Driver.Personality.Launch, -1, 1);
            double reaction = Math.Clamp(0.30 - 0.08 * launch + NextGaussian() * 0.07 + (1 - bot.Driver.Consistency) * 0.3, 0.17, 0.8);
            bot.LaunchAt = now + reaction;
            bot.CautiousUntil = bot.LaunchAt;
            // the launch itself: clean, too much throttle (wheelspin), or too little (bogs down)
            double r = _rng.NextDouble();
            double pSpin = Settings.RealisticStart ? 0.18 + 0.32 * MathF.Max(0, launch) : 0;
            double pBog = Settings.RealisticStart ? 0.06 + 0.10 * MathF.Max(0, -launch) : 0;
            bot.Launch = r < pSpin ? LaunchKind.Wheelspin : r < pSpin + pBog ? LaunchKind.Bog : LaunchKind.Clean;
            bot.LaunchSpin = 0.3f + 0.7f * _rng.NextSingle();
        }
    }

    private readonly List<float> _playerProgress = [];

    /// <summary>Rubber band: pace offsets towards the nearest player in the race (see <see cref="RaceWorldSettings.RubberBand"/>).</summary>
    private void UpdateRubberBand(float dt)
    {
        _playerProgress.Clear();
        if (Settings.RubberBand > 0 && Settings.IsRace)
            foreach (var e in Externals)
                if (e.Valid && !IsInPitLane(e))
                    _playerProgress.Add(e.Laps * Line.Length + Line.WrapS(e.S - Settings.StartLineS));
        float rate = MathF.Max(Settings.RubberBandAhead, Settings.RubberBandBehind) / MathF.Max(1, Settings.RubberBandTime) * dt;
        foreach (var bot in Bots)
        {
            float target = 0;
            if (_playerProgress.Count > 0 && bot.Phase == BotPhase.Racing && !bot.InPitLane && bot.Clone == null)
            {
                float mine = bot.LapsCompleted * Line.Length + Line.WrapS((float)bot.Distance - Settings.StartLineS);
                float d = float.MaxValue;
                foreach (var p in _playerProgress) if (MathF.Abs(mine - p) < MathF.Abs(d)) d = mine - p;
                float k = Math.Clamp(MathF.Abs(d) / MathF.Max(1, Settings.RubberBandDistance), 0, 1);
                target = Settings.RubberBand * (d > 0 ? -Settings.RubberBandAhead * k : Settings.RubberBandBehind * k);
                // never quicker than a little over the car's limit
                target = MathF.Min(target, MathF.Max(0, 1.03f - bot.Driver.Pace));
            }
            bot.PaceBoost += Math.Clamp(target - bot.PaceBoost, -rate, rate);
        }
    }

    /// <summary>
    /// Traction at the start (share of the full-throttle acceleration): the clutch bites over a third of a second, wheelspin costs
    /// grip for a second or two, a bogged launch takes a moment to pick up. 1 = normal driving.
    /// </summary>
    private float LaunchTraction(RaceBot me)
    {
        if (!Settings.RealisticStart || double.IsNaN(me.LaunchAt) || me.LapsCompleted > 0 || me.Speed > 30) return 1;
        float t = (float)(_now - me.LaunchAt);
        if (t < 0 || t > 3.5f) return 1;
        switch (me.Launch)
        {
            case LaunchKind.Wheelspin:
            {
                float dur = 0.7f + 1.1f * me.LaunchSpin;
                if (t < dur) return 0.78f - 0.12f * me.LaunchSpin + 0.1f * (t / dur);
                return 0.96f;
            }
            case LaunchKind.Bog:
                return t < 0.8f ? 0.35f + 0.35f * t / 0.8f : 0.93f;
            default:
                return t < 0.35f ? 0.55f + 0.45f * t / 0.35f : 0.98f;
        }
    }

    /// <summary>Rear wheels spinning at the start (m/s faster than the car), for the clients' view and sound.</summary>
    private float LaunchSlipSpeed(RaceBot me)
    {
        if (!Settings.RealisticStart || me.Launch != LaunchKind.Wheelspin || double.IsNaN(me.LaunchAt) || me.LapsCompleted > 0) return 0;
        float t = (float)(_now - me.LaunchAt), dur = 0.7f + 1.1f * me.LaunchSpin;
        return t < 0 || t > dur ? 0 : (4f + 6f * me.LaunchSpin) * (1 - t / dur);
    }

    // ------------------------------------------------------------------ external cars

    public ExternalCar GetOrAddExternal(int id)
    {
        foreach (var e in Externals)
            if (e.Id == id) return e;
        var car = new ExternalCar { Id = id };
        Externals.Add(car);
        return car;
    }

    public void RemoveExternal(int id) => Externals.RemoveAll(e => e.Id == id);

    /// <summary>Feeds the current state of a human car.</summary>
    /// <param name="at">World time of this state (the player's timestamp); NaN = now. The bots extrapolate from there
    /// with speed and acceleration, so they see where the player is at every step, not where his last packet said.</param>
    public void UpdateExternal(ExternalCar car, Vector3 position, Vector3 velocity, bool active = true, double at = double.NaN)
    {
        if (!active)
        {
            car.Valid = false;
            car.PrevAt = double.NaN;
            return;
        }

        if (double.IsNaN(at)) at = _now;
        var p = Line.Project(position, car.HintIndex);
        car.HintIndex = p.Index;
        car.S = p.S;
        car.Offset = p.Offset;
        car.Speed = Vector3.Dot(velocity, Line.Forward[p.Index]);
        var lat = Line.LateralAt(p.S);
        car.LateralSpeed = Vector3.Dot(velocity, lat);
        // acceleration from the speed change between two updates (smoothed; braking shows up within ~2 packets)
        if (!double.IsNaN(car.PrevAt) && at - car.PrevAt > 0.015 && at - car.PrevAt < 0.5)
        {
            float a = Math.Clamp((car.Speed - car.PrevSpeed) / (float)(at - car.PrevAt), -25f, 15f);
            car.Accel = car.Accel * 0.4f + a * 0.6f;
        }
        else if (double.IsNaN(car.PrevAt)) car.Accel = 0;
        if (double.IsNaN(car.PrevAt) || at - car.PrevAt > 0.015)
        {
            car.PrevAt = at;
            car.PrevSpeed = car.Speed;
        }
        car.At = at;
        int i = p.Index;
        // only relevant while on (or near) the track surface
        car.Valid = p.Offset > -Line.RoomMinus[i] - 4 && p.Offset < Line.RoomPlus[i] + 4 && MathF.Abs(p.Height) < 6;
    }

    /// <summary>A human car touched this bot. The bot gives way: loses some speed and moves away.</summary>
    public void OnContact(RaceBot bot, Vector3 otherPosition, float impactSpeed, ExternalCar? other = null)
    {
        if (!bot.OnTrack) return;
        bot.Contacts++;
        // where the other car is now (the collision message is a little old), else where it was
        float oS, oOff, oSpeed;
        if (other is { Valid: true } && !double.IsNaN(other.At))
        {
            float dt = Math.Clamp((float)(_now - other.At), 0, 0.35f);
            oS = Line.WrapS(other.S + other.Speed * dt);
            oOff = other.Offset + other.LateralSpeed * dt;
            oSpeed = MathF.Max(0, other.Speed);
        }
        else
        {
            var p = Line.Project(otherPosition, Line.IndexAt((float)bot.Distance));
            oS = p.S;
            oOff = p.Offset;
            oSpeed = bot.Speed;
        }
        float dOff = bot.Offset - oOff;
        float ds = Line.Delta(oS, Line.WrapS((float)bot.Distance)); // > 0: the bot is ahead
        float impact = Math.Clamp(impactSpeed, 0, 30);
        bool side = MathF.Abs(dOff) > 0.6f * bot.Car.Width && MathF.Abs(ds) < bot.Car.Length;
        // no jumps: everything changes speeds, so the car moves on smoothly on the players' screens
        if (side)
        {
            bot.LateralSpeed = MathF.Sign(dOff) * (0.6f + 0.08f * impact);
            bot.Speed *= 0.99f;
        }
        else if (ds > 0)
        {
            // hit from behind: pushed forward (it takes some of the other car's speed), a little wobble
            bot.Speed = MathF.Max(bot.Speed, MathF.Min(oSpeed, bot.Speed + 0.4f * impact + 0.3f));
            Wobble(bot, Math.Clamp(impact / 15f, 0.1f, 0.4f));
        }
        else
        {
            // the bot ran into the other car: it slows down to the other car's speed minus a bit
            bot.Speed = MathF.Max(0, MathF.Min(bot.Speed - 0.6f * impact, oSpeed - 0.5f));
        }
        bot.CautiousUntil = _now + 3;
        bot.OvertakeTargetId = -1;
    }

    // ------------------------------------------------------------------ simulation

    private double _lastAdvance = double.NaN;

    /// <summary>Largest simulation step; longer frames are split into several steps.</summary>
    /// <summary>
    /// Simulation step. 10 ms: bots touch less (overlaps resolved earlier), leave the track less and move more smoothly than with
    /// 25 ms; the calibration uses the same step so the lap times match the strength setting.
    /// </summary>
    public const float DefaultStep = 0.01f;
    public float MaxStep { get; set; } = DefaultStep;

    /// <summary>
    /// Advances the simulation to <paramref name="now"/> (seconds), splitting long frames into small steps.
    /// Frames longer than 0.5 s (server hiccup) are clamped so bots don't jump.
    /// </summary>
    public void Advance(double now)
    {
        if (double.IsNaN(_lastAdvance) || now < _lastAdvance)
        {
            _lastAdvance = now;
            _now = now;
            return;
        }

        double frame = Math.Min(now - _lastAdvance, 0.5);
        double t = now - frame;
        int steps = Math.Max(1, (int)Math.Ceiling(frame / MaxStep));
        float dt = (float)(frame / steps);
        for (int i = 0; i < steps; i++)
        {
            t += dt;
            Step(dt, t);
        }
        _lastAdvance = now;
    }

    private float _stepDt = 0.02f;

    // ---- diagnostics: where do cars jump (position change that doesn't match their velocity)? Used by RaceAiTool sim --jumps.
    public bool MeasureJumps { get; set; }
    public readonly Dictionary<string, (int Count, float Max, double Sum)> JumpStats = new();
    private Vector3[] _jumpPos = [];
    private Vector3[] _jumpVel = [];

    private void JumpSnapshot()
    {
        if (_jumpPos.Length != Bots.Count) { _jumpPos = new Vector3[Bots.Count]; _jumpVel = new Vector3[Bots.Count]; }
        for (int i = 0; i < Bots.Count; i++)
        {
            var p = GetPose(Bots[i]);
            _jumpPos[i] = p.Position;
            _jumpVel[i] = p.Velocity;
        }
    }

    // the overlap phase is measured against the positions right after driving (no time passes in between)
    private void JumpSnapshotAfterDrive(float dt)
    {
        for (int i = 0; i < Bots.Count; i++)
        {
            _jumpPos[i] = GetPose(Bots[i]).Position;
            _jumpVel[i] = Vector3.Zero;
        }
    }

    private void JumpCheck(string phase, float dt)
    {
        for (int i = 0; i < Bots.Count; i++)
        {
            var b = Bots[i];
            if (b.Phase is not (BotPhase.Racing or BotPhase.CoolDown)) continue;
            var p = GetPose(b);
            var ev = p.Position - (_jumpPos[i] + _jumpVel[i] * dt);
            float err = ev.Length();
            if (err < 0.05f || err > 30) continue;
            string cause = phase == "overlap" ? "overlap" : b.InPitLane ? "pit" : b.Mistake != MistakeKind.None ? "mistake-" + b.Mistake : "drive";
            if (cause == "drive")
            {
                float sNow = Line.WrapS((float)b.Distance);
                float along = MathF.Abs(Vector3.Dot(ev, Line.ForwardAt(sNow))), side = MathF.Abs(Vector3.Dot(ev, Line.LateralAt(sNow)));
                cause = along > side ? (MathF.Abs(ev.Y) > along ? "drive-vertical" : "drive-along") : (MathF.Abs(ev.Y) > side ? "drive-vertical" : "drive-side");
                if (cause == "drive-side") cause = b.ClampedAt == _now ? "drive-side-clamp" : MathF.Abs(b.Offset) < 1 ? "drive-side-off<1m" : MathF.Abs(b.Offset) < 3 ? "drive-side-off1-3m" : "drive-side-off>3m";
            }
            JumpStats.TryGetValue(cause, out var st);
            JumpStats[cause] = (st.Count + 1, MathF.Max(st.Max, err), st.Sum + err);
        }
    }

    public void Step(float dt, double now)
    {
        _now = now;
        _stepDt = dt;
        BuildNeighbors();
        FindIncidents();
        WatchStoppedCars();
        UpdateRubberBand(dt);
        if (MeasureJumps) JumpSnapshot();

        foreach (var bot in Bots)
        {
            switch (bot.Phase)
            {
                case BotPhase.Racing:
                case BotPhase.CoolDown:
                    if (bot.Speed < 1f)
                    {
                        if (double.IsNaN(bot.StoppedSince)) bot.StoppedSince = now;
                    }
                    else if (bot.Speed > 5f)
                    {
                        bot.StoppedSince = double.NaN;
                    }
                    UpdateStuck(bot);
                    if (bot.InPitLane)
                    {
                        PitStep(bot, dt);
                    }
                    else
                    {
                        Think(bot);
                        Integrate(bot, dt);
                        CheckPitEntry(bot);
                    }
                    UpdateCarCondition(bot, dt);
                    break;
                case BotPhase.Grid:
                    bot.Speed = 0;
                    break;
            }
        }

        if (MeasureJumps) { JumpCheck("drive", dt); JumpSnapshotAfterDrive(dt); }
        ResolveOverlaps();
        if (MeasureJumps) JumpCheck("overlap", 0);

        foreach (var bot in Bots)
        {
            if (bot.Phase is BotPhase.Racing or BotPhase.CoolDown)
                UpdateTiming(bot);
        }
    }

    private readonly List<float> _incidents = [];

    private void BuildNeighbors()
    {
        _neighbors.Clear();
        _incidents.Clear();
        foreach (var b in Bots)
        {
            if (!b.OnTrack) continue;
            _neighbors.Add(new Neighbor
            {
                Id = b.Id, IsBot = true, Bot = b,
                S = Line.WrapS((float)b.Distance), Offset = b.Offset, Speed = b.Speed,
                Length = b.Car.Length, Width = b.Car.Width,
                Progress = b.LapsCompleted * Line.Length + Line.WrapS((float)b.Distance - Settings.StartLineS)
            });
        }
        foreach (var e in Externals)
        {
            if (!e.Valid) continue;
            // where the player is at this step: his last state moved on with his speed and braking/acceleration
            float dt = double.IsNaN(e.At) ? 0 : Math.Clamp((float)(_now - e.At), -0.1f, 0.35f);
            float v = MathF.Max(0, e.Speed + e.Accel * dt);
            float s = e.S + (e.Speed + v) * 0.5f * dt;
            _neighbors.Add(new Neighbor
            {
                Id = e.Id, IsBot = false, External = e,
                S = Line.WrapS(s), Offset = e.Offset + e.LateralSpeed * dt, Speed = v, Accel = e.Accel,
                Length = e.Length, Width = e.Width,
                Progress = e.Laps * Line.Length + Line.WrapS(e.S - Settings.StartLineS)
            });
        }
    }

    private void FindIncidents()
    {
        if (!Settings.YellowFlags || _now - Settings.RaceStartTime < 20) return;
        foreach (var n in _neighbors)
        {
            if (n.Speed > 8) continue;
            if (n.IsBot && (n.Bot!.Phase != BotPhase.Racing || n.Bot.InPitLane)) continue;
            if (!n.IsBot && IsInPitLane(n.External!)) continue;
            _incidents.Add(n.S);
        }
    }

    private bool IsInPitLane(ExternalCar e)
    {
        var lane = PitLane;
        if (lane == null) return false;
        // the pit lane leaves the track between these racing line positions
        float from = lane.TrackSAt(lane.LimiterStart, Line), to = lane.TrackSAt(lane.LimiterEnd, Line);
        float d = Line.Delta(from, e.S);
        float len = Line.Delta(from, to);
        if (d < 0 || d > len) return false;
        int i = lane.IndexAt(lane.LimiterStart + d);
        return MathF.Abs(e.Offset - lane.TrackOffset[i]) < 3;
    }

    /// <summary>
    /// Highest speed the bot may have right now so it can still make every corner within braking distance,
    /// assuming it stays at <paramref name="offset"/>.
    /// </summary>
    public float LineSpeedLimit(RaceBot bot, float offset, float extraPaceLoss)
    {
        var car = bot.Car;
        float skill = bot.Driver.Pace - extraPaceLoss + bot.PaceNoise + bot.PaceBoost;
        if (_now < bot.MistakeUntil) skill -= 0.08f; // braked too early / too carefully
        if (Settings.RainCaution) skill -= WetFactor() * 0.05f; // careful in the wet
        float phys = Settings.GripFactor * bot.CarGrip * (bot.Phase == BotPhase.CoolDown ? Settings.CoolDownPace : 1);
        float pace = DriverProfile.CornerSkill(skill) * phys;
        float brakePace = DriverProfile.BrakeSkill(skill) * phys;

        float v = MathF.Max(bot.Speed, 10);
        float horizon = v * v / (2 * car.BrakeAt(0, brakePace, bot.MassRatio)) + 40;
        float s0 = Line.WrapS((float)bot.Distance);
        float best = car.TopSpeed * (1 + 0.12f * bot.Draft) * (bot.Phase == BotPhase.CoolDown ? Settings.CoolDownPace : 1) / MathF.Sqrt(DamageDrag(bot));
        // braked too late: plans with more braking than the car has
        float lateBrake = bot.Mistake == MistakeKind.LateBrake ? 1.2f + 0.3f * bot.MistakeSeverity : 1f;
        float step = MathF.Max(Line.Spacing, 2f);
        bool plan = bot.PlanActive && bot.Phase == BotPhase.Racing;
        bool shifted = plan && (bot.LineShiftNow != 0 || bot.PlanKind != CornerLineKind.Clean);

        for (float d = 0; d <= horizon; d += step)
        {
            int i = Line.IndexAt(s0 + d);
            float k = Line.Curvature[i];
            float off = offset;
            // a clone will be on the player's line there, not where it is now
            if (bot.Clone is { } lc && bot.Phase == BotPhase.Racing && d > 5) off = lc.OffsetAt(s0 + d, Line.Length);
            // a bot on its personal line: where it will be there. Its line is its way of driving the corner, not a slower
            // path: the radius changes with the offset, the line's own bend is taken as part of the driver's style (he adapts to it)
            else if (bot.OnOwnLine && (bot.LineScale != 0 || bot.ApexShift != 0) && bot.Phase == BotPhase.Racing && d > 2)
                off = PersonalLineOffset(bot, s0 + d);
            bool inPlan = plan && InPlannedCorner(bot, s0 + d);
            if (shifted && inPlan) off += CornerShift(bot, s0 + d);
            // radius changes when driving off the line: positive curvature turns towards +offset (inside)
            if (MathF.Abs(k) > 1e-5f)
            {
                float r = 1f / MathF.Abs(k) - off * MathF.Sign(k);
                k = MathF.Sign(k) / MathF.Max(r, 4f);
            }
            // a line that wanders across the road bends the path on top of the corner itself
            if (shifted && inPlan) k += CornerShiftCurvature(bot, s0 + d);

            float vLim = car.CornerLimit(k, Line.VerticalCurvature[i], inPlan ? pace * bot.PlanPace : pace, bot.MassRatio);
            if (bot.Clone is { } clone && bot.Phase == BotPhase.Racing)
            {
                // the player's own speed here (scaled with the grip we have now), never more than the car can do on this line
                float cv = clone.SpeedAt(s0 + d, Line.Length) * bot.ClonePace * MathF.Sqrt(MathF.Max(0.3f, phys));
                // the player really drove that fast here: our grip model is only an estimate (downforce, real car), so it may
                // only stop the clone when it's far off
                vLim = MathF.Min(cv, car.CornerLimit(k, Line.VerticalCurvature[i], 1.25f * phys, bot.MassRatio));
            }
            else if (Settings.UseTrackHints)
                vLim = MathF.Min(vLim * MathF.Sqrt(Line.HintFactor[i]), Line.MaxSpeed[i]); // hint scales the usable grip
            if (Settings.SpeedHintScale > 0 && Line.SpeedHint[i] > 5)
                vLim = MathF.Min(vLim, Line.SpeedHint[i] * Settings.SpeedHintScale);

            if (vLim >= best) continue;
            // attacking drivers brake a little later
            float decel = car.BrakeAt((vLim + v) * 0.5f, brakePace, bot.MassRatio) * (bot.OvertakeTargetId >= 0 ? 0.9f + 0.07f * bot.Driver.Aggression : 0.9f) * lateBrake;
            // small speed drops (fast kinks) are taken with a gentle, early brush of the brakes, big ones with hard braking
            decel *= Math.Clamp(0.4f + 0.6f * (v - vLim) / 14f, 0.4f, 1f);
            // late brakers brake later (even more when attacking), smooth drivers earlier and softer (lift and coast)
            var pers = bot.Driver.Personality;
            decel *= (1 + 0.05f * pers.BrakeBehavior * (bot.OvertakeTargetId >= 0 && pers.BrakeBehavior > 0 ? 1.6f : 1f)) * (1 - 0.15f * pers.Smoothness);
            if (inPlan || (plan && Line.Delta(s0 + d, bot.PlanStartS) >= 0)) decel *= bot.PlanBrake; // this corner's braking point
            if (bot.Clone is { } bc && bot.Phase == BotPhase.Racing && d > 1)
            {
                // a clone brakes where and as hard as the player did (his speed trace already is his braking curve)
                float pv0 = bc.SpeedAt(s0, Line.Length), pv1 = bc.SpeedAt(s0 + d, Line.Length);
                float playerDecel = (pv0 * pv0 - pv1 * pv1) / (2 * d);
                if (playerDecel > decel) decel = MathF.Min(playerDecel * 1.02f, car.BrakeAt(v, 1.1f, bot.MassRatio));
            }
            // braking too early: at corner speed already some metres before the corner (only for real braking zones)
            float margin = 0;
            if (bot.Phase == BotPhase.Racing && v - vLim > 5)
                margin = plan && (inPlan || Line.Delta(s0 + d, bot.PlanStartS) >= 0) ? bot.PlanBrakeMargin : DriverProfile.BrakeMargin(skill);
            float allowed = MathF.Sqrt(vLim * vLim + 2 * decel * MathF.Max(0, d - margin));
            if (allowed < best) best = allowed;
        }

        return best;
    }

    private void Think(RaceBot me)
    {
        if (me.Mistake == MistakeKind.Spin)
        {
            me.TargetSpeed = 0;
            me.Indicator = 0;
            return;
        }
        float myS = Line.WrapS((float)me.Distance);
        float half = me.Car.Width / 2;
        var (roomMinus, roomPlus) = Line.MinRoom(myS, 25);
        float edge = EdgeMarginFor(me);
        float minOff = -roomMinus + half + edge;
        float maxOff = roomPlus - half - edge;
        if (minOff > maxOff) minOff = maxOff = (minOff + maxOff) / 2;

        // driver form varies a little over time (less consistent drivers vary more)
        if (_now >= me.PaceNoiseUntil)
        {
            float sigma = (1 - me.Driver.Consistency) * 0.05f;
            me.PaceNoise = Math.Clamp((float)NextGaussian() * sigma, -2.5f * sigma, 1.0f * sigma);
            me.PaceNoiseUntil = _now + 20 + _rng.NextDouble() * 20;
        }

        float draft = 0;
        float sideMin = minOff, sideMax = maxOff; // constraints from cars alongside
        Neighbor? ahead = null;
        float aheadGap = float.MaxValue;
        Neighbor? behind = null;
        float behindGap = float.MaxValue;
        bool alongside = false;
        float squeezeAheadSpeed = float.MaxValue;
        float yieldSpeed = float.MaxValue;
        float lookAhead = MathF.Max(50, me.Speed * 3);
        bool cautious = _now < me.CautiousUntil;
        float cornerSign = NextCornerSign(myS, 200);

        bool ignorePlayers = _now < me.IgnorePlayersUntil;
        foreach (var o in _neighbors)
        {
            if (o.IsBot && o.Id == me.Id) continue;
            if (!o.IsBot && ignorePlayers) continue;
            float ds = Line.Delta(myS, o.S);
            if (!Considers(me, o, ds)) continue;
            float longClear = (me.Car.Length + o.Length) / 2;
            // players: more room (their position arrives late) and a longer overlap window, so a bot doesn't turn in on a player
            // whose nose is next to its rear wheel
            float margin = o.IsBot && _now < me.MarginOverrideUntil ? -0.2f : SideMarginFor(me, o);
            float latClear = (me.Car.Width + o.Width) / 2 + margin;
            float dOff = o.Offset - me.Offset;
            float closing = o.Speed - me.Speed; // > 0: car behind is faster
            float overlap = o.IsBot ? 1.0f : MathF.Max(1.0f, Settings.PlayerOverlap);

            bool isAlongside = MathF.Abs(ds) < longClear + overlap
                               || (ds < 0 && ds > -(longClear + MathF.Max(overlap + 1f, closing * 1.2f)));
            if (isAlongside)
            {
                alongside = true;
                // in a close fight a driver sometimes misjudges the gap and leans on the other car
                if (Settings.BotContacts && o.IsBot && me.Phase == BotPhase.Racing && _now >= me.MarginOverrideUntil + 8
                    && _rng.NextSingle() < _stepDt * 0.05f * (me.Driver.Aggression + me.Impatience + 2 * ErrorLevel(me)))
                {
                    me.MarginOverrideUntil = _now + 0.7;
                    me.MisjudgeTowards = MathF.Sign(dOff);
                }
                if (MathF.Abs(dOff) > 0.2f)
                {
                    if (dOff > 0) sideMax = MathF.Min(sideMax, o.Offset - latClear);
                    else sideMin = MathF.Max(sideMin, o.Offset + latClear);
                }
                // who backs out if the two of us get squeezed: the car behind, or when level, the one on the outside of the next corner.
                // Careful drivers back out early (even when nearly level), aggressive ones hold on until the other is clearly ahead.
                float give = me.Clone != null ? 0.15f : me.Driver.Personality.Room;
                float hold = Math.Clamp(0.5f - 0.6f * (give - 0.15f), 0.1f, 0.85f);
                if (!o.IsBot) hold = MathF.Min(hold, 0.5f); // never hold on against a player
                bool iYield;
                if (ds > me.Car.Length * hold) iYield = true;
                else if (ds < -me.Car.Length * (1 - hold)) iYield = false;
                else if (o.IsBot && MathF.Abs(give - o.Bot!.Driver.Personality.Room) > 0.3f) iYield = give > o.Bot.Driver.Personality.Room;
                else if (cornerSign != 0 && MathF.Abs(dOff) > 0.2f) iYield = cornerSign * (me.Offset - o.Offset) < 0;
                else iYield = ds > 0;
                if (iYield)
                {
                    squeezeAheadSpeed = MathF.Min(squeezeAheadSpeed, o.Speed);
                    // side by side into a corner: the one who gives way lifts a little and tucks in behind (careful drivers early and
                    // clearly, aggressive ones only just), instead of staying alongside lap after lap
                    if (cornerSign != 0 && me.Phase == BotPhase.Racing && ds > -me.Car.Length * 0.3f && _now - Settings.RaceStartTime > 15)
                        yieldSpeed = MathF.Min(yieldSpeed, o.Speed - (0.3f + 1.2f * Math.Clamp(give + 0.2f, 0, 1)));
                }
                continue;
            }

            if (ds > 0 && ds < Settings.SlipstreamRange && MathF.Abs(dOff) < 1.4f && o.Speed > 25)
                draft = MathF.Max(draft, (1 - ds / Settings.SlipstreamRange) * Settings.SlipstreamStrength * (1 - MathF.Abs(dOff) / 1.4f));

            if (ds > 0 && ds < lookAhead)
            {
                bool inPath = MathF.Abs(dOff) < latClear || MathF.Abs(o.Offset - me.TargetOffset) < latClear;
                float gap = ds - longClear;
                if (inPath && gap < aheadGap)
                {
                    ahead = o;
                    aheadGap = gap;
                }
            }
            else if (ds < 0 && -ds < 40)
            {
                float gap = -ds - longClear;
                if (gap < behindGap)
                {
                    behind = o;
                    behindGap = gap;
                }
            }
        }

        me.Draft = draft;
        UpdateClearAhead(me, myS);

        // pressure: somebody (player or bot) sitting right behind in my slipstream for a long time makes me nervous
        bool pressed = behind is { } pb && behindGap < 20 && MathF.Abs(pb.Offset - me.Offset) < 1.6f && me.Phase == BotPhase.Racing;
        me.PressureTime = pressed ? me.PressureTime + _stepDt : MathF.Max(0, me.PressureTime - 2 * _stepDt);
        me.Pressure = Math.Clamp((me.PressureTime - 5) / 25f, 0, 1) * (1 - Math.Clamp(me.Driver.Personality.Composure, 0, 1));
        me.Weaving = false;
        // the corner plan's shift is added on top of the intended lane at the end; take it off again first
        me.TargetOffset -= me.LineShiftNow;
        me.LineShiftNow = 0;
        UpdateCornerPlan(me, myS);
        // fighting for a position: the attacker commits (brakes later, carries more speed into the corner) - and risks more
        bool committing = me.OvertakeTargetId >= 0 && FindNeighbor(me.OvertakeTargetId) is { } tgt && Line.Delta(myS, tgt.S) < 15;
        float vLine = LineSpeedLimit(me, me.TargetOffset, committing ? -(0.02f + 0.04f * AttackOf(me)) : 0);
        float vTarget = vLine;

        // ---- yellow flag: somebody stopped or crawling ahead
        me.YellowHazards = false;
        if (me.Phase == BotPhase.Racing)
        {
            foreach (float inc in _incidents)
            {
                float d = Line.Delta(myS, inc);
                if (d < 3 || d > 350) continue;
                me.YellowUntil = _now + 1.5;
                if (d < 250) me.YellowHazards = true;
                if (d < 150) vTarget = MathF.Min(vTarget, MathF.Max(15, vLine * 0.9f));
            }
        }
        bool yellow = _now < me.YellowUntil;

        // ---- blue flag: a car that is a lap (or more) ahead comes up behind
        me.Indicator = 0;
        if (Settings.BlueFlags && me.Phase == BotPhase.Racing)
        {
            float myProgress = me.LapsCompleted * Line.Length + Line.WrapS(myS - Settings.StartLineS);
            foreach (var o in _neighbors)
            {
                if (o.IsBot && o.Id == me.Id) continue;
                float ds = Line.Delta(myS, o.S);
                if (ds > -2 || ds < -150) continue;
                if (o.Progress - myProgress < Line.Length * 0.5f) continue;
                if (o.Speed < me.Speed - 3) continue;
                if (_now >= me.BlueFlagUntil)
                {
                    // move over to the side with more room, keep right if both are fine
                    var (rm, rp) = Line.MinRoom(myS, 120);
                    float right = Line.RightIsPlus ? rp : rm, left = Line.RightIsPlus ? rm : rp;
                    me.BlueSide = right >= me.Car.Width + 1.5f || right >= left ? 1 : -1;
                }
                me.BlueFlagUntil = _now + 2.5;
            }
        }
        bool blueFlag = _now < me.BlueFlagUntil;

        // ---- braking-zone mistakes
        bool braking = vLine < me.Speed - 3;
        if (braking && !me.InBrakingZone && me.Phase == BotPhase.Racing)
            RollBrakingMistake(me);
        me.InBrakingZone = braking;
        CornerExitMistakes(me);
        Aquaplaning(me);

        // ---- overtake bookkeeping
        if (me.OvertakeTargetId >= 0 && (_now < me.YellowUntil || _now < me.BlueFlagUntil))
        {
            // no overtaking under yellow; a lapped car lets the others through instead of fighting
            Diag("end:yellow/blue");
            me.OvertakeTargetId = -1;
            me.ReturnToLineAfter = _now;
        }
        if (me.OvertakeTargetId >= 0)
        {
            var target = FindNeighbor(me.OvertakeTargetId);
            if (target == null)
            {
                Diag("end:target gone");
                me.OvertakeTargetId = -1;
            }
            else
            {
                float ds = Line.Delta(myS, target.Value.S);
                me.OvertakeBestGap = MathF.Min(me.OvertakeBestGap, ds);
                float tLatClear = (me.Car.Width + target.Value.Width) / 2 + SideMarginFor(me, target.Value);
                if (MathF.Abs(target.Value.Offset - me.Offset) > tLatClear - 0.4f)
                    me.OvertakeSeparatedAt = _now;
                // keep aiming for the chosen side of the target (the lane gets re-clamped to the road below)
                me.TargetOffset = target.Value.Offset + me.OvertakeSide * (tLatClear + 0.2f);
                // the door closed (the target covered that side, or the track narrows): try the other side, or wait behind
                bool closed = me.TargetOffset > maxOff + 0.5f || me.TargetOffset < minOff - 0.5f;
                if (!closed) me.OvertakeClosedSince = double.NaN;
                else if (double.IsNaN(me.OvertakeClosedSince)) me.OvertakeClosedSince = _now;
                else if (_now - me.OvertakeClosedSince > 0.8 && ds > 0)
                {
                    float other = target.Value.Offset - me.OvertakeSide * (tLatClear + 0.2f);
                    bool insideOnly = me.Driver.Personality.InsideLine >= 0.5f && NextCornerSign(myS, 250) is var cs && cs != 0 && MathF.Sign(other - target.Value.Offset) != cs;
                    if (!insideOnly && other >= minOff && other <= maxOff && LaneFree(me, other, myS, -(me.Car.Length + 3), ds + 15, target.Value.Id, ignoreFasterAhead: true))
                    {
                        me.OvertakeSide = -me.OvertakeSide;
                        me.TargetOffset = other;
                        me.OvertakeClosedSince = double.NaN;
                        Diag("switch side");
                    }
                    else
                    {
                        // stays right behind, waiting for the door to open again (the time limits below end it)
                        me.OvertakeClosedSince = _now;
                    }
                }
                if (me.OvertakeTargetId < 0) { }
                else if (ds < -((me.Car.Length + target.Value.Length) / 2 + 2))
                {
                    Diag("end:passed");
                    me.OvertakeTargetId = -1;
                    me.Overtakes++;
                    me.ReturnToLineAfter = _now + 0.8;
                }
                else if (_now - me.OvertakeSince > 12 + 10 * AttackOf(me) || ds > 70
                         || ((ds - me.OvertakeBestGap) / MathF.Max(me.Speed, 10) > 0.25f + 0.35f * AttackOf(me) && _now - me.OvertakeSince > 2)
                         || _now - me.OvertakeSeparatedAt > 8 + 6 * AttackOf(me))
                {
                    // lost ground or took too long: tuck in behind again and wait a moment before the next try
                    if (_now - me.OvertakeSince > 12 + 10 * AttackOf(me))
                        Diag($"toolong: ds {(ds < 0 ? "<0" : ds < 5 ? "0-5" : ds < 10 ? "5-10" : ds < 20 ? "10-20" : ">20")} best {(me.OvertakeBestGap < 5 ? "<5" : me.OvertakeBestGap < 10 ? "5-10" : ">10")} sameLane {(MathF.Abs(target.Value.Offset - me.Offset) < tLatClear - 0.4f)}");
                    Diag(_now - me.OvertakeSince > 12 + 10 * AttackOf(me) ? "end:too long" : ds > 70 ? "end:dropped back" : _now - me.OvertakeSeparatedAt > 8 + 6 * AttackOf(me) ? "end:never alongside" : "end:lost ground");
                    me.OvertakeTargetId = -1;
                    me.OvertakeGiveUps++;
                    me.ReturnToLineAfter = _now;
                    me.OvertakeCooldownUntil = _now + (3 + _rng.NextDouble() * 3) * (1 - 0.6 * me.Impatience) * (1.6 - AttackOf(me));
                }
            }
        }

        // ---- car in front
        if (ahead is { } a)
        {
            float latClear = (me.Car.Width + a.Width) / 2 + SideMarginFor(me, a);
            bool gripLimited = vLine < me.Car.TopSpeed * 0.93f;
            if (gripLimited && aheadGap < 40)
            {
                // how much faster could I go where the car in front is limiting me (corners, braking zones)?
                float sample = Math.Clamp(vLine - a.Speed, -4, 4);
                me.PressureEma += (sample - me.PressureEma) * MathF.Min(1, _stepDt / 4f);
            }
            // impatience: the longer I'm stuck behind a slower car, the harder I push
            float closing = me.Speed - a.Speed;
            // held up by the car in front (flashing additionally needs a clear speed advantage, see below:
            // a group at the same pace closes and opens the gaps all the time, that's no reason to flash)
            if (aheadGap < 30 && (me.PressureEma > 0.2f || closing > 0.5f))
            {
                if (me.BlockedById != a.Id || double.IsNaN(me.BlockedSince))
                {
                    me.BlockedById = a.Id;
                    me.BlockedSince = _now;
                }
                me.LastBlockedAt = _now;
            }
            // patient drivers take a lot longer to get impatient
            float patience = Math.Clamp(me.Driver.Personality.Patience, 0, 1);
            float impTime = Settings.ImpatienceTime * (0.4f + 1.6f * patience);
            float imp = Settings.ImpatienceTime > 0 && !double.IsNaN(me.BlockedSince)
                ? Math.Clamp((float)(_now - me.BlockedSince) / impTime, 0, 1) * (0.4f + 0.6f * me.Driver.Aggression)
                : 0;
            me.Impatience = imp;

            float att = AttackOf(me);
            float attackRange = 3 + me.Speed * (0.12f + 0.18f * me.Driver.Aggression) + imp * 8
                                + 4 * me.Driver.Personality.InsideLine + 3 * me.Driver.Personality.BrakeBehavior + 10 * att;

            // stuck in the slipstream on a straight for a while: weave a little to unsettle the car in front
            if (me.Draft > 0.05f && !gripLimited && aheadGap > 4 && aheadGap < 30 && me.OvertakeTargetId != a.Id)
                me.DraftTime += _stepDt;
            else
                me.DraftTime = MathF.Max(0, me.DraftTime - 2 * _stepDt);
            float weaving = me.Driver.Personality.Weaving;
            if (weaving > 0.05f && me.DraftTime > 7 - 4 * weaving && !gripLimited && !yellow && !blueFlag && me.Phase == BotPhase.Racing)
            {
                me.Weaving = true;
                me.WeaveCenter = a.Offset;
            }
            float needAdvantage = (1.2f - 0.9f * me.Driver.Aggression) * (1 - imp) * (1.4f - 0.9f * att);

            // flash the lights at the car in front
            // not in the first minute of a race, not in the pits, only when I'm clearly quicker and right behind
            bool flashAllowed = !Settings.IsRace || _now - Settings.RaceStartTime > Settings.FlashStartDelay;
            if (flashAllowed && imp > 0.45f + 0.35f * patience && aheadGap < 25 && me.PressureEma > 1.6f && _now >= me.NextFlashAt
                && !yellow && !blueFlag && !me.InPitLane && !(a.IsBot && a.Bot!.InPitLane) && me.Phase == BotPhase.Racing
                && _rng.NextSingle() < 1 - 0.8f * patience)
            {
                me.FlashUntil = _now + 0.9;
                me.FlashCount++;
                me.NextFlashAt = _now + (8 + _rng.NextDouble() * 10 * (1.2 - imp)) * (0.6 + 2 * patience);
            }
            else if (_now >= me.NextFlashAt && imp > 0.4f)
            {
                me.NextFlashAt = _now + 3; // decided not to flash this time
            }

            if (me.OvertakeTargetId < 0 && !cautious && !yellow && !blueFlag && !Settings.SafetyCar && me.Phase == BotPhase.Racing && _now >= me.OvertakeCooldownUntil
                && aheadGap < attackRange && (me.PressureEma > needAdvantage || closing > 1.0f))
            {
                if (!TryChooseOvertakeSide(me, a, aheadGap, latClear, minOff, maxOff, out var side))
                {
                    me.OvertakeNoRoom++;
                }
                else
                {
                    me.OvertakeAttempts++;
                    Diag(me.OvertakeTargetId >= 0 ? "start:switched target" : "start");
                    me.TargetOffset = side;
                    me.OvertakeTargetId = a.Id;
                    me.OvertakeSince = _now;
                    me.OvertakeSeparatedAt = _now;
                    me.OvertakeSide = side > a.Offset ? 1 : -1;
                    me.OvertakeBestGap = aheadGap + (me.Car.Length + a.Length) / 2;
                }
            }

            bool blocked = MathF.Abs(a.Offset - me.Offset) < latClear - Settings.SideMargin * 0.5f;
            if (UnstuckAround(me, a, myS, minOff, maxOff, ref vTarget)) blocked = false;
            if (blocked)
            {
                float followGap = 1.5f + me.Speed * (cautious ? 0.45f : 0.10f + 0.20f * (1 - me.Driver.Aggression));
                if (me.OvertakeTargetId == a.Id && !cautious)
                    followGap = 1.0f + me.Speed * (0.05f + 0.04f * (1 - me.Driver.Aggression)); // right on the gearbox
                followGap = MathF.Max(1.0f + me.Speed * 0.04f, followGap * (1 - 0.35f * imp));
                // in the slipstream on a straight: close right up for a run at the next braking zone
                if (me.Draft > 0.05f && !gripLimited && !cautious) followGap *= 0.45f;
                // a player's braking reaches us late (network): follow a little further back and react to it at once
                float aSpeed = a.IsBot ? a.Speed : MathF.Max(0, a.Speed + MathF.Min(0, a.Accel) * 0.25f);
                if (!a.IsBot) followGap += MathF.Max(0, me.Speed - aSpeed) * 0.15f + 0.5f;
                float vFollow = aSpeed + (aheadGap - followGap) * 0.8f;
                vTarget = MathF.Min(vTarget, MathF.Max(0, vFollow));
            }
        }

        if (_now - me.LastBlockedAt > 4)
        {
            me.BlockedSince = double.NaN;
            me.BlockedById = -1;
            me.Impatience = 0;
        }

        // ---- defend against a faster car right behind (or one that is going for it)
        float def = DefendOf(me);
        bool attackedBy = behind is { IsBot: true } ab && ab.Bot!.OvertakeTargetId == me.Id;
        if (behind is { } b && me.OvertakeTargetId < 0 && me.Phase == BotPhase.Racing && !blueFlag && !yellow && !Settings.SafetyCar
            && behindGap < 2 + 8 * def && (b.Speed > me.Speed + 1f || attackedBy))
        {
            if (def >= 0.25f && _now > me.DefendUntil + 8 - 4 * def && _rng.NextSingle() < def * 0.02f * (1 + me.Driver.Personality.InsideLine)
                     && (b.IsBot || behindGap > 3)) // a player's position arrives late: only cover the inside while he's clearly behind
            {
                // one move towards the inside of the next corner: the harder the driver defends, the further over.
                // Not when the attacker is already there (that would be closing the door on him: contact)
                float k = NextCornerSign(myS, 250);
                bool alreadyInside = k != 0 && (b.Offset - me.Offset) * k > 1.0f;
                if (k != 0 && !alreadyInside)
                {
                    me.DefendOffset = Math.Clamp(me.Offset + k * (1.2f + 1.8f * def), minOff, maxOff);
                    me.DefendUntil = _now + 2.5 + 2 * def;
                    me.Defends++;
                }
            }
            else if (def < 0.25f && attackedBy && b.Speed > me.Speed + 1f && _now > me.DefendUntil + 3 && MathF.Abs(b.Offset - me.Offset) < 3.5f)
            {
                // a careful driver doesn't fight a clearly faster car: moves over a little and lets it by
                float away = MathF.Sign(me.Offset - b.Offset);
                if (away == 0) away = 1;
                me.DefendOffset = Math.Clamp(me.Offset + away * 0.9f, minOff, maxOff);
                me.DefendUntil = _now + 2;
                me.LetBy++;
            }
        }

        // ---- choose lateral target
        bool ownLine = false;
        if (me.Clone != null) me.CloneZNow += (me.CloneZ - me.CloneZNow) * MathF.Min(1, _stepDt / 2.5f);
        if (blueFlag)
        {
            me.TargetOffset = me.BlueSide * (Line.RightIsPlus ? 1 : -1) > 0 ? maxOff : minOff;
            me.Indicator = me.BlueSide;
            vTarget = MathF.Min(vTarget, vLine * 0.95f);
            me.ReturnToLineAfter = _now + 1.5;
        }
        else if (me.OvertakeTargetId < 0)
        {
            if (_now < me.DefendUntil)
            {
                me.TargetOffset = me.DefendOffset;
            }
            else if (_now >= me.ReturnToLineAfter)
            {
                // go back to the racing line when that lane is free
                float lineOffset = me.Clone is { } cl
                    ? CloneLineOffset(me, cl, myS)
                    : Math.Clamp(PersonalLineOffset(me, myS) + RainLineOffset(me, myS, minOff, maxOff), minOff, maxOff);
                if (LaneFree(me, lineOffset, myS, -(me.Car.Length + 2), 25))
                {
                    me.TargetOffset = lineOffset;
                    ownLine = true;
                }
            }
        }

        me.OnOwnLine = ownLine;

        // out-lap with cold tyres and nobody around: weave on the straights to get heat into them
        bool sc = Settings.SafetyCar && me.Phase == BotPhase.Racing && !me.InPitLane;
        if (sc && me.OvertakeTargetId >= 0) { me.OvertakeTargetId = -1; me.ReturnToLineAfter = _now; }
        float warmWeave = !me.Weaving && me.OvertakeTargetId < 0 && !blueFlag
                          && (sc ? aheadGap > 12 && !alongside : aheadGap > 80 && behindGap > 60) && WantsTyreWarmWeave(me, myS)
            ? 0.8f * MathF.Sin((float)(_now * 2 * Math.PI / 1.7) + me.Id) : 0;

        if (me.Weaving && me.OvertakeTargetId < 0 && !blueFlag)
        {
            float amp = 0.5f + 0.7f * me.Driver.Personality.Weaving;
            me.TargetOffset = me.WeaveCenter + amp * MathF.Sin((float)(_now * 2 * Math.PI / 2.4) + me.Id);
        }

        float shiftBase = float.NaN;
        // imprecise line through this corner (only when driving alone on the line, not while fighting)
        if (((me.PlanActive && me.PlanKind != CornerLineKind.Clean) || warmWeave != 0) && me.OvertakeTargetId < 0 && _now >= me.DefendUntil
            && !blueFlag && !me.Weaving && me.Mistake == MistakeKind.None)
        {
            shiftBase = me.TargetOffset;
            me.LineShiftNow = CornerShift(me, myS) + warmWeave;
            me.TargetOffset += me.LineShiftNow;
        }

        if (_now < me.MistakeUntil)
        {
            // run a bit wide
            float k = Line.CurvatureAt(myS);
            if (MathF.Abs(k) > 1 / 300f) me.TargetOffset -= MathF.Sign(k) * 0.02f;
        }

        float lo = MathF.Max(minOff, sideMin);
        float hi = MathF.Min(maxOff, sideMax);
        if (lo > hi)
        {
            // squeezed: sit in the middle of the gap; the car that is further back lifts until there is room again
            me.TargetOffset = (lo + hi) / 2;
            if (squeezeAheadSpeed < float.MaxValue)
                vTarget = MathF.Min(vTarget, MathF.Max(0, squeezeAheadSpeed - 2f));
        }
        else
        {
            me.TargetOffset = Math.Clamp(me.TargetOffset, lo, hi);
        }

        if (yieldSpeed < float.MaxValue && me.OvertakeTargetId < 0) vTarget = MathF.Min(vTarget, MathF.Max(0, yieldSpeed));

        if (cautious && me.Speed < 3 && me.Phase == BotPhase.Racing && _now - me.OvertakeSince < 0.3)
            vTarget = MathF.Min(vTarget, 0);

        if (_now < me.MarginOverrideUntil && me.Mistake == MistakeKind.None && lo <= hi)
            me.TargetOffset = Math.Clamp(me.Offset + me.MisjudgeTowards * 0.5f, lo, hi); // leans on the other car
        if (!float.IsNaN(shiftBase)) me.LineShiftNow = me.TargetOffset - shiftBase; // after clamping to the room there is
                CornerExecution(me, myS, ref vTarget);
        MistakeThink(me, ref vTarget);
        if (sc) vTarget = MathF.Min(vTarget, Settings.SafetyCarSpeed);
        me.TargetSpeed = MathF.Max(0, vTarget);
        if (me.Id == TraceBotId && Trace != null && _now - _lastTrace >= 0.5)
        {
            _lastTrace = _now;
            Trace($"t={_now,7:F1} s={myS,7:F0} v={me.Speed * 3.6f,4:F0} tgtV={vTarget * 3.6f,4:F0} line={vLine * 3.6f,4:F0} off={me.Offset,5:F1}->{me.TargetOffset,5:F1} " +
                  $"room=[{minOff,5:F1},{maxOff,5:F1}] side=[{sideMin,5:F1},{sideMax,5:F1}] draft={me.Draft:F2} " +
                  (ahead is { } aa ? $"ahead={aa.Id} gap={aheadGap,5:F1} v={aa.Speed * 3.6f,4:F0} off={aa.Offset,5:F1} " : "") +
                  $"ot={me.OvertakeTargetId} alongside={alongside}");
        }
    }

    public int DiagNoRoomEdge, DiagNoRoomLane, DiagNoRoomAll;
    public readonly Dictionary<string, int> DiagCounts = new();
    internal void Diag(string key) => DiagCounts[key] = DiagCounts.GetValueOrDefault(key) + 1;

    private bool TryChooseOvertakeSide(RaceBot me, Neighbor a, float gap, float latClear, float minOff, float maxOff, out float side)
    {
        float myS = Line.WrapS((float)me.Distance);
        // the lane has to be free alongside the target and a little beyond (to pull ahead and back in), not for half a straight
        float span = gap + (me.Car.Length + a.Length) / 2 + 12 + 20 * (1 - AttackOf(me));
        var (roomMinus, roomPlus) = Line.MinRoom(myS, span);
        float half = me.Car.Width / 2;
        float lo = MathF.Max(minOff, -roomMinus + half + Settings.EdgeMargin);
        float hi = MathF.Min(maxOff, roomPlus - half - Settings.EdgeMargin);

        float plus = a.Offset + latClear + 0.2f;
        float minus = a.Offset - latClear - 0.2f;
        bool plusOk = plus <= hi && LaneFree(me, plus, myS, -(me.Car.Length + 3), span, a.Id, ignoreFasterAhead: true);
        bool minusOk = minus >= lo && LaneFree(me, minus, myS, -(me.Car.Length + 3), span, a.Id, ignoreFasterAhead: true);

        side = 0;
        if (!(plus <= hi) && !(minus >= lo)) DiagNoRoomEdge++;
        else if (!plusOk && !minusOk) DiagNoRoomLane++;
        float cornerAhead = NextCornerSign(myS, 250);
        if (me.Driver.Personality.InsideLine >= 0.5f && cornerAhead != 0)
        {
            // dive-bombers only go down the inside
            if (cornerAhead > 0) minusOk = false; else plusOk = false;
        }
        if (!plusOk && !minusOk) { DiagNoRoomAll++; return false; }
        if (plusOk && !minusOk) { side = plus; return true; }
        if (minusOk && !plusOk) { side = minus; return true; }

        // both possible: prefer the inside of the next corner, else the smaller move
        float corner = NextCornerSign(myS, 250);
        if (corner > 0) side = plus;
        else if (corner < 0) side = minus;
        else side = MathF.Abs(plus - me.Offset) < MathF.Abs(minus - me.Offset) ? plus : minus;
        return true;
    }

    /// <summary>+1 / -1 for the direction of the next real corner within the distance, 0 if none.</summary>
    private float NextCornerSign(float s, float distance)
    {
        for (float d = 20; d < distance; d += 5)
        {
            float k = Line.Curvature[Line.IndexAt(s + d)];
            if (MathF.Abs(k) > 1f / 250f) return MathF.Sign(k);
        }
        return 0;
    }

    private bool LaneFree(RaceBot me, float offset, float myS, float from, float to, int ignoreId = int.MinValue, bool ignoreFasterAhead = false)
    {
        foreach (var o in _neighbors)
        {
            if (o.IsBot && o.Id == me.Id) continue;
            if (o.Id == ignoreId) continue;
            float ds = Line.Delta(myS, o.S);
            if (ds < from - o.Length / 2 || ds > to + o.Length / 2) continue;
            // a car further ahead that is at least as fast moves away: it doesn't block the lane
            if (ignoreFasterAhead && ds > (me.Car.Length + o.Length) / 2 + 6 && o.Speed >= me.Speed - 0.5f) continue;
            float latClear = (me.Car.Width + o.Width) / 2 + SideMarginFor(me, o);
            if (MathF.Abs(o.Offset - offset) < latClear) return false;
        }
        return true;
    }

    /// <summary>Distance kept to the track edge: drivers who use all of the track go onto the kerbs, careful ones stay clear.</summary>
    private float EdgeMarginFor(RaceBot me)
    {
        if (me.Clone != null) return Settings.EdgeMargin;
        float use = me.Driver.Personality.TrackUse;
        return Settings.EdgeMargin - (use > 0 ? 0.55f : 0.35f) * use;
    }

    /// <summary>How far past the edge (of the width data) the car may physically be without a mistake: kerbs for those who use them.</summary>
    private float KerbAllowance(RaceBot me)
        => me.Clone != null ? me.Car.Width / 2 - CloneProfile.KerbLimit + 0.2f : MathF.Max(0, -EdgeMarginFor(me)) + 0.05f;

    /// <summary>Room left to a car alongside: the personality's (careful drivers leave more, aggressive ones squeeze), never less to players.</summary>
    private float SideMarginFor(RaceBot me, in Neighbor o)
    {
        float room = me.Clone != null ? 0 : me.Driver.Personality.Room;
        return o.IsBot ? MathF.Max(0.05f, Settings.SideMargin + room) : MathF.Max(Settings.SideMargin, Settings.PlayerSideMargin) + MathF.Max(0, room) * 0.5f;
    }

    private float AttackOf(RaceBot me) => me.Clone != null ? 0.5f : Math.Clamp(me.Driver.Personality.Attack, 0, 1);
    private float DefendOf(RaceBot me) => me.Clone != null ? 0.3f : Math.Clamp(me.Driver.Personality.Defend, 0, 1);

    private double NextGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private Neighbor? FindNeighbor(int id)
    {
        foreach (var n in _neighbors)
            if (n.Id == id) return n;
        return null;
    }

    private void Integrate(RaceBot me, float dt)
    {
        float phys = Settings.GripFactor * me.CarGrip;
        float skill = me.Driver.Pace + me.PaceNoise + me.PaceBoost;
        float pace = DriverProfile.CornerSkill(skill) * phys;
        float v = me.Speed;
        float target = me.TargetSpeed;

        if (!double.IsNaN(me.LaunchAt) && _now < me.LaunchAt && me.Speed < 1 && me.LapsCompleted == 0)
            target = 0; // reaction time at the start
        else if (_now < me.CautiousUntil && me.Speed < 1 && me.LapsCompleted == 0 && _now - me.LapStartTime < 1.5)
            target = 0;
        if (Settings.FuelRate > 0 && me.Fuel <= 0)
            target = MathF.Min(target, 25 / 3.6f); // out of fuel: rolling to the pits on the last drops

        // weaker drivers lose their time in the corners (and on the way out of them), on the straights everybody is flat out
        float sNow = Line.WrapS((float)me.Distance);
        float kNow = MathF.Abs(Line.CurvatureAt(sNow));
        float cornerLoad = Math.Clamp(v * v * kNow / (me.Car.LateralGrip * CarSpec.G) * 1.3f, 0, 1);
        float throttlePace = 1 - (1 - DriverProfile.ThrottleSkill(skill) * phys) * cornerLoad;

        // pedals: the target falls along the planned braking curve; the driver follows it with a brake pressure that builds up quickly,
        // is released gradually towards the apex (trail braking), and small corrections are done by lifting only (no brake lights)
        float drag = me.Car.DragCoefficient * v * v * DamageDrag(me);
        float coast = drag + 1.0f; // lifting: drag + engine braking
        float maxBrake = me.Car.BrakeAt(v, MathF.Min(1, DriverProfile.BrakeSkill(skill) + 0.06f * MathF.Max(0, me.Driver.Personality.BrakeBehavior)) * phys, me.MassRatio);
        float physBrake = me.Car.BrakeAt(v, phys, me.MassRatio); // what the car could do: the pedal is shown relative to this
        float targetFall = double.IsNaN(me.PrevTargetSpeed) ? 0 : MathF.Max(0, (me.PrevTargetSpeed - target) / dt);
        me.PrevTargetSpeed = target;
        float accel;
        if (target < v - 0.05f)
        {
            float want = Math.Clamp(MathF.Min(targetFall, maxBrake) * 0.95f + (v - target) / 0.35f, 0, maxBrake);
            float pedal = want <= coast || _now < me.LiftOnlyUntil ? 0 : Math.Clamp((want - coast) / MathF.Max(0.5f, physBrake - coast), 0, 1);
            // quick to press (0.12 s to full), slower to release (0.4 s); late brakers stamp on it, careful drivers squeeze it
            float bb = me.Driver.Personality.BrakeBehavior;
            float smooth = Math.Clamp(me.Driver.Personality.Smoothness, 0, 1);
            float press = 0.12f * (1 - 0.35f * bb) * (1 + 0.8f * smooth), release = 0.4f * (1 - 0.2f * bb);
            me.Brake += Math.Clamp(pedal - me.Brake, -dt / release, dt / press);
            me.Throttle = MathF.Max(0, me.Throttle - dt / 0.1f);
            accel = -MathF.Min(maxBrake, coast + me.Brake * (physBrake - coast));
            v = MathF.Max(target, v + accel * dt);
        }
        else
        {
            me.Brake = MathF.Max(0, me.Brake - dt / 0.25f);
            float full = me.Car.AccelAt(v, throttlePace) / me.MassRatio + (me.Draft - (DamageDrag(me) - 1)) * me.Car.DragCoefficient * v * v;
            // in a fight the attacker gets on the power earlier and harder out of the corner
            if (me.OvertakeTargetId >= 0) full *= 1.02f + 0.04f * AttackOf(me);
            full *= LaunchTraction(me);
            // a clone accelerates like the player did here: our engine model is only an estimate of the real car
            if (me.Clone is { } cl && me.Phase == BotPhase.Racing && !me.InPitLane)
            {
                float v1 = cl.SpeedAt(sNow, Line.Length), v2 = cl.SpeedAt(sNow + CloneProfile.BinSize, Line.Length);
                float playerAccel = (v2 * v2 - v1 * v1) / (2 * CloneProfile.BinSize);
                if (playerAccel > full) full = MathF.Min(playerAccel * 1.03f, full + 4f);
            }
            // full throttle when far below the target, part throttle to hold the speed near it (fast corners, following)
            float hold = MathF.Max(0, full) > 0.1f ? Math.Clamp(drag / (full + drag), 0, 1) : 1;
            float pedal = Math.Clamp(hold + (target - v) / 0.6f, 0, 1);
            // smooth drivers roll onto the throttle more gently
            float rise = 0.2f * (1 + 1.5f * Math.Clamp(me.Driver.Personality.Smoothness, 0, 1));
            me.Throttle += Math.Clamp(pedal - me.Throttle, -dt / 0.15f, dt / rise);
            // net acceleration: the full-throttle value scaled by the pedal, minus drag the engine doesn't cover
            accel = me.Throttle * (full + drag) - drag;
            if (me.Brake > 0.05f) accel -= me.Brake * (physBrake - coast);
            v = accel > 0 ? MathF.Min(target, v + accel * dt) : v + accel * dt;
        }
        me.Accel = accel;
        me.Speed = MathF.Max(0, v);
        if (MistakeIntegrate(me, dt, pace * 1.04f)) return;
        float launchSlip = LaunchSlipSpeed(me);
        if (launchSlip > 0) me.RearSlip = MathF.Max(me.RearSlip, 1 + launchSlip / MathF.Max(me.Speed, 1.5f));

        // lateral movement: smooth, limited lateral speed and acceleration (not while running wide, the car can't turn tighter)
        if (!me.Overspeed)
        {
            float desiredLat, latAcc = 5f;
            if (me.OnOwnLine && !me.Weaving)
            {
                // on its own line (a clone: the player's, a bot: its personal one) it drives along it like along the racing line:
                // the sideways speed comes from the line's slope (feed-forward), a small correction pulls it back onto it.
                // No chasing a point ahead, no snapping.
                float sHere = Line.WrapS((float)me.Distance);
                float onLine = me.Clone is { } cl ? CloneLineOffset(me, cl, sHere) : me.TargetOffset;
                // slope of the line as driven (inside the track): from the same function, so the two always agree
                float slope = (OwnLineOffset(me, sHere + 2) - OwnLineOffset(me, sHere - 2)) / 4f;
                desiredLat = me.Speed * slope + Math.Clamp((onLine - me.Offset) * 2f, -1.5f, 1.5f);
                desiredLat = Math.Clamp(desiredLat, -8f, 8f);
                latAcc = 12f; // it's the path's own curvature, not a steering correction
            }
            else
            {
                float err = me.TargetOffset - me.Offset;
                // a clone follows the player's line more tightly (the line is his, not a lane change)
                bool tight = me.Clone != null && me.OvertakeTargetId < 0;
                float maxLat = MathF.Min(tight ? 5f : 3.5f, 0.5f + me.Speed * (tight ? 0.09f : 0.06f));
                desiredLat = Math.Clamp(err * (tight ? 2.5f : 1.4f), -maxLat, maxLat);
            }
            me.LateralSpeed += Math.Clamp(desiredLat - me.LateralSpeed, -latAcc * dt, latAcc * dt);
        }
        me.Offset += me.LateralSpeed * dt;

        // keep on the road (or on the grass next to it after a mistake)
        float s = Line.WrapS((float)me.Distance);
        int i = Line.IndexAt(s);
        ClampOffsetWithAllowance(me);

        // progress along the line is faster on the inside of a corner
        float k = Line.Curvature[i];
        float factor = MathF.Max(0.5f, 1 - k * me.Offset);
        me.Distance += me.Speed * dt / factor;
    }

    private void ResolveOverlaps()
    {
        // Bots are kinematic, so nothing stops them from driving through each other or through players.
        // After moving, push apart anything that overlaps. The car behind (or the bot, against a player) yields.
        for (int x = 0; x < Bots.Count; x++)
        {
            var me = Bots[x];
            if (me.Phase is not (BotPhase.Racing or BotPhase.CoolDown)) continue;
            if (OffTrackInPits(me)) continue;
            float myS = Line.WrapS((float)me.Distance);

            foreach (var o in _neighbors)
            {
                if (o.IsBot && o.Id == me.Id) continue;
                if (!o.IsBot && _now < me.IgnorePlayersUntil) continue;
                if (me.GhostUntil > _now || (o.IsBot && o.Bot!.GhostUntil > _now)) continue;
                if (o.IsBot && OffTrackInPits(o.Bot!)) continue;
                float oS = o.IsBot ? Line.WrapS((float)o.Bot!.Distance) : o.S;
                float oOff = o.IsBot ? o.Bot!.Offset : o.Offset;
                float ds = Line.Delta(myS, oS);
                float dOff = oOff - me.Offset;
                float longClear = (me.Car.Length + o.Length) / 2;
                float latClear = (me.Car.Width + o.Width) / 2;
                if (MathF.Abs(ds) >= longClear || MathF.Abs(dOff) >= latClear) continue;

                float longPen = longClear - MathF.Abs(ds);
                float latPen = latClear - MathF.Abs(dOff);

                if (o.IsBot && latPen > 0.03f && longPen > 0.05f)
                {
                    bool side = latPen < longPen;
                    if (side || ds > 0) BotContact(me, o.Bot!, side, dOff, ds);
                    else BotContact(o.Bot!, me, false, -dOff, -ds);
                }
                if (latPen < longPen && MathF.Abs(dOff) > 0.05f)
                {
                    // mostly side by side: slide apart
                    float push = o.IsBot ? latPen / 2 : latPen;
                    me.Offset = ClampToRoad(me, me.Offset - MathF.Sign(dOff) * push);
                    if (o.IsBot) o.Bot!.Offset = ClampToRoad(o.Bot, o.Bot.Offset + MathF.Sign(dOff) * push);
                    // don't keep sliding into it (that's what makes a car jitter in and out of another one on the clients)
                    float otherLat = o.IsBot ? o.Bot!.LateralSpeed : o.External!.LateralSpeed;
                    if ((me.LateralSpeed - otherLat) * MathF.Sign(dOff) > 0) me.LateralSpeed = otherLat - MathF.Sign(dOff) * 0.3f;
                    if (o.IsBot && (o.Bot!.LateralSpeed - me.LateralSpeed) * MathF.Sign(dOff) < 0) o.Bot.LateralSpeed = me.LateralSpeed + MathF.Sign(dOff) * 0.3f;
                    if (!o.IsBot) me.CautiousUntil = Math.Max(me.CautiousUntil, _now + 1.5);
                }
                else if (ds > 0)
                {
                    // I'm behind: fall back
                    me.Distance -= longPen;
                    me.Speed = MathF.Min(me.Speed, o.Speed * 0.97f);
                }
                else if (!o.IsBot)
                {
                    // a player is behind me and overlapping: he pushes me, I take his speed
                    me.Distance += longPen * 0.5f;
                    me.Speed = MathF.Max(me.Speed, o.Speed);
                }
            }
        }
    }

    /// <summary>Apex moved along the track (m) at ApexStyle 1.</summary>
    private const float ApexShiftPerStyle = 7f;

    private float[]? _center;

    /// <summary>Offset of the middle of the track from the AI line (+ = the middle is on the + side), smoothed over ±8 m.</summary>
    internal float TrackCenter(float s)
    {
        if (_center == null)
        {
            var c = new float[Line.Count];
            int r = Math.Max(1, (int)MathF.Round(8f / Line.Spacing));
            for (int i = 0; i < c.Length; i++)
            {
                float sum = 0;
                for (int k = -r; k <= r; k++)
                {
                    int j = ((i + k) % c.Length + c.Length) % c.Length;
                    sum += (Line.RoomPlus[j] - Line.RoomMinus[j]) / 2;
                }
                c[i] = sum / (2 * r + 1);
            }
            _center = c;
        }
        Line.Interp(s, out var a, out var b, out var t);
        return _center[a] + (_center[b] - _center[a]) * t;
    }

    /// <summary>
    /// A bot's personal line at <paramref name="s"/>, built from the AI line's way across the track:
    /// - apex earlier or later (<see cref="RaceBot.ApexShift"/> m): the line's pattern moved along the track. Later: stays outside
    ///   longer, turns in later and sharper, apex later, straighter exit ("V"). Earlier: turns in early, round line, runs wide ("U").
    /// - track use (<see cref="RaceBot.LineScale"/> m): where the line touches an edge (entry, apex, exit) it goes further out onto the
    ///   kerbs, or stays that far clear of the edge.
    /// Depends on the personality and the driver; kept on the track softly, so the line has no kinks.
    /// </summary>
    internal float PersonalLineOffset(RaceBot me, float s)
    {
        EnsureStyle(me);
        if (me.LineScale == 0 && me.ApexShift == 0) return 0;
        float c = TrackCenter(s);
        float off = me.ApexShift != 0 ? c - TrackCenter(s - me.ApexShift) : 0;
        // the line is near an edge where the middle of the track is far away: push towards that edge (or away from it)
        off -= me.LineScale * MathF.Tanh(c / 1.5f);
        float half = me.Car.Width / 2, edge = EdgeMarginFor(me);
        float lo = -Line.RoomMinusAt(s) + half + edge, hi = Line.RoomPlusAt(s) - half - edge;
        return SoftClamp(off, MathF.Min(lo, 0), MathF.Max(hi, 0), 0.25f);
    }

    /// <summary>Unchanged inside [lo, hi]; beyond, the overshoot is squeezed smoothly into at most <paramref name="w"/> (no kink).</summary>
    private static float SoftClamp(float x, float lo, float hi, float w)
    {
        if (x > hi) return hi + w * (1 - MathF.Exp(-(x - hi) / w));
        if (x < lo) return lo - w * (1 - MathF.Exp(-(lo - x) / w));
        return x;
    }

    /// <summary>The line the bot drives when nobody is in the way.</summary>
    internal float OwnLineOffset(RaceBot me, float s) => me.Clone is { } cl ? CloneLineOffset(me, cl, s) : PersonalLineOffset(me, s);

    /// <summary>The bot's line is rolled again (personality changed, personal lines switched on or off).</summary>
    public void ResetStyle(RaceBot me) => me.StyleRolled = false;

    /// <summary>Rolls the driver's own version of his personality once.</summary>
    private void EnsureStyle(RaceBot me)
    {
        if (me.StyleRolled) return;
        me.StyleRolled = true;
        me.StyleScale = 0.6f + 0.8f * _rng.NextSingle();
        me.StyleBias = (float)NextGaussian() * 0.2f;
        if (!Settings.PersonalLines) return;
        var p = me.Driver.Personality;
        // track use: +0.4 m onto the kerbs .. -0.6 m clear of the edges
        float use = Math.Clamp(p.TrackUse * me.StyleScale + me.StyleBias, -1.3f, 1.3f);
        me.LineScale = use > 0 ? 0.4f * use : 0.6f * use;
        // apex: up to ±5 m later / earlier along the track
        float apex = Math.Clamp(p.ApexStyle * me.StyleScale + (float)NextGaussian() * 0.2f, -1.3f, 1.3f);
        me.ApexShift = ApexShiftPerStyle * apex;
    }

    /// <summary>The clone's line at <paramref name="s"/>: the player's own line, a little to the side as much as his laps differ.</summary>
    private float CloneLineOffset(RaceBot me, CloneProfile cl, float s)
    {
        // his laps differ a little: so does the clone's line, by at most half a metre (and smoothly)
        float o = cl.OffsetAt(s, Line.Length) + Math.Clamp(me.CloneZNow * cl.OffsetSpreadAt(s, Line.Length), -0.5f, 0.5f);
        return Math.Clamp(o, -Line.RoomMinusAt(s) + CloneProfile.KerbLimit, Line.RoomPlusAt(s) - CloneProfile.KerbLimit);
    }

    private float ClampToRoad(RaceBot bot, float offset)
    {
        float s = Line.WrapS((float)bot.Distance);
        float half = bot.Car.Width / 2;
        float allow = MathF.Max(Settings.GrassMoments ? bot.EdgeAllowance : 0, KerbAllowance(bot));
        return Math.Clamp(offset, -Line.RoomMinusAt(s) + half - allow, Line.RoomPlusAt(s) - half + allow);
    }

    private void UpdateTiming(RaceBot bot)
    {
        long idx = (long)Math.Floor((bot.Distance - Settings.StartLineS) / Line.Length);
        if (idx <= bot.LapIndex) return;

        bot.LapIndex = idx;
        if (idx == 0 && bot.TimingValid && bot.LapsCompleted == 0 && bot.LastLapSeconds == 0 && StartedBehindLine(bot))
        {
            // crossed the line for the first time after a standing start: lap 1 continues
            bot.StartCrossed = true;
            return;
        }

        if (bot.FuelAtLapStart >= 0 && !bot.PittedThisLap)
        {
            // laps with a pit stop use less fuel (pit lane), they don't count
            float used = bot.FuelAtLapStart + bot.FuelAddedThisLap - bot.Fuel;
            if (used > 0.1f) bot.LastLapFuel = bot.LastLapFuel > 0 ? MathF.Max(used, bot.LastLapFuel * 0.7f + used * 0.3f) : used;
        }
        bot.FuelAtLapStart = bot.Fuel;
        bot.FuelAddedThisLap = 0;
        bot.PittedThisLap = bot.InPitLane;

        if (bot.TimingValid)
        {
            float lap = (float)(_now - bot.LapStartTime);
            bot.LapsCompleted++;
            bot.LastLapSeconds = lap;
            bot.TotalTime = _now - bot.RaceStartTime;
            if (lap < bot.BestLapSeconds) bot.BestLapSeconds = lap;
            LapCompleted?.Invoke(bot, lap);
        }

        bot.TimingValid = true;
        bot.LapStartTime = _now;
    }

    private static bool StartedBehindLine(RaceBot bot) => !bot.StartCrossed;

    // ------------------------------------------------------------------ output

    public BotPose GetPose(RaceBot bot)
    {
        if (bot.Phase is BotPhase.Parked or BotPhase.Hidden)
        {
            var f = bot.ParkForward;
            var rot = new Vector3(MathF.Atan2(f.Z, f.X) - MathF.PI / 2, (MathF.Atan2(new Vector2(f.Z, f.X).Length(), f.Y) - MathF.PI / 2) * -1f, 0);
            return new BotPose(bot.ParkPosition + Vector3.UnitY * Settings.HeightOffset, rot, Vector3.Zero, 0, 0, 0, bot.Car.IdleRpm, true,
                Line.WrapS(Line.Project(bot.ParkPosition).S - Settings.StartLineS) / Line.Length);
        }

        if (bot.InPitLane && PitLane != null)
            return PitPose(bot);

        float s = Line.WrapS((float)bot.Distance);
        Line.Interp(s, out var a, out var b, out var t);
        var pos = Line.PositionAt(s, bot.Offset);
        var normal = Vector3.Normalize(Vector3.Lerp(Line.Normal[a], Line.Normal[b], t));
        pos += normal * Settings.HeightOffset;

        var fwd = Line.ForwardAt(s);
        var lat = Line.LateralAt(s);
        var vel = fwd * bot.Speed + lat * bot.LateralSpeed;
        var dir = bot.Speed > 0.5f || bot.LateralSpeed * bot.LateralSpeed > 0.25f ? Vector3.Normalize(vel) : fwd;
        dir = Rotate(dir, lat, bot.Yaw);

        var rotation = new Vector3(
            MathF.Atan2(dir.Z, dir.X) - MathF.PI / 2,
            (MathF.Atan2(new Vector2(dir.Z, dir.X).Length(), dir.Y) - MathF.PI / 2) * -1f,
            Line.CamberAt(s));

        float k = Line.CurvatureAt(s);
        if (MathF.Abs(k) > 1e-5f)
        {
            float r = 1f / MathF.Abs(k) - bot.Offset * MathF.Sign(k);
            k = MathF.Sign(k) / MathF.Max(r, 4f);
        }
        float wheel = MathF.Atan(bot.Car.Wheelbase * k) * 180 / MathF.PI;
        wheel = Math.Clamp(wheel + bot.SteerExtraDeg, -32, 32);

        var (gear, rpm) = GearAndRpm(bot);
        // launch: revs held up by the slipping clutch (and the spinning wheels), or dropping when it bogs down
        if (Settings.RealisticStart && !double.IsNaN(bot.LaunchAt) && bot.LapsCompleted == 0 && bot.Speed < 25 && _now - bot.LaunchAt < 3 && _now >= bot.LaunchAt)
        {
            float tl = (float)(_now - bot.LaunchAt);
            float share = bot.Launch switch { LaunchKind.Wheelspin => 0.88f, LaunchKind.Bog => tl < 0.6f ? 0.35f : 0.6f, _ => 0.68f };
            rpm = Math.Max(rpm, (int)(bot.Car.MaxRpm * share));
        }
        float full = bot.Car.AccelAt(bot.Speed, bot.Driver.Pace);
        byte throttle = (byte)Math.Clamp(bot.Throttle * 255f, 0, 255);
        _ = full;
        if (bot.Phase == BotPhase.Grid) throttle = 40; // blipping on the grid
        bool hazards = (bot.Phase == BotPhase.Racing && bot.Speed < 5 && !double.IsNaN(bot.StoppedSince) && _now - bot.StoppedSince > 3)
                       || (bot.YellowHazards && bot.Phase == BotPhase.Racing)
                       || (bot.Mistake == MistakeKind.Spin && bot.SpinPhase >= 1);
        // two short flashes
        bool flash = _now < bot.FlashUntil && (bot.FlashUntil - _now) % 0.45 > 0.22;
        // pit limiter on: GT3 cars flash their headlights in the pit lane (like the players' cars with CSP)
        if (Settings.PitLimiterFlash && bot.InPitLane && PitLane != null && bot.Pit != PitPhase.Stopped
            && bot.PitS >= PitLane.LimiterStart && bot.PitS <= PitLane.LimiterEnd)
            flash = (_now + bot.Id * 0.13) % 0.8 < 0.4;
        int indicator = hazards ? 0 : bot.Indicator;
        bool brake = bot.Brake > 0.04f || (bot.Phase == BotPhase.Grid) || (bot.Speed < 0.5f && bot.Phase == BotPhase.Racing);
        if (bot.Mistake == MistakeKind.Slide && _now - bot.MistakeStart < bot.MistakeDuration * 0.5) throttle = 230; // too much gas
        switch (SignalTestPhase(_now))
        {
            case 1: indicator = -1; hazards = false; break;
            case 2: indicator = 1; hazards = false; break;
            case 3: hazards = true; indicator = 0; break;
            case 4: flash = (_now - SignalTestStart) % 0.9 < 0.3; break;
            case 5: brake = true; break;
        }
        float spinning = bot.Mistake == MistakeKind.Spin && bot.SpinPhase == 0 ? 0.3f : 1f;
        return new BotPose(pos, rotation, vel, bot.Speed, wheel, gear, rpm,
            brake,
            Line.WrapS(s - Settings.StartLineS) / Line.Length,
            throttle, hazards, indicator, flash,
            SignalTestPhase(_now) is var ph && ph != 0 ? ph == 6 : bot.ClearAhead && bot.Mistake != MistakeKind.Spin,
            bot.FrontLock ? 0f : spinning, bot.RearSlip * spinning);
    }

    internal static (int Gear, int Rpm) GearAndRpm(RaceBot bot)
    {
        var car = bot.Car;
        float kmh = bot.Speed * 3.6f;
        if (kmh < 1) return (1, car.IdleRpm);
        var gears = car.GearTopSpeedsKmh;
        for (int g = 0; g < gears.Length; g++)
        {
            float shiftAt = car.UpshiftRpm > 0 && car.MaxRpm > 0 ? MathF.Min(0.99f, car.UpshiftRpm / (float)car.MaxRpm) : 0.97f;
            if (kmh <= gears[g] * shiftAt || g == gears.Length - 1)
            {
                float lowTop = g == 0 ? 0 : gears[g - 1];
                // rpm range used in a gear: from the rpm after the upshift to max
                float ratio = kmh / gears[g];
                float rpm = Math.Clamp(ratio, 0, 1) * car.MaxRpm;
                rpm = MathF.Max(rpm, g == 0 ? car.IdleRpm : car.MaxRpm * lowTop / gears[g]);
                return (g + 1, (int)rpm);
            }
        }
        return (gears.Length, car.MaxRpm);
    }
}
