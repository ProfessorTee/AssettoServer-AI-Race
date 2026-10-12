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
    /// <summary>World time (s) the position belongs to; the bots extrapolate from there to their own time.</summary>
    public double At { get; internal set; } = double.NaN;
    internal float PrevSpeed;
    internal double PrevAt = double.NaN;
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
            bot.Phase = BotPhase.Racing;
            bot.LapStartTime = now;
            bot.RaceStartTime = now;
            bot.TimingValid = true;
            // AC (stepGasBrakeStart): a reaction time of 50-400 ms
            bot.LaunchAt = now + 0.05 + 0.35 * _rng.NextDouble();
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
        car.LateralSpeed = Vector3.Dot(velocity, Line.LateralAt(p.S));
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

    /// <summary>A human car touched this bot (the client's collision message): the bot's car is knocked, like a contact in the physics.</summary>
    public void OnContact(RaceBot bot, Vector3 otherPosition, float impactSpeed, ExternalCar? other = null)
    {
        if (!bot.OnTrack) return;
        var p = Line.Project(otherPosition, Line.IndexAt((float)bot.Distance));
        float dOff = bot.Offset - p.Offset;
        float ds = Line.Delta(p.S, Line.WrapS((float)bot.Distance)); // > 0: the bot is ahead
        float impact = Math.Clamp(impactSpeed, 0, 30);
        bool side = MathF.Abs(dOff) > 0.6f * bot.Car.Width && MathF.Abs(ds) < bot.Car.Length;
        if (side) bot.LateralSpeed = MathF.Sign(dOff) * (0.6f + 0.08f * impact);
        else if (ds > 0) bot.Speed += MathF.Min(2, 0.3f * impact);
        else bot.Speed = MathF.Max(0, bot.Speed - 0.6f * impact);
        bot.LastContactAt = _now;
        bot.ContactCount++;
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
                    break;
            }
        }

        ResolveOverlaps();
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
                S = Line.WrapS(s), Offset = e.Offset + e.LateralSpeed * dt, Speed = v,
                Length = e.Length, Width = e.Width,
                Progress = e.Laps * Line.Length + Line.WrapS(e.S - Settings.StartLineS)
            });
        }
    }

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

    /// <summary>
    /// The cars are bodies of their own (AC's collisions live on the clients): after moving, anything overlapping is pushed apart. Side by
    /// side the two slide apart, nose to tail the car behind falls back, a player behind pushes the bot.
    /// </summary>
    private void ResolveOverlaps()
    {
        foreach (var me in Bots)
        {
            if (me.Phase is not (BotPhase.Racing or BotPhase.CoolDown) || OffTrackInPits(me)) continue;
            float myS = Line.WrapS((float)me.Distance);
            foreach (var o in _neighbors)
            {
                if (o.IsBot && o.Id == me.Id) continue;
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
                // touching: the AC AI's spring to its line rests for a second (AC: car+0x3508, the last collision)
                me.LastOverlapAt = _now;
                if (o.IsBot) o.Bot!.LastOverlapAt = _now;

                if (o.IsBot && latPen > 0.03f && longPen > 0.05f)
                {
                    bool side = latPen < longPen;
                    if (side || ds > 0) BotContact(me, o.Bot!, side, dOff);
                    else BotContact(o.Bot!, me, false, -dOff);
                }
                if (latPen < longPen && MathF.Abs(dOff) > 0.05f)
                {
                    // side by side: slide apart, at most 8 cm per step (a deep overlap resolved at once is a jump on the clients' screens)
                    float push = MathF.Min(o.IsBot ? latPen / 2 : latPen, 0.08f);
                    PushSideways(me, -MathF.Sign(dOff) * push);
                    if (o.IsBot) PushSideways(o.Bot!, MathF.Sign(dOff) * push);
                    float otherLat = o.IsBot ? o.Bot!.LateralSpeed : o.External!.LateralSpeed;
                    if ((me.LateralSpeed - otherLat) * MathF.Sign(dOff) > 0) me.LateralSpeed = otherLat - MathF.Sign(dOff) * 0.3f;
                    if (o.IsBot && (o.Bot!.LateralSpeed - me.LateralSpeed) * MathF.Sign(dOff) < 0) o.Bot.LateralSpeed = me.LateralSpeed + MathF.Sign(dOff) * 0.3f;
                }
                else if (ds > 0)
                {
                    // I'm behind: fall back (at most 30 cm per step, the speed does the rest)
                    me.Distance -= MathF.Min(longPen, 0.3f);
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

    /// <summary>Pushed sideways by another car: not past what the car may use beside the road, but never pulled back from beyond it.</summary>
    private void PushSideways(RaceBot bot, float delta)
    {
        float s = Line.WrapS((float)bot.Distance);
        float half = bot.Car.Width / 2, allow = Settings.GrassAllowance;
        float limited = Math.Clamp(bot.Offset + delta, -Line.RoomMinusAt(s) + half - allow, Line.RoomPlusAt(s) - half + allow);
        bot.Offset = delta > 0 ? MathF.Max(bot.Offset, limited) : MathF.Min(bot.Offset, limited);
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
        Line.Interp(s, out var a, out var b, out var t);
        var pos = Line.PositionAt(s, bot.Offset);
        var normal = Vector3.Normalize(Vector3.Lerp(Line.Normal[a], Line.Normal[b], t));
        pos += normal * (Settings.HeightOffset + (Heights?.At(s, bot.Offset) ?? 0));

        var fwd = Line.ForwardAt(s);
        var lat = Line.LateralAt(s);
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
        // (the body's own roll and pitch are the clients' business: their physics animate the suspension)
        var rotation = new Vector3(
            MathF.Atan2(dir.Z, dir.X) - MathF.PI / 2,
            (MathF.Atan2(new Vector2(dir.Z, dir.X).Length(), dir.Y) - MathF.PI / 2) * -1f,
            Line.CamberAt(s));
        if (bot.Phys != null && bot.Car.Physics is { } pd && Road != null)
            OnWheels(ref pos, ref rotation, dir, lat, pd);

        float wheel = Math.Clamp((bot.Phys?.Steer ?? 0) * 180 / MathF.PI, -32, 32);
        var (gear, rpm) = GearAndRpm(bot);
        byte throttle = (byte)Math.Clamp(bot.Throttle * 255f, 0, 255);
        if (bot.Phase == BotPhase.Grid) throttle = 40; // blipping on the grid
        bool hazards = bot.Phase == BotPhase.Racing && bot.Speed < 5 && !double.IsNaN(bot.StoppedSince) && _now - bot.StoppedSince > 3;
        bool brake = bot.Brake > 0.04f || bot.Phase == BotPhase.Grid || (bot.Speed < 0.5f && bot.Phase == BotPhase.Racing);
        return new BotPose(pos, rotation, vel, bot.Speed, wheel, gear, rpm, brake,
            Line.WrapS(s - Settings.StartLineS) / Line.Length,
            throttle, hazards, bot.ClearAhead,
            bot.FrontLock ? 0f : 1f, bot.RearSlip);
    }

    /// <summary>
    /// The car standing on the real road under its four wheels (kerbs, crown, bumps; the road mesh, not the AI line's flat road): height from
    /// their mean, pitch from front against rear, roll from side to side. A wheel off the road stands on the grass/gravel mesh, beyond all meshes on the line's plane.
    /// </summary>
    private void OnWheels(ref Vector3 pos, ref Vector3 rotation, Vector3 dir, Vector3 lat, Physics.VehicleData d)
    {
        var fh = Vector3.Normalize(new Vector3(dir.X, 0, dir.Z));
        var side = Vector3.Normalize(new Vector3(-fh.Z, 0, fh.X));
        if (Vector3.Dot(side, lat) < 0) side = -side;
        var c = pos;
        float baseY = c.Y - Settings.HeightOffset;
        float H(float x, float y)
        {
            var p = c + fh * x + side * y;
            float lineY = baseY + lat.Y * y + dir.Y * x;
            return Road!.HeightAt(p.X, p.Z, lineY) ?? OffTrack?.HeightAt(p.X, p.Z, lineY) ?? lineY;
        }
        float fp = H(d.A, d.TrackFront / 2), fm = H(d.A, -d.TrackFront / 2), rp = H(-d.B, d.TrackRear / 2), rm = H(-d.B, -d.TrackRear / 2);
        pos.Y = (fp + fm + rp + rm) / 4 + Settings.HeightOffset;
        rotation.Y = MathF.Atan2((fp + fm - rp - rm) / 2, d.A + d.B);
        // (the same sign as the line's camber: asin of the lateral's tilt, checked on TM/NS/RBR)
        float track = (d.TrackFront + d.TrackRear) / 2;
        rotation.Z = MathF.Asin(Math.Clamp((fp + rp - fm - rm) / 2 / track, -0.5f, 0.5f));
    }

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
        if (bot.Phys is { } st && !bot.InPitLane && bot.Gear > 0)
            return (bot.ShiftTo > 0 && _now < bot.ShiftEnd ? bot.ShiftTo : bot.Gear, (int)Math.Clamp(st.EngineRpm, car.IdleRpm, car.MaxRpm));
        float kmh = bot.Speed * 3.6f;
        if (kmh < 1) return (1, car.IdleRpm);
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
