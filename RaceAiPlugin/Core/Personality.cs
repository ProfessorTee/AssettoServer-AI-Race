namespace RaceAiPlugin.Core;

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

    public static Personality Balanced => new();

    /// <summary>The built-in set (used when the configuration lists none).</summary>
    public static List<(Personality Personality, float Share)> Defaults() =>
    [
        (new Personality { Name = "Balanced", TyreChangeAt = 93 }, 40),
        (new Personality { Name = "DiveBomber", Aggression = 0.25f, BrakeBehavior = 1f, InsideLine = 1f, TyreWear = 1.25f, FuelUse = 1.05f,
            Composure = 0.4f, Weaving = 0.9f, Mistakes = 1.2f, LineErrors = 1.1f, Patience = 0.1f, TyreChangeAt = 95 }, 20),
        (new Personality { Name = "Chill", Aggression = -0.2f, BrakeBehavior = -0.6f, TyreWear = 0.75f, FuelUse = 0.97f, Smoothness = 0.5f,
            Composure = 0.9f, Weaving = 0.1f, Mistakes = 0.8f, LineErrors = 0.8f, Patience = 0.9f, TyreChangeAt = 90 }, 20),
        (new Personality { Name = "FuelSaver", Aggression = -0.1f, BrakeBehavior = -0.4f, TyreWear = 0.9f, FuelUse = 0.85f, Smoothness = 0.7f,
            Composure = 0.7f, Weaving = 0.2f, LineErrors = 0.9f, Patience = 0.7f, TyreChangeAt = 91 }, 20)
    ];
}
