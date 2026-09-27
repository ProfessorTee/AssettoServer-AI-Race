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
    internal int HintIndex = -1;
}

public sealed class RaceBot
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public CarSpec Car { get; init; } = new();
    public DriverProfile Driver { get; init; } = new();
    public BotPhase Phase { get; set; } = BotPhase.Hidden;

    /// <summary>Unwrapped distance along the line (grows by the line length every lap).</summary>
    public double Distance { get; set; }
    public float Offset { get; set; }
    public float Speed { get; set; }
    public float LateralSpeed { get; internal set; }
    public float TargetOffset { get; set; }
    public float TargetSpeed { get; internal set; }
    public float Accel { get; internal set; }

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
    bool Hazards = false);

public sealed class RaceWorldSettings
{
    /// <summary>Distance along the racing line where the start/finish line is.</summary>
    public float StartLineS { get; set; } = 0;
    /// <summary>Distance kept to the track edge (m).</summary>
    public float EdgeMargin { get; set; } = 0.4f;
    /// <summary>Extra lateral space kept to other cars (m).</summary>
    public float SideMargin { get; set; } = 0.5f;
    /// <summary>Use the speed recorded in fast_lane.ai as an upper limit, scaled by this factor. 0 = off.</summary>
    public float SpeedHintScale { get; set; } = 0f;
    /// <summary>Lift the car this far above the line (m).</summary>
    public float HeightOffset { get; set; } = 0f;
    public float CoolDownPace { get; set; } = 0.6f;
    /// <summary>Respect the track's ai_hints.ini (slower sections / max speeds), like the Kunos AI does.</summary>
    public bool UseTrackHints { get; set; } = true;
    /// <summary>Overall grip of the track surface (dynamic track grip, rain). 1 = dry, fully rubbered in.</summary>
    public float GripFactor { get; set; } = 1f;
    /// <summary>Maximum share of aero drag removed when directly behind another car (0 = no slipstream).</summary>
    public float SlipstreamStrength { get; set; } = 0.35f;
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
    }

    /// <summary>Parks a bot at a fixed position (e.g. its pit box). It is no longer an obstacle and does not drive.</summary>
    public void Park(RaceBot bot, Vector3 position, Vector3 forward)
    {
        bot.Phase = BotPhase.Parked;
        bot.ParkPosition = position;
        bot.ParkForward = forward.LengthSquared() > 0.01f ? Vector3.Normalize(forward) : Vector3.UnitZ;
        bot.Speed = 0;
        bot.OvertakeTargetId = -1;
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
            // small reaction time spread, better drivers react quicker
            bot.CautiousUntil = now + 0.15 + (1 - bot.Driver.Consistency) * _rng.NextDouble() * 0.5;
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
    public void UpdateExternal(ExternalCar car, Vector3 position, Vector3 velocity, bool active = true)
    {
        if (!active)
        {
            car.Valid = false;
            return;
        }

        var p = Line.Project(position, car.HintIndex);
        car.HintIndex = p.Index;
        car.S = p.S;
        car.Offset = p.Offset;
        car.Speed = Vector3.Dot(velocity, Line.Forward[p.Index]);
        int i = p.Index;
        // only relevant while on (or near) the track surface
        car.Valid = p.Offset > -Line.RoomMinus[i] - 4 && p.Offset < Line.RoomPlus[i] + 4 && MathF.Abs(p.Height) < 6;
    }

    /// <summary>A human car touched this bot. The bot gives way: loses some speed and moves away.</summary>
    public void OnContact(RaceBot bot, Vector3 otherPosition, float impactSpeed)
    {
        if (!bot.OnTrack) return;
        bot.Contacts++;
        var p = Line.Project(otherPosition, Line.IndexAt((float)bot.Distance));
        float dOff = bot.Offset - p.Offset;
        float ds = Line.Delta(p.S, Line.WrapS((float)bot.Distance));
        float loss = Math.Clamp(impactSpeed / 60f, 0.03f, 0.3f);
        if (ds < -1) // hit from the front: the bot ran into someone -> bigger slowdown
            loss *= 1.5f;
        bot.Speed *= 1 - loss;
        if (MathF.Abs(dOff) > 0.3f && MathF.Abs(ds) < bot.Car.Length)
            bot.Offset += MathF.Sign(dOff) * 0.35f;
        bot.CautiousUntil = _now + 3;
        bot.OvertakeTargetId = -1;
    }

    // ------------------------------------------------------------------ simulation

    private double _lastAdvance = double.NaN;

    /// <summary>Largest simulation step; longer frames are split into several steps.</summary>
    public float MaxStep { get; set; } = 0.025f;

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

    public void Step(float dt, double now)
    {
        _now = now;
        _stepDt = dt;
        BuildNeighbors();

        foreach (var bot in Bots)
        {
            switch (bot.Phase)
            {
                case BotPhase.Racing:
                case BotPhase.CoolDown:
                    if (bot.Speed < 1f)
                    {
                        if (double.IsNaN(bot.StoppedSince)) bot.StoppedSince = now;
                        else if (now - bot.StoppedSince > 12 && now > bot.IgnorePlayersUntil + 10)
                            bot.IgnorePlayersUntil = now + 6;
                    }
                    else if (bot.Speed > 5f)
                    {
                        bot.StoppedSince = double.NaN;
                    }
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

        ResolveOverlaps();

        foreach (var bot in Bots)
        {
            if (bot.Phase is BotPhase.Racing or BotPhase.CoolDown)
                UpdateTiming(bot);
        }
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
                Length = b.Car.Length, Width = b.Car.Width
            });
        }
        foreach (var e in Externals)
        {
            if (!e.Valid) continue;
            _neighbors.Add(new Neighbor
            {
                Id = e.Id, IsBot = false, External = e,
                S = e.S, Offset = e.Offset, Speed = MathF.Max(0, e.Speed),
                Length = e.Length, Width = e.Width
            });
        }
    }

    /// <summary>
    /// Highest speed the bot may have right now so it can still make every corner within braking distance,
    /// assuming it stays at <paramref name="offset"/>.
    /// </summary>
    public float LineSpeedLimit(RaceBot bot, float offset, float extraPaceLoss)
    {
        var car = bot.Car;
        float pace = (bot.Driver.Pace - extraPaceLoss + bot.PaceNoise) * Settings.GripFactor * bot.CarGrip;
        if (bot.Phase == BotPhase.CoolDown) pace *= Settings.CoolDownPace;
        if (_now < bot.MistakeUntil) pace -= 0.06f;

        float v = MathF.Max(bot.Speed, 10);
        float horizon = v * v / (2 * car.BrakeAt(0, pace)) + 40;
        float s0 = Line.WrapS((float)bot.Distance);
        float best = car.TopSpeed * (1 + 0.12f * bot.Draft) * (bot.Phase == BotPhase.CoolDown ? Settings.CoolDownPace : 1);
        float step = MathF.Max(Line.Spacing, 2f);

        for (float d = 0; d <= horizon; d += step)
        {
            int i = Line.IndexAt(s0 + d);
            float k = Line.Curvature[i];
            // radius changes when driving off the line: positive curvature turns towards +offset (inside)
            if (MathF.Abs(k) > 1e-5f)
            {
                float r = 1f / MathF.Abs(k) - offset * MathF.Sign(k);
                k = MathF.Sign(k) / MathF.Max(r, 4f);
            }

            float vLim = car.CornerLimit(k, Line.VerticalCurvature[i], pace);
            if (Settings.UseTrackHints)
                vLim = MathF.Min(vLim * MathF.Sqrt(Line.HintFactor[i]), Line.MaxSpeed[i]); // hint scales the usable grip
            if (Settings.SpeedHintScale > 0 && Line.SpeedHint[i] > 5)
                vLim = MathF.Min(vLim, Line.SpeedHint[i] * Settings.SpeedHintScale);

            if (vLim >= best) continue;
            // attacking drivers brake a little later
            float decel = car.BrakeAt((vLim + v) * 0.5f, pace) * (bot.OvertakeTargetId >= 0 ? 0.9f + 0.07f * bot.Driver.Aggression : 0.9f);
            float allowed = MathF.Sqrt(vLim * vLim + 2 * decel * d);
            if (allowed < best) best = allowed;
        }

        return best;
    }

    private void Think(RaceBot me)
    {
        float myS = Line.WrapS((float)me.Distance);
        float half = me.Car.Width / 2;
        var (roomMinus, roomPlus) = Line.MinRoom(myS, 25);
        float minOff = -roomMinus + half + Settings.EdgeMargin;
        float maxOff = roomPlus - half - Settings.EdgeMargin;
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
        float lookAhead = MathF.Max(50, me.Speed * 3);
        bool cautious = _now < me.CautiousUntil;
        float cornerSign = NextCornerSign(myS, 200);

        bool ignorePlayers = _now < me.IgnorePlayersUntil;
        foreach (var o in _neighbors)
        {
            if (o.IsBot && o.Id == me.Id) continue;
            if (!o.IsBot && ignorePlayers) continue;
            float ds = Line.Delta(myS, o.S);
            float longClear = (me.Car.Length + o.Length) / 2;
            float latClear = (me.Car.Width + o.Width) / 2 + Settings.SideMargin;
            float dOff = o.Offset - me.Offset;
            float closing = o.Speed - me.Speed; // > 0: car behind is faster

            bool isAlongside = MathF.Abs(ds) < longClear + 1.0f
                               || (ds < 0 && ds > -(longClear + MathF.Max(2f, closing * 1.2f)));
            if (isAlongside)
            {
                alongside = true;
                if (MathF.Abs(dOff) > 0.2f)
                {
                    if (dOff > 0) sideMax = MathF.Min(sideMax, o.Offset - latClear);
                    else sideMin = MathF.Max(sideMin, o.Offset + latClear);
                }
                // who backs out if the two of us get squeezed: the car clearly behind, or when level, the one on the outside of the next corner
                bool iYield;
                if (ds > me.Car.Length * 0.5f) iYield = true;
                else if (ds < -me.Car.Length * 0.5f) iYield = false;
                else if (cornerSign != 0 && MathF.Abs(dOff) > 0.2f) iYield = cornerSign * (me.Offset - o.Offset) < 0;
                else iYield = ds > 0;
                if (iYield)
                    squeezeAheadSpeed = MathF.Min(squeezeAheadSpeed, o.Speed);
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
        float vLine = LineSpeedLimit(me, me.TargetOffset, 0);
        float vTarget = vLine;

        // ---- braking-zone mistakes
        bool braking = vLine < me.Speed - 3;
        if (braking && !me.InBrakingZone && me.Phase == BotPhase.Racing)
        {
            float chance = (1 - me.Driver.Consistency) * 0.15f;
            if (_rng.NextSingle() < chance)
                me.MistakeUntil = _now + 1.5 + _rng.NextDouble() * 1.5;
        }
        me.InBrakingZone = braking;

        // ---- overtake bookkeeping
        if (me.OvertakeTargetId >= 0)
        {
            var target = FindNeighbor(me.OvertakeTargetId);
            if (target == null)
            {
                me.OvertakeTargetId = -1;
            }
            else
            {
                float ds = Line.Delta(myS, target.Value.S);
                me.OvertakeBestGap = MathF.Min(me.OvertakeBestGap, ds);
                float tLatClear = (me.Car.Width + target.Value.Width) / 2 + Settings.SideMargin;
                if (MathF.Abs(target.Value.Offset - me.Offset) > tLatClear - 0.4f)
                    me.OvertakeSeparatedAt = _now;
                // keep aiming for the chosen side of the target (the lane gets re-clamped to the road below)
                me.TargetOffset = target.Value.Offset + me.OvertakeSide * (tLatClear + 0.2f);
                if (ds < -((me.Car.Length + target.Value.Length) / 2 + 2))
                {
                    me.OvertakeTargetId = -1;
                    me.Overtakes++;
                    me.ReturnToLineAfter = _now + 0.8;
                }
                else if (_now - me.OvertakeSince > 30 || ds > 70
                         || (ds > me.OvertakeBestGap + 6 && _now - me.OvertakeSince > 2)
                         || _now - me.OvertakeSeparatedAt > 8)
                {
                    // lost ground or took too long: tuck in behind again and wait a moment before the next try
                    me.OvertakeTargetId = -1;
                    me.OvertakeGiveUps++;
                    me.ReturnToLineAfter = _now;
                    me.OvertakeCooldownUntil = _now + 3 + _rng.NextDouble() * 3;
                }
            }
        }

        // ---- car in front
        if (ahead is { } a)
        {
            float latClear = (me.Car.Width + a.Width) / 2 + Settings.SideMargin;
            bool gripLimited = vLine < me.Car.TopSpeed * 0.93f;
            if (gripLimited && aheadGap < 40)
            {
                // how much faster could I go where the car in front is limiting me (corners, braking zones)?
                float sample = Math.Clamp(vLine - a.Speed, -4, 4);
                me.PressureEma += (sample - me.PressureEma) * MathF.Min(1, _stepDt / 4f);
            }
            float attackRange = 3 + me.Speed * (0.12f + 0.18f * me.Driver.Aggression);
            float needAdvantage = 1.2f - 0.9f * me.Driver.Aggression;
            float closing = me.Speed - a.Speed;

            if (me.OvertakeTargetId != a.Id && !cautious && me.Phase == BotPhase.Racing && _now >= me.OvertakeCooldownUntil
                && aheadGap < attackRange && (me.PressureEma > needAdvantage || closing > 1.0f))
            {
                if (!TryChooseOvertakeSide(me, a, aheadGap, latClear, minOff, maxOff, out var side))
                {
                    me.OvertakeNoRoom++;
                }
                else
                {
                    me.OvertakeAttempts++;
                    me.TargetOffset = side;
                    me.OvertakeTargetId = a.Id;
                    me.OvertakeSince = _now;
                    me.OvertakeSeparatedAt = _now;
                    me.OvertakeSide = side > a.Offset ? 1 : -1;
                    me.OvertakeBestGap = aheadGap + (me.Car.Length + a.Length) / 2;
                }
            }

            bool blocked = MathF.Abs(a.Offset - me.Offset) < latClear - Settings.SideMargin * 0.5f;
            if (blocked)
            {
                float followGap = 1.5f + me.Speed * (cautious ? 0.45f : 0.10f + 0.20f * (1 - me.Driver.Aggression));
                if (me.OvertakeTargetId == a.Id && !cautious)
                    followGap = 1.0f + me.Speed * (0.05f + 0.04f * (1 - me.Driver.Aggression)); // right on the gearbox
                // in the slipstream on a straight: close right up for a run at the next braking zone
                if (me.Draft > 0.05f && !gripLimited && !cautious) followGap *= 0.45f;
                float vFollow = a.Speed + (aheadGap - followGap) * 0.8f;
                vTarget = MathF.Min(vTarget, MathF.Max(0, vFollow));
            }
        }

        // ---- defend against a faster car right behind
        if (behind is { } b && me.OvertakeTargetId < 0 && me.Phase == BotPhase.Racing
            && behindGap < 8 && b.Speed > me.Speed + 0.5f && _now > me.DefendUntil + 6
            && me.Driver.Aggression > 0.25f && _rng.NextSingle() < me.Driver.Aggression * 0.05f)
        {
            // one move towards the inside of the next corner
            float k = NextCornerSign(myS, 250);
            if (k != 0)
            {
                me.DefendOffset = Math.Clamp(k * 2.5f, minOff, maxOff);
                me.DefendUntil = _now + 4;
            }
        }

        // ---- choose lateral target
        if (me.OvertakeTargetId < 0)
        {
            if (_now < me.DefendUntil)
            {
                me.TargetOffset = me.DefendOffset;
            }
            else if (_now >= me.ReturnToLineAfter)
            {
                // go back to the racing line when that lane is free
                if (LaneFree(me, 0, myS, -(me.Car.Length + 2), 25))
                    me.TargetOffset = 0;
            }
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

        if (cautious && me.Speed < 3 && me.Phase == BotPhase.Racing && _now - me.OvertakeSince < 0.3)
            vTarget = MathF.Min(vTarget, 0);

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

    private bool TryChooseOvertakeSide(RaceBot me, Neighbor a, float gap, float latClear, float minOff, float maxOff, out float side)
    {
        float myS = Line.WrapS((float)me.Distance);
        float span = gap + (me.Car.Length + a.Length) / 2 + 40;
        var (roomMinus, roomPlus) = Line.MinRoom(myS, span);
        float half = me.Car.Width / 2;
        float lo = MathF.Max(minOff, -roomMinus + half + Settings.EdgeMargin);
        float hi = MathF.Min(maxOff, roomPlus - half - Settings.EdgeMargin);

        float plus = a.Offset + latClear + 0.2f;
        float minus = a.Offset - latClear - 0.2f;
        bool plusOk = plus <= hi && LaneFree(me, plus, myS, -(me.Car.Length + 3), span, a.Id);
        bool minusOk = minus >= lo && LaneFree(me, minus, myS, -(me.Car.Length + 3), span, a.Id);

        side = 0;
        if (!plusOk && !minusOk) return false;
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

    private bool LaneFree(RaceBot me, float offset, float myS, float from, float to, int ignoreId = int.MinValue)
    {
        foreach (var o in _neighbors)
        {
            if (o.IsBot && o.Id == me.Id) continue;
            if (o.Id == ignoreId) continue;
            float ds = Line.Delta(myS, o.S);
            if (ds < from - o.Length / 2 || ds > to + o.Length / 2) continue;
            float latClear = (me.Car.Width + o.Width) / 2 + Settings.SideMargin;
            if (MathF.Abs(o.Offset - offset) < latClear) return false;
        }
        return true;
    }

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
        float pace = me.Driver.Pace * Settings.GripFactor * me.CarGrip;
        float v = me.Speed;
        float target = me.TargetSpeed;

        if (_now < me.CautiousUntil && me.Speed < 1 && me.LapsCompleted == 0 && _now - me.LapStartTime < 1.5)
            target = 0; // reaction time at the start
        if (Settings.FuelRate > 0 && me.Fuel <= 0)
            target = MathF.Min(target, 25 / 3.6f); // out of fuel: rolling to the pits on the last drops

        float accel;
        if (target > v)
        {
            accel = me.Car.AccelAt(v, pace) / me.MassRatio + me.Draft * me.Car.DragCoefficient * v * v;
            v = MathF.Min(target, v + accel * dt);
        }
        else
        {
            accel = -me.Car.BrakeAt(v, pace);
            v = MathF.Max(target, v + accel * dt);
        }
        me.Accel = target > me.Speed ? accel : (target < me.Speed - 0.05f ? accel : 0);
        me.Speed = MathF.Max(0, v);

        // lateral movement: smooth, limited lateral speed and acceleration
        float err = me.TargetOffset - me.Offset;
        float maxLat = MathF.Min(3.5f, 0.5f + me.Speed * 0.06f);
        float desiredLat = Math.Clamp(err * 1.4f, -maxLat, maxLat);
        float latAcc = 5f;
        float lat = me.LateralSpeed + Math.Clamp(desiredLat - me.LateralSpeed, -latAcc * dt, latAcc * dt);
        me.LateralSpeed = lat;
        me.Offset += lat * dt;

        // keep on the road
        float s = Line.WrapS((float)me.Distance);
        int i = Line.IndexAt(s);
        float half = me.Car.Width / 2;
        me.Offset = Math.Clamp(me.Offset, -Line.RoomMinus[i] + half, Line.RoomPlus[i] - half);

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

                if (latPen < longPen && MathF.Abs(dOff) > 0.05f)
                {
                    // mostly side by side: slide apart
                    float push = o.IsBot ? latPen / 2 : latPen;
                    me.Offset = ClampToRoad(me, me.Offset - MathF.Sign(dOff) * push);
                    if (o.IsBot) o.Bot!.Offset = ClampToRoad(o.Bot, o.Bot.Offset + MathF.Sign(dOff) * push);
                }
                else if (ds > 0)
                {
                    // I'm behind: fall back
                    me.Distance -= longPen;
                    me.Speed = MathF.Min(me.Speed, o.Speed * 0.97f);
                }
                else if (!o.IsBot)
                {
                    // a player is behind me and overlapping: he rammed me, let him through a bit
                    me.Distance += longPen * 0.5f;
                }
            }
        }
    }

    private float ClampToRoad(RaceBot bot, float offset)
    {
        int i = Line.IndexAt(Line.WrapS((float)bot.Distance));
        float half = bot.Car.Width / 2;
        return Math.Clamp(offset, -Line.RoomMinus[i] + half, Line.RoomPlus[i] - half);
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
        var dir = bot.Speed > 0.5f ? Vector3.Normalize(vel) : fwd;

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

        var (gear, rpm) = GearAndRpm(bot);
        float full = bot.Car.AccelAt(bot.Speed, bot.Driver.Pace);
        byte throttle = bot.Accel > 0.05f && full > 0.1f ? (byte)Math.Clamp(bot.Accel / full * 255f, 0, 255) : (byte)0;
        if (bot.Phase == BotPhase.Grid) throttle = 40; // blipping on the grid
        bool hazards = bot.Phase == BotPhase.Racing && bot.Speed < 5 && !double.IsNaN(bot.StoppedSince) && _now - bot.StoppedSince > 3;
        return new BotPose(pos, rotation, vel, bot.Speed, wheel, gear, rpm,
            bot.Accel < -1f || (bot.Phase == BotPhase.Grid),
            Line.WrapS(s - Settings.StartLineS) / Line.Length,
            throttle, hazards);
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
