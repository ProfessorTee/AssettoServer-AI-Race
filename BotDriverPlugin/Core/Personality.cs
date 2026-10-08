namespace BotDriverPlugin.Core;

/// <summary>
/// Driving style of a bot. The strength (% of the best lap) stays the same, the personality decides how the driver gets there:
/// late on the brakes and down the inside, or smooth and easy on tyres and fuel.
/// </summary>
public sealed class Personality
{
    public string Name { get; set; } = "Balanced";
    /// <summary>Added to the driver's aggression (-1..1).</summary>
    public float Aggression { get; set; }
    /// <summary>
    /// -1..1: how the driver brakes. -1 = smooth and careful (brakes earlier, squeezes the pedal), 0 = normal,
    /// 1 = extremely late, stamps on the brakes, dives from far back when attacking, sometimes too late.
    /// </summary>
    public float BrakeBehavior { get; set; }
    /// <summary>0..1: attacks and defends on the inside; at 0.5 and above never tries the outside.</summary>
    public float InsideLine { get; set; }
    /// <summary>Tyre wear multiplier (1 = normal).</summary>
    public float TyreWear { get; set; } = 1f;
    /// <summary>Wants new tyres when worn tyres would have less grip than this (percent, e.g. 93). 0 = the global TyreChangeGrip.</summary>
    public float TyreChangeAt { get; set; }
    /// <summary>Fuel use multiplier (1 = normal).</summary>
    public float FuelUse { get; set; } = 1f;
    /// <summary>
    /// 0..1: 0 = normal, 1 = very smooth: presses the brake pedal and the throttle more gently, brakes earlier and softer
    /// with a bit of lift and coast. Saves tyres (up to 30 %) and fuel (up to 8 %), costs a little time.
    /// </summary>
    public float Smoothness { get; set; }
    /// <summary>0..1: how well the driver copes with somebody sitting in his slipstream for a long time (1 = ice cold).</summary>
    public float Composure { get; set; } = 0.5f;
    /// <summary>0..1: tendency to weave behind the car in front to unsettle it.</summary>
    public float Weaving { get; set; } = 0.4f;
    /// <summary>Multiplier on the chance of mistakes.</summary>
    public float Mistakes { get; set; } = 1f;
    /// <summary>Multiplier on the line errors (missed apex, running wide, early braking points).</summary>
    public float LineErrors { get; set; } = 1f;
    /// <summary>0..1: patience behind a slower car. 0 = flashes the lights after a few seconds, 1 = waits a long time and hardly ever flashes.</summary>
    public float Patience { get; set; } = 0.5f;

    // ---- line, fights and start (each bot of a personality varies a little around these)

    /// <summary>
    /// -1..1: the line through the corners. +1 = late apex / "V": brakes in a straight line, stays outside longer, turns in late and
    /// sharp, kerb on the apex, straight exit. -1 = round "U": turns in earlier and softer, earlier apex, wide flowing exit. 0 = AI line.
    /// </summary>
    public float ApexStyle { get; set; }
    /// <summary>-1..1: +1 uses every centimetre including the kerbs, -1 stays well clear of the edges.</summary>
    public float TrackUse { get; set; }
    /// <summary>0..1: early and hard on the throttle out of corners; now and then too early: runs wide, one or two wheels on the grass.</summary>
    public float ExitGreed { get; set; } = 0.35f;
    /// <summary>Extra room (m) left to a car alongside: positive = gives room and backs out early, negative = squeezes and holds on.</summary>
    public float Room { get; set; } = 0.15f;
    /// <summary>0..1: how readily an overtake is tried (from further back, with less speed advantage, in tighter gaps).</summary>
    public float Attack { get; set; } = 0.55f;
    /// <summary>0..1: how hard a position is defended (covering the inside); low values let a clearly faster car by.</summary>
    public float Defend { get; set; } = 0.5f;
    /// <summary>-1..1: the start. +1 = quick reaction and an aggressive launch (often wheelspin), -1 = slow and careful.</summary>
    public float Launch { get; set; }
    /// <summary>0..1: how far over the track edge an overtake may go: 1 = two wheels on the grass, also in the middle of a corner.</summary>
    public float GrassPass { get; set; }
    /// <summary>0..1: where overtakes are tried. 1 = anywhere, 0 = on the straights, in a corner only past a car that is off its line.</summary>
    public float CornerPass { get; set; } = 1f;
    /// <summary>-1..1: shift point. +1 = revs into the limiter before each upshift, 0 = the car's shift point, -1 = short shifting.</summary>
    public float ShiftRpm { get; set; }

    public static Personality Balanced => new() { ApexStyle = 0.15f, TrackUse = 0.2f };

    /// <summary>The built-in set (used when the configuration lists none).</summary>
    public static List<(Personality Personality, float Share)> Defaults() =>
    [
        (new Personality { Name = "Balanced", TyreChangeAt = 93, ApexStyle = 0.15f, TrackUse = 0.2f }, 40),
        (new Personality { Name = "DiveBomber", Aggression = 0.25f, BrakeBehavior = 1f, InsideLine = 1f, TyreWear = 1.25f, FuelUse = 1.05f,
            Composure = 0.4f, Weaving = 0.9f, Mistakes = 1.2f, LineErrors = 1.1f, Patience = 0.1f, TyreChangeAt = 95,
            ApexStyle = 0.9f, TrackUse = 0.9f, ExitGreed = 0.9f, Room = -0.2f, Attack = 1f, Defend = 0.9f, Launch = 0.8f, GrassPass = 1f, ShiftRpm = 1f }, 20),
        (new Personality { Name = "Chill", Aggression = -0.2f, BrakeBehavior = -0.6f, TyreWear = 0.75f, FuelUse = 0.97f, Smoothness = 0.5f,
            Composure = 0.9f, Weaving = 0.1f, Mistakes = 0.8f, LineErrors = 0.8f, Patience = 0.9f, TyreChangeAt = 90,
            ApexStyle = -0.8f, TrackUse = -0.6f, ExitGreed = 0.05f, Room = 0.75f, Attack = 0.3f, Defend = 0.15f, Launch = -0.5f, CornerPass = 0f, ShiftRpm = -0.3f }, 20),
        (new Personality { Name = "FuelSaver", Aggression = -0.1f, BrakeBehavior = -0.4f, TyreWear = 0.9f, FuelUse = 0.85f, Smoothness = 0.7f,
            Composure = 0.7f, Weaving = 0.2f, LineErrors = 0.9f, Patience = 0.7f, TyreChangeAt = 91,
            ApexStyle = -0.45f, TrackUse = -0.3f, ExitGreed = 0.1f, Room = 0.45f, Attack = 0.25f, Defend = 0.35f, Launch = -0.3f, CornerPass = 0.3f, ShiftRpm = -0.6f }, 20)
    ];

    /// <summary>The built-in personality of that name (for values missing in an older configuration), or null.</summary>
    public static Personality? BuiltIn(string name) => Defaults().Select(d => d.Personality).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
