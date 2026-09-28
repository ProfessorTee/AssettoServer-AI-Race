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
    /// <summary>0..1: brakes later (more in attacks), dives from further back, a bit more prone to braking too late.</summary>
    public float LateBraking { get; set; }
    /// <summary>0..1: attacks and defends on the inside; at 0.5 and above never tries the outside.</summary>
    public float InsideLine { get; set; }
    /// <summary>Tyre wear multiplier (1 = normal).</summary>
    public float TyreWear { get; set; } = 1f;
    /// <summary>Fuel use multiplier (1 = normal).</summary>
    public float FuelUse { get; set; } = 1f;
    /// <summary>0..1: gentle inputs, earlier and softer braking with a bit of lift and coast: saves tyres and fuel, costs a little time.</summary>
    public float Smoothness { get; set; }
    /// <summary>0..1: how well the driver copes with somebody sitting in his slipstream for a long time (1 = ice cold).</summary>
    public float Composure { get; set; } = 0.5f;
    /// <summary>0..1: tendency to weave behind the car in front to unsettle it.</summary>
    public float Weaving { get; set; } = 0.4f;
    /// <summary>Multiplier on the chance of mistakes.</summary>
    public float Mistakes { get; set; } = 1f;

    public static Personality Balanced => new();

    /// <summary>The built-in set (used when the configuration lists none).</summary>
    public static List<(Personality Personality, float Share)> Defaults() =>
    [
        (new Personality { Name = "Balanced" }, 40),
        (new Personality { Name = "DiveBomber", Aggression = 0.25f, LateBraking = 1f, InsideLine = 1f, TyreWear = 1.25f, FuelUse = 1.05f,
            Composure = 0.4f, Weaving = 0.9f, Mistakes = 1.2f }, 20),
        (new Personality { Name = "Chill", Aggression = -0.2f, TyreWear = 0.75f, FuelUse = 0.97f, Smoothness = 0.5f, Composure = 0.9f,
            Weaving = 0.1f, Mistakes = 0.8f }, 20),
        (new Personality { Name = "FuelSaver", Aggression = -0.1f, TyreWear = 0.9f, FuelUse = 0.85f, Smoothness = 0.7f, Composure = 0.7f,
            Weaving = 0.2f }, 20)
    ];
}
