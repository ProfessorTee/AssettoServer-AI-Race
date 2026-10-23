using TrackGeometry;
using System.Numerics;

namespace BotDriverPlugin.Core;

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

/// <summary>The AC AI's settings for one driver (race.ini AI_LEVEL / AI_AGGRESSION).</summary>
public sealed class DriverProfile
{
    /// <summary>AC AI level (ai+0x28): 1 = the car's limit as the AI plans it; from the strength calibration (may go a little above 1).</summary>
    public float Pace { get; set; } = 1f;
    /// <summary>AC AI aggression 0..1 (ai+0x1c, launcher value × 0.95): only matters for committing to a pass.</summary>
    public float Aggression { get; set; } = 0.5f;
    /// <summary>AI strength in percent, for display.</summary>
    public float Level { get; set; } = 100;

    public static DriverProfile FromStrength(float strengthPercent, float pace, float aggressionPercent)
        => new() { Level = strengthPercent, Pace = pace, Aggression = Math.Clamp(aggressionPercent / 100f, 0, 1) * 0.95f };
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
    /// <summary>Heading against the line (rad, + = turned towards positive offset).</summary>
    public float Psi { get; internal set; }
    /// <summary>World time (s) the position belongs to; the bots extrapolate from there to their own time.</summary>
    public double At { get; internal set; } = double.NaN;
    internal float PrevSpeed;
    internal double PrevAt = double.NaN;
    /// <summary>
    /// What a contact with a bot did to this car (line frame, m/s) until its own packets show it: its client has bounced it off at once, the
    /// server only sees that later; without it the car would go on pushing the bot until then as if it were immovable.
    /// </summary>
    internal Vector2 Bounce;
    internal double BounceUntil = double.NegativeInfinity;
    /// <summary>Completed laps of this car in the current race (set from outside).</summary>
    public int Laps { get; set; }
    internal int HintIndex = -1;
}

public sealed class RaceBot
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public CarSpec Car { get; set; } = new();
    public DriverProfile Driver { get; init; } = new();
    public BotPhase Phase { get; set; } = BotPhase.Hidden;

    /// <summary>Unwrapped distance along the line (grows by the line length every lap).</summary>
    public double Distance { get; set; }
    public float Offset { get; set; }
    public float Speed { get; set; }
    public float LateralSpeed { get; internal set; }
    /// <summary>Where the AI wants to be (offset from the line) and how fast (its plan, before the pedals).</summary>
    public float TargetOffset { get; set; }
    public float TargetSpeed { get; internal set; }
    public float Accel { get; internal set; }
    /// <summary>Pedals 0..1.</summary>
    public float Throttle { get; internal set; }
    public float Brake { get; internal set; }

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

    /// <summary>Gear engaged (1 = first, 0 = not set yet) and the gear change in progress (target gear, when it's in).</summary>
    internal int Gear;
    internal int ShiftTo;
    internal double ShiftEnd;

    public bool OnTrack => Phase is BotPhase.Grid or BotPhase.Racing or BotPhase.CoolDown;

    public Vector3 ParkPosition { get; internal set; }
    public Vector3 ParkForward { get; internal set; } = Vector3.UnitZ;
    /// <summary>Time the bot has been (nearly) standing still while racing, for the stuck detection.</summary>
    internal double StoppedSince = double.NaN;
    /// <summary>Clutch up at the start (reaction time).</summary>
    internal double LaunchAt = double.NaN;
    /// <summary>
    /// The start, rolled once on the grid: lets the clutch go this many seconds before the lights (jump start, 0 = none), and floors it with
    /// no feel for the grip until <see cref="LaunchSpinUntil"/> (wheelspin; NaN = not rolled yet).
    /// </summary>
    internal float JumpLead = float.NaN, SpinFor;
    internal double LaunchSpinUntil = double.NegativeInfinity;
    /// <summary>The pit stop requested is a drive-through penalty.</summary>
    internal bool DriveThrough;

    // ---- the vehicle model and the AC AI (RaceWorld.Kunos.cs)
    internal Physics.VehicleState? Phys;
    internal KunosAi? Ai;
    /// <summary>The four tyres' temperatures and pressures (FL, FR, RL, RR as the vehicle model's wheels 0-3), null without a vehicle model.</summary>
    public Physics.TyreHeat[]? Heat;
    /// <summary>
    /// How this driver sets the pressures: + wants the car to turn (rear off its ideal pressure), − wants it stable (front off its
    /// ideal); from the aggression, NaN = not chosen yet.
    /// </summary>
    public float PressureStyle = float.NaN;
    /// <summary>Air and road temperature when the pressures were set (°C).</summary>
    public float PressureSetAmbient, PressureSetRoad;
    /// <summary>Visual yaw of the body against the direction of travel (rad).</summary>
    public float Yaw { get; internal set; }
    internal bool FrontLock;
    internal float RearSlip = 1f;
    /// <summary>A car alongside (last decision).</summary>
    internal bool Alongside;
    /// <summary>The corner the AI brakes for: how far ahead and its speed (for the brake help).</summary>
    internal float LimitDist = float.PositiveInfinity, LimitSpeed;
    internal double LastContactAt = double.NegativeInfinity;
    /// <summary>Last time the car overlapped another one (touching).</summary>
    internal double LastOverlapAt = double.NegativeInfinity;
    internal int LastContactWith = -1;
    public int ContactCount { get; internal set; }
    public int Overtakes { get; internal set; }
    /// <summary>Emergency ghost (no collisions) until this time.</summary>
    public double GhostUntil { get; internal set; } = double.NegativeInfinity;
    public int GhostCount { get; internal set; }

    // ---- endurance
    /// <summary>Fuel in the tank (litres).</summary>
    public float Fuel { get; set; } = 30f;
    /// <summary>Tyre wear in Kunos "virtual km" of the current set.</summary>
    public float TyreVirtualKm { get; set; }
    /// <summary>The compound on the car (null = the car's default compound) and the ones the server allows (LEGAL_TYRES, "" = all).</summary>
    public Compound? Tyres { get; set; }
    public string LegalTyres { get; set; } = "";
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
    /// <summary>The AI's tyre threshold: stops for new tyres below this much grip left (AC: ai+0x204, 0.95..0.97).</summary>
    internal float TyreStopGrip = 0.96f;
    internal float FuelPerKmEma;
    internal float FuelAtLapStart = -1;
    internal float FuelAddedThisLap;
    internal bool PittedThisLap;
    /// <summary>Fuel used on the last complete lap (litres), 0 = not measured yet.</summary>
    public float LastLapFuel { get; internal set; }
    /// <summary>Grip factor from tyres (wear, temperature), fuel load and damage, 1 = new warm tyres and light car.</summary>
    public float CarGrip { get; internal set; } = 1f;
    internal float MassRatio = 1f;

    public bool InPitLane => Pit is PitPhase.InLane or PitPhase.Stopped;

    // ---- damage (RaceWorld.Damage.cs)
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
    bool HighBeam = false,
    float FrontTyreFactor = 1,
    float RearTyreFactor = 1);

public sealed class RaceWorldSettings
{
    /// <summary>Distance along the racing line where the start/finish line is.</summary>
    public float StartLineS { get; set; } = 0;
    /// <summary>Distance kept to the track edge when overtaking (m).</summary>
    public float EdgeMargin { get; set; } = 0.4f;
    /// <summary>How far beyond the track edge (m, car centre past the edge minus half its width) a car may get before it is held.</summary>
    public float GrassAllowance { get; set; } = 1.2f;
    /// <summary>Height of the track's AC_START_x / AC_PIT_x dummies above the ground (m), subtracted when parking in the pit box.</summary>
    public float SpotHeightOffset { get; set; }
    /// <summary>High beams only when nobody is within this distance ahead (m).</summary>
    public float HighBeamRange { get; set; } = 85f;
    /// <summary>Fine tuning of the height of cars parked in the pit box (m, + = higher).</summary>
    public float ParkHeightAdjust { get; set; }
    /// <summary>Lift the car this far above the line (m).</summary>
    public float HeightOffset { get; set; } = 0f;
    /// <summary>In-lap after the flag: share of the corner speed and a speed cap (CSP's AI_RACE_COOLDOWN: 120 km/h).</summary>
    public float CoolDownPace { get; set; } = 0.6f;
    /// <summary>Respect the track's ai_hints.ini (HINT_ radius factors, MAXSPEED_), like the Kunos AI does.</summary>
    public bool UseTrackHints { get; set; } = true;
    /// <summary>Scale on the AI's extra lateral grip (the car's ai.ini ULTRA_GRIP, AC default 1.2). 1 = like the AC AI.</summary>
    public float PhysicsCorneringGrip { get; set; } = 1f;
    /// <summary>Overall grip of the track surface (dynamic track grip, rain). 1 = dry, fully rubbered in.</summary>
    public float GripFactor { get; set; } = 1f;
    /// <summary>The AC AI's random braking mistakes (stepHumanizer): every 20-120 s up to 2 s of doubled brake pressure without ABS.</summary>
    public bool HumanErrors { get; set; } = true;
    /// <summary>The AC AI's push variation over time (stepPushVariations) and genome (stronger and weaker parts of the lap).</summary>
    public bool PushVariation { get; set; } = true;
    /// <summary>Race start time.</summary>
    public double RaceStartTime { get; set; } = double.NegativeInfinity;
    /// <summary>When the lights go out (world time, +∞ = unknown) and the server's START_RULE (0 car locked, 1 back to the pits, 2 drive-through).</summary>
    public double GreenAt { get; set; } = double.PositiveInfinity;
    public int StartRule { get; set; }
    public int Seed { get; set; } = Environment.TickCount;

    // ---- endurance
    /// <summary>Fuel consumption multiplier (server FUEL_RATE / 100). 0 = no fuel use.</summary>
    public float FuelRate { get; set; } = 1f;
    /// <summary>Tyre wear multiplier (server TYRE_WEAR_RATE / 100). 0 = no wear.</summary>
    public float TyreWearRate { get; set; } = 1f;
    /// <summary>Virtual km per real km at average load (Kunos virtual km grow with tyre slip).</summary>
    public float TyreWearScale { get; set; } = 0.15f;
    public bool PitStops { get; set; } = true;
    /// <summary>Pit lane speed limit (m/s).</summary>
    public float PitSpeedLimit { get; set; } = 80 / 3.6f;
    /// <summary>Mandatory pit stop between these laps (race), 0/0 = none.</summary>
    public int PitWindowStart { get; set; }
    public int PitWindowEnd { get; set; }
    /// <summary>Litres a bot keeps as reserve when planning.</summary>
    public float FuelReserve { get; set; } = 2f;

    /// <summary>Contacts between bots (bounce, damage).</summary>
    public bool BotContacts { get; set; } = true;
    /// <summary>Damage from contacts (slower car, repaired in the pits).</summary>
    public bool Damage { get; set; } = true;
    /// <summary>Damage multiplier (server DAMAGE_MULTIPLIER / 100).</summary>
    public float DamageRate { get; set; } = 1f;

    // ---- weather
    /// <summary>Track wetness and standing water (0..1, from the server's weather).</summary>
    public float Wetness { get; set; }
    public float Water { get; set; }
    /// <summary>Air and track temperature (°C), for the tyre temperatures.</summary>
    public float AmbientTemp { get; set; } = 20;
    /// <summary>Tyre blankets allowed (server_cfg.ini TYRE_BLANKETS_ALLOWED, as for the players): tyres leave the pits at blanket temperature.</summary>
    public bool TyreBlankets { get; set; }
    public float RoadTemp { get; set; } = 28;
    /// <summary>Server extra_cfg RainTrackGripReductionPercent (0..0.5): the server already lowers the grip for everybody.</summary>
    public float ServerRainReduction { get; set; }
    /// <summary>Grip lost on a wet track (1 = normal).</summary>
    public float RainGripLoss { get; set; } = 1f;
    /// <summary>The current session is a race / a qualifying (AC: fuel, tyre choice).</summary>
    public bool IsRace { get; set; } = true;
    public bool Qualifying { get; set; }
}

/// <summary>
/// The bots: every car is driven by the Assetto Corsa singleplayer AI (acs.exe AIDriver, see UEBERGABE_AC_KI.md) on a vehicle model
/// after AC's physics (Core/Physics). Pure logic without any AssettoServer dependencies, so it can be tested offline.
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
    /// <summary>A penalty for a bot (what for).</summary>
    public event Action<RaceBot, string>? Penalty;
    public PitLane? PitLane { get; set; }
    /// <summary>Real road height beside the line (null: the line's flat road).</summary>
    public LineHeights? Heights { get; set; }
    /// <summary>Surfaces beside the track (null: every run-off is grass).</summary>
    public RoadSurface? OffTrack { get; set; }
    /// <summary>The road under the cars (bumps, kerbs, surfaces), null = the line's flat road.</summary>
    public RoadSurface? Road { get; set; }
    /// <summary>Every bot contact (test tool).</summary>
    public Action<string>? ContactLog { get; set; }
    /// <summary>Stuck (standing in the way for this long while racing): no collisions for a moment to get out. 0 = never.</summary>
    public float GhostAfter { get; set; } = 20f;

    /// <summary>Debug: bot id whose state is written to <see cref="Trace"/> four times a second.</summary>
    public int TraceBotId { get; set; } = -1;
    public Action<string>? Trace { get; set; }

    private readonly Random _rng;
    private readonly List<Neighbor> _neighbors = [];
    private double _now;

    private struct Neighbor
    {
        public int Id;
        public bool IsBot;
        public float S, Offset, Speed, Length, Width;
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
        // grid spots are behind the start line: keep the distance negative so the first crossing starts lap 1
        float rel = Line.Delta(Settings.StartLineS, p.S);
        PlaceAt(bot, Settings.StartLineS + rel, p.Offset, phase);
    }

    public void PlaceAt(RaceBot bot, double distance, float offset, BotPhase phase)
    {
        bot.Distance = distance;
        bot.Offset = offset;
        bot.TargetOffset = offset;
        bot.Speed = 0;
        bot.LateralSpeed = 0;
        bot.Yaw = 0;
        bot.Phys = null; // the vehicle model starts again from here
        bot.Phase = phase;
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
        bot.LaunchAt = double.NaN;
        bot.JumpLead = float.NaN;
        bot.SpinFor = 0;
        bot.LaunchSpinUntil = double.NegativeInfinity;
        bot.DriveThrough = false;
        bot.Gear = 0;
        bot.ShiftTo = 0;
    }

    /// <summary>Parks a bot at a fixed position (e.g. its pit box). It is no longer an obstacle and does not drive.</summary>
    public void Park(RaceBot bot, Vector3 position, Vector3 forward)
    {
        bot.Phase = BotPhase.Parked;
        bot.ParkPosition = OnGround(position);
        bot.ParkForward = forward.LengthSquared() > 0.01f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        bot.Speed = 0;
        bot.Phys = null;
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
            bots[k].Speed = MathF.Min(PlanSpeed(bots[k], 0) * 0.8f, 40);
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
            Launch(bot, now, now + 0.05 + 0.35 * _rng.NextDouble()); // AC (stepGasBrakeStart): a reaction time of 50-400 ms
        }
    }

    private void Launch(RaceBot bot, double green, double clutchAt)
    {
        bot.Phase = BotPhase.Racing;
        bot.LapStartTime = green;
        bot.RaceStartTime = green;
        bot.TimingValid = true;
        bot.LaunchAt = clutchAt;
        bot.LaunchSpinUntil = clutchAt + bot.SpinFor;
    }

    /// <summary>
    /// On the grid, a few seconds before the lights: how this driver will get away. Weaker drivers more often spin the wheels (chance
    /// 1.5 × (1 − level), up to 60 %, for 0.4-2 s) and now and then jump the start (0.25 × (1 − level), 0.1-0.5 s early; not where the
    /// server locks the cars). A jump start gets the server's penalty right away: back to the pits (START_RULE 1) or a drive-through (2).
    /// </summary>
    private void GridStart(RaceBot bot)
    {
        double toGreen = Settings.GreenAt - _now;
        if (float.IsNaN(bot.JumpLead) && toGreen < 5)
        {
            float miss = Settings.HumanErrors ? 1 - Math.Clamp(bot.Driver.Pace, 0, 1) : 0;
            bot.SpinFor = _rng.NextSingle() < MathF.Min(0.6f, 1.5f * miss) ? 0.4f + 1.6f * _rng.NextSingle() * (0.5f + miss) : 0;
            bot.JumpLead = Settings.StartRule != 0 && _rng.NextSingle() < 0.25f * miss ? 0.1f + 0.4f * _rng.NextSingle() : 0;
        }
        if (bot.JumpLead > 0 && toGreen <= bot.JumpLead)
        {
            Launch(bot, Settings.GreenAt, _now);
            Penalty?.Invoke(bot, Settings.StartRule == 1 ? "jump start: back to the pits" : "jump start: drive-through");
            if (Settings.StartRule == 1) ReleaseFromPitBox(bot);
            else RequestDriveThrough(bot);
        }
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
    /// <param name="forward">The car's heading (null: from its direction of travel).</param>
    public void UpdateExternal(ExternalCar car, Vector3 position, Vector3 velocity, bool active = true, double at = double.NaN, Vector3? forward = null)
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
        car.LateralSpeed = Vector3.Dot(velocity, Line.LateralAt(p.S));
        var f = forward ?? (velocity.LengthSquared() > 4 ? velocity : Line.Forward[p.Index]);
        car.Psi = MathF.Atan2(Vector3.Dot(f, Line.LateralAt(p.S)), Vector3.Dot(f, Line.Forward[p.Index]));
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

    /// <summary>
    /// A human car touched this bot (the client's collision message). The server finds most contacts itself from the cars' footprints
    /// (<see cref="ResolveContacts"/>); this one only counts where it missed it (the player's positions arrive late, on his screen the bot
    /// was somewhere else): then the bot gets the hit on the side facing the player's car, with the client's impact speed, as from a car as
    /// heavy as itself.
    /// </summary>
    public void OnContact(RaceBot bot, Vector3 otherPosition, float impactSpeed, ExternalCar? other = null)
    {
        if (!bot.OnTrack) return;
        bool seen = other != null && bot.LastContactWith == other.Id && _now - bot.LastContactAt < 0.5;
        bot.LastContactAt = _now;
        bot.LastOverlapAt = _now;
        if (other != null) bot.LastContactWith = other.Id;
        if (seen) return;
        bot.ContactCount++;
        if (bot.Phys is not { } st || bot.Car.Physics is not { } d) return;
        var p = Line.Project(otherPosition, Line.IndexAt((float)bot.Distance));
        // where the other car is, in the bot's body frame: the face it hit and the point on it
        var rel = new Vector2(Line.Delta(Line.WrapS((float)bot.Distance), p.S), p.Offset - bot.Offset);
        float c = MathF.Cos(st.Psi), sn = MathF.Sin(st.Psi);
        float x = rel.X * c + rel.Y * sn, y = -rel.X * sn + rel.Y * c;
        float hl = bot.Car.Length / 2, hw = bot.Car.Width / 2;
        Vector2 nBody, rBody;
        if (MathF.Abs(x) / hl > MathF.Abs(y) / hw) { nBody = new(-MathF.Sign(x), 0); rBody = new(MathF.Sign(x) * hl, Math.Clamp(y, -hw, hw)); }
        else { nBody = new(0, -MathF.Sign(y)); rBody = new(Math.Clamp(x, -hl, hl), MathF.Sign(y) * hw); }
        Vector2 ToLine(Vector2 v) => new(v.X * c - v.Y * sn, v.X * sn + v.Y * c);
        float m = d.Mass * bot.MassRatio;
        ApplyImpulse(bot, ToLine(nBody) * ((1 + ContactRestitution) * Math.Clamp(impactSpeed, 0, 30) * m / 2), ToLine(rBody));
    }

    // ------------------------------------------------------------------ simulation

    private double _lastAdvance = double.NaN;

    /// <summary>World step (s). The vehicle model runs at ~300 Hz inside it, the AI decides every 5th physics step like AC.</summary>
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

    public void Step(float dt, double now)
    {
        _now = now;
        foreach (var b in Bots) b.Car.SetTrack(Line.Length);
        BuildNeighbors();

        foreach (var bot in Bots)
        {
            // the AI plans with what the physics car can really do
            if (bot.Car.Physics != null && !bot.Car.PhysicsTuned) bot.Car = bot.Car.ForPhysics();
            switch (bot.Phase)
            {
                case BotPhase.Racing:
                case BotPhase.CoolDown:
                    if (bot.Car.Physics == null) { bot.Speed = 0; break; } // no car data: can't drive
                    if (bot.Speed < 1f) { if (double.IsNaN(bot.StoppedSince)) bot.StoppedSince = now; }
                    else if (bot.Speed > 5f) bot.StoppedSince = double.NaN;
                    WatchStuck(bot);
                    if (bot.InPitLane)
                    {
                        PitStep(bot, dt);
                    }
                    else
                    {
                        Drive(bot, dt);
                        CheckPitEntry(bot);
                    }
                    UpdateCarCondition(bot, dt);
                    UpdateClearAhead(bot);
                    break;
                case BotPhase.Grid:
                    bot.Speed = 0;
                    bot.LateralSpeed = 0;
                    GridStart(bot);
                    break;
            }
        }

        ResolveContacts();
        foreach (var bot in Bots)
            if (bot.Phase is BotPhase.Racing or BotPhase.CoolDown)
                UpdateTiming(bot);
    }

    private void BuildNeighbors()
    {
        _neighbors.Clear();
        foreach (var b in Bots)
        {
            if (!b.OnTrack) continue;
            _neighbors.Add(new Neighbor
            {
                Id = b.Id, IsBot = true, Bot = b,
                S = Line.WrapS((float)b.Distance), Offset = b.Offset, Speed = b.Speed,
                Length = Turned(b.Car.Length, b.Car.Width, b.Phys?.Psi ?? 0), Width = Turned(b.Car.Width, b.Car.Length, b.Phys?.Psi ?? 0),
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
                S = Line.WrapS(s), Offset = e.Offset + e.LateralSpeed * dt, Speed = v,
                Length = Turned(e.Length, e.Width, e.Psi), Width = Turned(e.Width, e.Length, e.Psi),
                Progress = e.Laps * Line.Length + Line.WrapS(e.S - Settings.StartLineS)
            });
        }
    }

    /// <summary>How long a car's footprint is along the line (<paramref name="along"/> = its length) or wide across it, turned by its heading: a car
    /// spun across the road blocks its length.</summary>
    private static float Turned(float along, float across, float psi) => MathF.Abs(MathF.Cos(psi)) * along + MathF.Abs(MathF.Sin(psi)) * across;

    /// <summary>A player's car (external) in the pit lane.</summary>
    public bool ExternalInPitLane(int id)
    {
        foreach (var e in Externals)
            if (e.Id == id) return e.Valid && IsInPitLane(e);
        return false;
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

    private double NextGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Stuck: standing on the track for <see cref="GhostAfter"/> seconds (a car in its way, facing the wrong way): no collisions for a few
    /// seconds, and a car facing backwards is turned round.
    /// </summary>
    private void WatchStuck(RaceBot bot)
    {
        if (GhostAfter <= 0 || bot.InPitLane || double.IsNaN(bot.StoppedSince) || _now - bot.StoppedSince < GhostAfter) return;
        if (bot.GhostUntil > _now) return;
        bot.GhostUntil = _now + 4;
        bot.GhostCount++;
        bot.StoppedSince = double.NaN;
        if (bot.Phys is { } st && MathF.Abs(st.Psi) > 1.2f)
        {
            st.Psi = 0;
            st.Vy = 0;
            st.R = 0;
        }
    }

    // ------------------------------------------------------------------ contacts

    /// <summary>Car against car: restitution and friction of the bodywork (little bounce, sliding along), the overlap pushed out per step.</summary>
    private const float ContactRestitution = 0.3f, ContactFriction = 0.3f, MaxPushPerStep = 0.05f;

    /// <summary>A car's footprint and motion in the line's frame (s along, n across) for the contacts.</summary>
    private struct Body
    {
        public RaceBot? Bot;
        public ExternalCar? External;
        public int Id;
        public float S, N, Psi, Vs, Vn, W, HalfL, HalfW, InvM, InvI;
        public Vector2 U => new(MathF.Cos(Psi), MathF.Sin(Psi));
        public Vector2 V => new(-MathF.Sin(Psi), MathF.Cos(Psi));
        public float Radius(Vector2 axis) => HalfL * MathF.Abs(Vector2.Dot(U, axis)) + HalfW * MathF.Abs(Vector2.Dot(V, axis));
        /// <summary>Is <paramref name="p"/> (relative to this car's centre) inside the footprint.</summary>
        public bool Inside(Vector2 p) => MathF.Abs(Vector2.Dot(p, U)) <= HalfL && MathF.Abs(Vector2.Dot(p, V)) <= HalfW;
    }

    private bool BodyOf(in Neighbor o, out Body b)
    {
        b = default;
        if (o.IsBot)
        {
            var bot = o.Bot!;
            if (bot.Phase is not (BotPhase.Racing or BotPhase.CoolDown) || OffTrackInPits(bot) || bot.GhostUntil > _now) return false;
            if (!BotBody(bot, out b))
            {
                // no vehicle model (pit lane): it drives on its own line and doesn't give way
                b.Bot = bot;
                b.S = Line.WrapS((float)bot.Distance);
                b.N = bot.Offset;
                b.HalfL = bot.Car.Length / 2;
                b.HalfW = bot.Car.Width / 2;
                b.Vs = bot.Speed;
                b.Vn = bot.LateralSpeed;
            }
        }
        else
        {
            var e = o.External!;
            b.External = e;
            b.S = o.S;
            b.N = o.Offset;
            b.Psi = e.Psi;
            if (_now > e.BounceUntil) e.Bounce = Vector2.Zero;
            b.Vs = o.Speed + e.Bounce.X;
            b.Vn = e.LateralSpeed + e.Bounce.Y;
            b.HalfL = e.Length / 2;
            b.HalfW = e.Width / 2;
        }
        b.Id = o.Id;
        return true;
    }

    /// <summary>A bot's vehicle model as a body (false without one).</summary>
    private bool BotBody(RaceBot bot, out Body b)
    {
        b = default;
        if (bot.Phys is not { } st || bot.Car.Physics is not { } d) return false;
        float c = MathF.Cos(st.Psi), sn = MathF.Sin(st.Psi);
        b.Bot = bot;
        b.Id = bot.Id;
        b.S = Line.WrapS((float)bot.Distance);
        b.N = bot.Offset;
        b.HalfL = bot.Car.Length / 2;
        b.HalfW = bot.Car.Width / 2;
        b.Psi = st.Psi;
        b.Vs = st.Vx * c - st.Vy * sn;
        b.Vn = st.Vx * sn + st.Vy * c;
        // yaw rate against the line (its frame turns with the curvature)
        float k = Line.CurvatureAt(b.S);
        b.W = st.R - k * b.Vs / MathF.Max(0.2f, 1 - k * bot.Offset);
        b.InvM = 1 / (d.Mass * bot.MassRatio);
        b.InvI = 1 / (d.YawInertia * bot.MassRatio);
        return true;
    }

    /// <summary>
    /// The cars are bodies of their own (AC's collisions live on the clients): after every step two footprints that overlap (rectangles
    /// turned by their heading against the line) touch like rigid bodies: an impulse along the contact normal at the contact point and friction
    /// across it change speed, direction and yaw rate of the vehicle models, and the overlap is pushed out (at most 5 cm per step, so the clients
    /// see no jump). A player's car only moves on his own client (to him the bot is a moving wall): here it weighs as much as the bot, and only
    /// the bot takes its share.
    /// </summary>
    private void ResolveContacts()
    {
        // a ghost (stuck, no collisions for a moment) stays one while it still overlaps a car, else the two are stuck in each other
        foreach (var g in Bots)
        {
            if (!(g.GhostUntil > _now) || g.GhostUntil > _now + 0.1 || !BotBody(g, out var gb)) continue;
            foreach (var o in _neighbors)
                if (!(o.IsBot && o.Bot == g) && (o.IsBot ? BotBody(o.Bot!, out var ob) : BodyOf(o, out ob)) && Overlap(gb, ob, out _, out _, out _))
                {
                    g.GhostUntil = _now + 0.5;
                    break;
                }
        }
        for (int i = 0; i < _neighbors.Count; i++)
        {
            if (!BodyOf(_neighbors[i], out var a)) continue;
            for (int k = i + 1; k < _neighbors.Count; k++)
            {
                if (!BodyOf(_neighbors[k], out var b)) continue;
                // A is a bot with a vehicle model (the one that reacts), B the other
                if (a.InvM == 0 && b.InvM == 0) continue;
                if (a.InvM == 0) Touch(b, a); else Touch(a, b);
                BodyOf(_neighbors[i], out a);
            }
        }
    }

    /// <summary>
    /// Do the footprints overlap: separating axes (the four sides' directions), the one with the least overlap is the contact normal (from B
    /// to A); <paramref name="d"/> is A's centre from B's.
    /// </summary>
    private bool Overlap(in Body a, in Body b, out Vector2 d, out Vector2 n, out float depth)
    {
        d = new Vector2(Line.Delta(b.S, a.S), a.N - b.N);
        n = Vector2.Zero;
        depth = float.MaxValue;
        float reach = MathF.Sqrt(a.HalfL * a.HalfL + a.HalfW * a.HalfW) + MathF.Sqrt(b.HalfL * b.HalfL + b.HalfW * b.HalfW);
        if (d.LengthSquared() > reach * reach) return false;
        foreach (var axis in new[] { a.U, a.V, b.U, b.V })
        {
            float pen = a.Radius(axis) + b.Radius(axis) - MathF.Abs(Vector2.Dot(d, axis));
            if (pen <= 0) return false;
            if (pen < depth) { depth = pen; n = Vector2.Dot(d, axis) >= 0 ? axis : -axis; }
        }
        return true;
    }

    private void Touch(Body a, Body b)
    {
        if (!Overlap(a, b, out var d, out var n, out float depth)) return;
        // contact point (relative to B's centre): the middle of where the two overlap along the contact face, halfway into the overlap. Two
        // cars side by side touch along their sides (a push at the middle of that), a corner into a side at the corner
        var t0 = new Vector2(-n.Y, n.X);
        float ct = Vector2.Dot(d, t0), lo = MathF.Max(ct - a.Radius(t0), -b.Radius(t0)), hi = MathF.Min(ct + a.Radius(t0), b.Radius(t0));
        var p = t0 * ((lo + hi) / 2) + n * (b.Radius(n) - depth / 2);
        Vector2 ra = p - d, rb = p;

        var bot = a.Bot!;
        var other = b.Bot;
        bot.LastOverlapAt = _now;
        if (other != null) other.LastOverlapAt = _now;

        // a car held at the edge of the run-off (a wall, the end of the gravel) can't give way in that direction: it is the wall
        bool aWall = Pinned(bot, n.Y), bWall = other != null && Pinned(other, -n.Y);
        if (aWall && bWall) aWall = bWall = false;
        if (aWall)
        {
            if (other == null || b.InvM == 0) { MoveBy(bot, Vector2.Zero); return; }
            (a, b, n, ra, rb, bot, other) = (b, a, -n, rb, ra, other, bot);
            bWall = true;
        }
        // the other's share: a player's car or a car without a vehicle model counts as heavy as this one, a car against a wall as the wall
        float invMb = bWall ? 0 : b.InvM > 0 ? b.InvM : a.InvM, invIb = bWall ? 0 : b.InvI > 0 ? b.InvI : a.InvI;
        static float Cross(Vector2 r, Vector2 v) => r.X * v.Y - r.Y * v.X;
        var va = new Vector2(a.Vs - a.W * ra.Y, a.Vn + a.W * ra.X);
        var vb = new Vector2(b.Vs - b.W * rb.Y, b.Vn + b.W * rb.X);
        var rel = va - vb;
        float vn = Vector2.Dot(rel, n);
        // (between bots only with BotContacts; else they just slide apart)
        if (vn < 0 && (other == null || Settings.BotContacts))
        {
            float kn = a.InvM + a.InvI * Cross(ra, n) * Cross(ra, n) + invMb + invIb * Cross(rb, n) * Cross(rb, n);
            float j = -(1 + ContactRestitution) * vn / kn;
            var t = new Vector2(-n.Y, n.X);
            float kt = a.InvM + a.InvI * Cross(ra, t) * Cross(ra, t) + invMb + invIb * Cross(rb, t) * Cross(rb, t);
            float jt = Math.Clamp(-Vector2.Dot(rel, t) / kt, -ContactFriction * j, ContactFriction * j);
            var imp = n * j + t * jt;
            ApplyImpulse(bot, imp, ra);
            if (other != null && b.InvM > 0 && !bWall) ApplyImpulse(other, -imp, rb);
            if (b.External is { } ext)
            {
                ext.Bounce -= imp * invMb;
                ext.BounceUntil = _now + 0.2;
            }
            // (a hit, not the rubbing of two cars side by side; AC damage starts at 4 km/h)
            if (-vn > 1.1f) ContactEvent(a, b, -vn, ra, rb);
        }
        // push the overlap out: a bot takes all of it against a player (he can't be moved from here), half of it against another bot
        float corr = MathF.Min(MathF.Max(0, depth - 0.01f), MaxPushPerStep);
        float share = b.InvM > 0 && !bWall ? a.InvM / (a.InvM + b.InvM) : 1;
        MoveBy(bot, n * (corr * share));
        if (other != null && b.InvM > 0) MoveBy(other, -n * (corr * (1 - share)));
    }

    /// <summary>
    /// The car hit the edge of the run-off (a wall, the end of the gravel; <paramref name="back"/> = the sign of the way back to the road):
    /// an impulse at its corner out there against an immovable wall, restitution 0.2 and friction 0.4, so it bounces, scrubs speed and turns
    /// like against a barrier.
    /// </summary>
    private void WallHit(RaceBot bot, float back)
    {
        if (!BotBody(bot, out var b)) return;
        var n = new Vector2(0, back);
        // the corner furthest out
        var r = Vector2.Zero;
        for (int c = 0; c < 4; c++)
        {
            var q = b.U * ((c < 2 ? 1 : -1) * b.HalfL) + b.V * ((c % 2 == 0 ? 1 : -1) * b.HalfW);
            if (Vector2.Dot(q, n) < Vector2.Dot(r, n)) r = q;
        }
        var v = new Vector2(b.Vs - b.W * r.Y, b.Vn + b.W * r.X);
        float vn = Vector2.Dot(v, n);
        if (vn >= 0) return;
        static float Cross(Vector2 a, Vector2 c) => a.X * c.Y - a.Y * c.X;
        var t = new Vector2(1, 0);
        float j = -1.2f * vn / (b.InvM + b.InvI * Cross(r, n) * Cross(r, n));
        float jt = Math.Clamp(-Vector2.Dot(v, t) / (b.InvM + b.InvI * Cross(r, t) * Cross(r, t)), -0.4f * j, 0.4f * j);
        ApplyImpulse(bot, n * j + t * jt, r);
        bot.LastContactAt = _now;
    }

    /// <summary>Is the car held at the edge of what it may use beside the road, towards <paramref name="dir"/> (sign of the offset change).</summary>
    private bool Pinned(RaceBot bot, float dir)
    {
        float s = Line.WrapS((float)bot.Distance), half = bot.Car.Width / 2, allow = Settings.GrassAllowance;
        return dir > 0.3f ? bot.Offset >= Line.RoomPlusAt(s) - half + allow - 0.02f
             : dir < -0.3f && bot.Offset <= -Line.RoomMinusAt(s) + half - allow + 0.02f;
    }

    /// <summary>An impulse (line frame, N·s) at <paramref name="r"/> (line frame, from the car's centre) on a car's vehicle model.</summary>
    private void ApplyImpulse(RaceBot bot, Vector2 imp, Vector2 r)
    {
        if (bot.Phys is not { } st || bot.Car.Physics is not { } d) return;
        float m = d.Mass * bot.MassRatio, iz = d.YawInertia * bot.MassRatio;
        float c = MathF.Cos(st.Psi), s = MathF.Sin(st.Psi);
        st.Vx += (imp.X * c + imp.Y * s) / m;
        st.Vy += (-imp.X * s + imp.Y * c) / m;
        st.R += (r.X * imp.Y - r.Y * imp.X) / iz;
        // pushed off its line: the driver takes it from where he is now instead of steering straight back into the other car
        if (imp.Length() / m > 0.5f) AiOf(bot).DesiredOffset = st.N;
    }

    /// <summary>Moves a car (line frame) without the vehicle model taking it as a placement from outside.</summary>
    private static void MoveBy(RaceBot bot, Vector2 delta)
    {
        bot.Distance += delta.X;
        bot.Offset += delta.Y;
        if (bot.Phys is not { } st) return;
        st.S = bot.Distance;
        st.N = bot.Offset;
        st.WrittenOffset = bot.Offset;
    }

    /// <summary>High beams only with nobody ahead (players or bots); switched on a moment after the road got free.</summary>
    private void UpdateClearAhead(RaceBot me)
    {
        float myS = Line.WrapS((float)me.Distance);
        bool clear = !me.InPitLane;
        if (clear)
            foreach (var o in _neighbors)
            {
                if ((o.IsBot && o.Id == me.Id) || (o.IsBot && o.Bot!.InPitLane)) continue;
                float ds = Line.Delta(myS, o.S);
                if (ds > -3 && ds < Settings.HighBeamRange) { clear = false; break; }
            }
        if (!clear) { me.ClearAhead = false; me.ClearAheadSince = double.NaN; return; }
        if (double.IsNaN(me.ClearAheadSince)) me.ClearAheadSince = _now;
        me.ClearAhead = _now - me.ClearAheadSince > 1.5;
    }

    // ------------------------------------------------------------------ timing

    private void UpdateTiming(RaceBot bot)
    {
        long idx = (long)Math.Floor((bot.Distance - Settings.StartLineS) / Line.Length);
        if (idx <= bot.LapIndex) return;

        bot.LapIndex = idx;
        if (idx == 0 && bot.TimingValid && bot.LapsCompleted == 0 && bot.LastLapSeconds == 0 && !bot.StartCrossed)
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

    // ------------------------------------------------------------------ output

    /// <summary>Degrees between the car as shown and the track's direction (the pit lane not).</summary>
    public float HeadingError(RaceBot bot, in BotPose pose)
    {
        if (bot.InPitLane || bot.Phase is BotPhase.Parked or BotPhase.Hidden) return 0;
        float yaw = pose.Rotation.X + MathF.PI / 2;
        var f = Line.ForwardAt(Line.WrapS((float)bot.Distance));
        return MathF.Abs(MathF.IEEERemainder(yaw - MathF.Atan2(f.Z, f.X), 2 * MathF.PI)) * 180 / MathF.PI;
    }

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
        var (pos, normal, fwd, lat) = Frame(bot, s, heights: bot.Phys == null);
        Vector3 vel, dir;
        if (bot.Phys is { } ph)
        {
            // the vehicle model: moving along the line at its offset (1 − k·n of the line's speed), heading the line turned by its yaw
            vel = fwd * ph.SDot * MathF.Max(0.2f, 1 - Line.CurvatureAt(s) * bot.Offset) + lat * ph.NDot;
            dir = Rotate(fwd, lat, ph.Psi);
        }
        else
        {
            vel = fwd * bot.Speed + lat * bot.LateralSpeed;
            // heading from the direction of travel turned by the body's yaw against it; at least 4 m/s forward, so sliding sideways at a
            // standstill doesn't turn the car 90° for a moment
            dir = Rotate(Vector3.Normalize(fwd * MathF.Max(bot.Speed, 4f) + lat * bot.LateralSpeed), lat, bot.Yaw);
        }
        var rotation = new Vector3(
            MathF.Atan2(dir.Z, dir.X) - MathF.PI / 2,
            (MathF.Atan2(new Vector2(dir.Z, dir.X).Length(), dir.Y) - MathF.PI / 2) * -1f,
            Line.CamberAt(s));
        if (bot.Phys is { } body && bot.Car.Physics is { } vd)
        {
            // the body as the vehicle model has it: its CG height over the road under the car (kerbs and bumps through the wheels and springs)
            // and its own pitch and roll on top of the road's. The clients keep the sent height above the ground under the car's centre and hang
            // the wheels from the body, each within ±0.1 m of its rest (acs.exe NetCarStateProvider::step: hub at BASEY, contact at BASEY −
            // radius): the height has to be the car's own, the line's points are ~0.1 m lower (another car recorded them) and the wheels sank
            var p0 = Line.PositionAt(s, 0);
            float? road = Road?.HeightAt(p0.X, p0.Z, p0.Y);
            pos += road is { } h0 ? Vector3.UnitY * (h0 - p0.Y) + normal * body.H : normal * (body.H - vd.CgHeight);
            vel += normal * body.HDot;
            rotation.Y -= body.Pitch;
            rotation.Z += RollSign * body.Roll;
        }

        float wheel = Math.Clamp((bot.Phys?.Steer ?? 0) * 180 / MathF.PI, -32, 32);
        var (gear, rpm) = GearAndRpm(bot);
        byte throttle = (byte)Math.Clamp(bot.Throttle * 255f, 0, 255);
        if (bot.Phase == BotPhase.Grid) (gear, rpm, throttle) = GridRevs(bot);
        bool hazards = bot.Phase == BotPhase.Racing && bot.Speed < 5 && !double.IsNaN(bot.StoppedSince) && _now - bot.StoppedSince > 3;
        bool brake = bot.Brake > 0.04f || bot.Phase == BotPhase.Grid || (bot.Speed < 0.5f && bot.Phase == BotPhase.Racing);
        return new BotPose(pos, rotation, vel, bot.Speed, wheel, gear, rpm, brake,
            Line.WrapS(s - Settings.StartLineS) / Line.Length,
            throttle, hazards, bot.ClearAhead,
            bot.FrontLock ? 0f : 1f, bot.RearSlip);
    }

    /// <summary>The car's centre on the line's road plane at its offset (with <paramref name="heights"/>: plus the road's height beside the line
    /// from the 1 m grid), the road normal, forward and lateral.</summary>
    private (Vector3 Pos, Vector3 Normal, Vector3 Fwd, Vector3 Lat) Frame(RaceBot bot, float s, bool heights = true)
    {
        Line.Interp(s, out var a, out var b, out var t);
        var pos = Line.PositionAt(s, bot.Offset);
        var normal = Vector3.Normalize(Vector3.Lerp(Line.Normal[a], Line.Normal[b], t));
        pos += normal * (Settings.HeightOffset + (heights ? Heights?.At(s, bot.Offset) ?? 0 : 0));
        return (pos, normal, Line.ForwardAt(s), Line.LateralAt(s));
    }

    /// <summary>
    /// Revving on the grid: a blip now and then, the last 5 s holding the launch revs (a wobbling foot; a driver about to spin
    /// the wheels holds them a lot higher).
    /// </summary>
    private (int Gear, int Rpm, byte Throttle) GridRevs(RaceBot bot)
    {
        var car = bot.Car;
        float idle = car.IdleRpm, max = car.MaxRpm;
        double toGreen = Settings.GreenAt - _now;
        float wobble = MathF.Sin((float)(_now * 6.3 + bot.Id * 1.7)) * 0.5f + MathF.Sin((float)(_now * 2.3 + bot.Id)) * 0.5f;
        if (toGreen < 5)
        {
            // (shown in neutral: the clients turn the wheels with the revs of an engaged gear, they can't see a clutch held down)
            float launch = MathF.Min(max * 0.6f, 4500) * (bot.SpinFor > 0 ? 1.35f : 1);
            return (0, (int)Math.Clamp(launch * (1 + 0.06f * wobble), idle, max), (byte)(110 + 40 * wobble));
        }
        float phase = (float)((_now * 0.35 + bot.Id * 0.37) % 1);
        if (phase < 0.08f)
        {
            float b = MathF.Sin(phase / 0.08f * MathF.PI);
            return (0, (int)(idle + (max * 0.55f - idle) * b), (byte)(40 + 200 * b));
        }
        return (0, (int)idle, 0);
    }

    /// <summary>The vehicle model's roll (+ = the + offset side down) against the pose's (+ = that side up).</summary>
    internal static float RollSign = -1;

    private static Vector3 Rotate(Vector3 dir, Vector3 lat, float yaw)
    {
        if (MathF.Abs(yaw) < 1e-4f) return dir;
        var latH = lat - Vector3.Dot(lat, dir) * dir;
        if (latH.LengthSquared() < 1e-6f) return dir;
        latH = Vector3.Normalize(latH);
        return Vector3.Normalize(dir * MathF.Cos(yaw) + latH * MathF.Sin(yaw));
    }

    /// <summary>Gear and revs shown to the clients: the vehicle model's engine, in the pit lane the gear for the speed.</summary>
    internal (int Gear, int Rpm) GearAndRpm(RaceBot bot)
    {
        var car = bot.Car;
        // the clients turn the wheels with the revs of the gear shown: while the clutch slips pulling away (below 43 km/h) or the car
        // stands, neutral is shown, else the launch revs spin the wheels on their screens and smoke
        if (bot.Phys is { } st && !bot.InPitLane && bot.Gear > 0)
            return (st.ClutchLocked || bot.Speed > 12 ? (bot.ShiftTo > 0 && _now < bot.ShiftEnd ? bot.ShiftTo : bot.Gear) : 0, (int)Math.Clamp(st.EngineRpm, car.IdleRpm, car.MaxRpm));
        float kmh = bot.Speed * 3.6f;
        if (kmh < 1) return (0, car.IdleRpm);
        var gears = car.GearTopSpeedsKmh;
        int gear = gears.Length;
        for (int g = 0; g < gears.Length; g++)
            if (kmh <= gears[g] * 0.9f) { gear = g + 1; break; }
        float top = gears[gear - 1];
        float lowTop = gear == 1 ? 0 : gears[gear - 2];
        float rpm = MathF.Max(Math.Clamp(kmh / top, 0, 1) * car.MaxRpm, gear == 1 ? car.IdleRpm : car.MaxRpm * lowTop / top);
        return (gear, (int)rpm);
    }
}
