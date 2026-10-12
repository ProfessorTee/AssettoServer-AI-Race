namespace BotDriverPlugin.Core.Physics;

/// <summary>
/// One tyre's temperatures and pressure, AC's TyreThermalModel (acs.exe: step 0x1402ae340, addThermalInput 0x1402ad090,
/// Tyre::stepThermalModel 0x140284ca0, pressure in Tyre::step 0x140283800, grip in 0x140280190):
/// <list type="bullet">
/// <item>the tread is 12 slices round the tyre × 3 across (inner, middle, outer), the slice on the road gets the heat: road temperature plus
/// friction (slip speed × μ × load × FRICTION_K) and rolling (SURFACE_ROLLING_K × ω × load / 1000, tyres.ini version above 5), spread across
/// by camber (CAMBER_TEMP_SPREAD_K) and pressure (over-inflated: hotter middle); a patch heats towards that at SURFACE_TRANSFER, the others
/// cool towards the air at SURFACE_TRANSFER × (1 + v² × (COOL_FACTOR − 1) × 0.000324);</item>
/// <item>neighbouring patches share heat (PATCH_TRANSFER), every patch with the core (CORE_TRANSFER); the core goes to the rolling heat
/// (ROLLING_K × ω × load / 1000) at INTERNAL_CORE_TRANSFER;</item>
/// <item>pressure = cold pressure + (core − 26 °C) × PRESSURE_TEMPERATURE_GAIN (0.16 psi/°C);</item>
/// <item>grip × PERFORMANCE_CURVE at core + (contact patch − core)/4, ÷ (1 + |pressure − PRESSURE_IDEAL| × PRESSURE_D_GAIN).</item>
/// </list>
/// </summary>
public sealed class TyreHeat
{
    public const int Slices = 12;
    private readonly float[] _t = new float[Slices * 3], _in = new float[Slices * 3];
    private double _angle;
    private float _coreIn;
    public float Core { get; private set; }
    /// <summary>Cold (set) pressure and the hot pressure now (psi).</summary>
    public float StaticPressure, Pressure;
    /// <summary>Grip factor from temperature and pressure (1 = in the window at the ideal pressure).</summary>
    public float Grip { get; private set; } = 1;
    /// <summary>Rolling resistance factor from the pressure ((ideal / pressure − 1) × PRESSURE_RR_GAIN + 1).</summary>
    public float RollFactor { get; private set; } = 1;
    /// <summary>Hot pressure × seconds and seconds since the last reset (the average a bot's engineer reads).</summary>
    public double PressureSum, PressureTime;

    public TyreHeat(float staticPressure) => StaticPressure = Pressure = staticPressure;

    private int Slot => (int)((long)Math.Floor(_angle / (2 * Math.PI) * Slices) % Slices + Slices) % Slices;

    /// <summary>Contact patch average (inner, middle, outer) and the temperature the grip goes by.</summary>
    public float Patch => (_t[Slot] + _t[Slices + Slot] + _t[2 * Slices + Slot]) / 3;
    public float Practical => Core + (Patch - Core) * 0.25f;
    /// <summary>Inner / middle / outer tread temperature of the contact patch.</summary>
    public (float I, float M, float O) Tread => (_t[Slot], _t[Slices + Slot], _t[2 * Slices + Slot]);

    public void Set(float temperature, TyreData t)
    {
        Core = temperature;
        Array.Fill(_t, temperature);
        Array.Clear(_in);
        _coreIn = 0;
        Update(t);
    }

    /// <param name="slipSpeed">Speed of the contact patch over the road (m/s).</param>
    /// <param name="camber">AC's sign: positive leans towards −y.</param>
    public void Step(TyreData t, float dt, float omega, float load, float slipSpeed, float camber, float speed, float ambient, float road)
    {
        if (!t.HasThermal) return;
        float pd = Pressure / t.PressureIdeal - 1;
        float rr = Pressure > 0 ? (t.PressureIdeal / Pressure - 1) * t.PressureRrGain + 1 : 1;
        float w = MathF.Abs(omega), fz = MathF.Max(0, load);
        float power = slipSpeed * t.StaticDy(fz) * fz * t.FrictionK;
        float rollK = t.RollingK * rr;
        if (t.Version < 5) power += rollK * w * fz * 0.001f;
        if (t.Version > 5) power += rr * t.SurfaceRollingK * w * fz * 0.001f;
        if (t.Version > 4) _coreIn += rollK * w * fz * 0.001f;

        // the heat into the patch on the road, spread across by camber and pressure
        int slot = Slot;
        power += road;
        float c = Math.Clamp(camber * t.CamberSpreadK, -1, 1);
        float f6 = 1 - 0.5f * pd, f7 = 0.05f * pd;
        _in[slot] += (c + 1 - f7) * f6 * power;
        _in[Slices + slot] += (1 + 0.1f * pd) * f6 * power;
        _in[2 * Slices + slot] += (1 - c - f7) * f6 * power;

        _angle = (_angle + dt * omega) % 100000;
        Core += (MathF.Max(ambient, _coreIn) - Core) * dt * t.InternalCoreTransfer;
        _coreIn = 0;
        float cool = (speed * speed * t.CoolFactor + 1) * t.SurfaceTransfer * dt;
        float pt = dt * t.PatchTransfer, ct = dt * t.CoreTransfer;
        for (int r = 0; r < 3; r++)
        for (int k = 0; k < Slices; k++)
        {
            int i = r * Slices + k;
            float T = _t[i];
            T += _in[i] <= ambient ? (ambient - T) * cool : (_in[i] - T) * dt * t.SurfaceTransfer;
            _in[i] = 0;
            if (r > 0) T += (_t[i - Slices] - T) * pt;
            if (r < 2) T += (_t[i + Slices] - T) * pt;
            T += (_t[r * Slices + (k + Slices - 1) % Slices] - T) * pt;
            T += (_t[r * Slices + (k + 1) % Slices] - T) * pt;
            T += (Core - T) * ct;
            Core += (T - Core) * ct;
            _t[i] = T;
        }
        Update(t);
        RollFactor = rr;
        PressureSum += Pressure * dt;
        PressureTime += dt;
    }

    private void Update(TyreData t)
    {
        if (!t.HasThermal) return;
        Pressure = StaticPressure + (Core - 26) * t.PressureTempGain;
        float perf = t.Performance is { } lut ? Math.Clamp(lut.At(Practical), 0.5f, 1f) : 1;
        Grip = perf / (MathF.Abs(Pressure - t.PressureIdeal) * t.PressureDGain + 1);
    }
}
