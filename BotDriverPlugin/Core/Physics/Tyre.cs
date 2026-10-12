using TrackGeometry;

namespace BotDriverPlugin.Core.Physics;

/// <summary>
/// One axle's tyres (tyres.ini of the compound) with AC's tyre model version 10 ("SCTM", acs.exe SCTM::solve 0x14044bc20 and
/// SCTM::getPureFY 0x14044ba70, read in Tyre::initCompounds 0x140280800):
/// <list type="bullet">
/// <item>grip D = DY_REF·(Fz/FZ0)^(LS_EXPY−1) (or the linear DY0 + DY1·Fz/2000 of older tyres), the same for x with DX/LS_EXPX;</item>
/// <item>camber: D_y / (1 + c·DCAMBER_0 − c²·DCAMBER_1), c negative when the wheel leans into its lateral force; camber thrust as an extra
/// slip angle sin(camber)·CAMBER_GAIN;</item>
/// <item>sliding speed lowers both D by 1 / (1 + v_slide·SPEED_SENSITIVITY); braking multiplies D_x by 1 + BRAKE_DX_MOD;</item>
/// <item>combined slip s = |(tan α, κ)| / (1 + κ) (or the COMBINED_FACTOR norm); the peak slip grows linearly with load from
/// tan(FRICTION_LIMIT_ANGLE) at FZ0 to tan(FRICTION_LIMIT_ANGLE·(1 + FLEX_GAIN)) at 2·FZ0 and with the grip multiplier;
/// shape 1 − (1 − s/s_peak)³ up to the peak, then (1 − FALLOFF_LEVEL) / (1 + (s − s_peak)·FALLOFF_SPEED) + FALLOFF_LEVEL; the x direction
/// peaks CX_MULT times earlier.</item>
/// </list>
/// Hot tyres at their ideal pressure and temperature (PRESSURE_IDEAL, the top of the performance curve) are assumed: the pressure terms
/// then vanish.
/// </summary>
public sealed class TyreData
{
    public float Radius = 0.33f, Width = 0.25f, AngularInertia = 1.2f;
    /// <summary>Vertical stiffness (N/m) and damping (N·s/m) of the tyre.</summary>
    public float Rate = 250000, Damp = 500;
    public float DyRef = 1.2f, DxRef = 1.2f, Fz0 = 3000, LsExpY = 0.8f, LsExpX = 0.85f;
    /// <summary>Linear grip of tyres without DY_REF (DY0 + DY1·Fz·0.0005).</summary>
    public float Dy0, Dy1, Dx0, Dx1;
    public bool LinearD;
    /// <summary>tan of the peak slip at FZ0 and at 2·FZ0.</summary>
    public float PeakTan = MathF.Tan(7.5f * MathF.PI / 180), PeakTan2 = MathF.Tan(7.5f * MathF.PI / 180);
    public float FalloffLevel = 0.85f, FalloffSpeed = 3, SpeedSensitivity, CamberGain, DCamber0, DCamber1, CxMult = 1, BrakeDxMod = 1, CombinedFactor;
    public float RelaxationLength = 0.07f;
    /// <summary>Rolling resistance: (RR0 + RR1·v²)·Fz/1000, × (1 + 0.001·RR_SLIP·slip) while sliding.</summary>
    public float Rr0 = 10, Rr1 = 0.001f, RrSlip;
    /// <summary>RADIUS_ANGULAR_K: radius growth in mm per rad/s.</summary>
    public float RadiusAngularK;

    public float StaticDy(float fz) => LinearD ? Dy0 + Dy1 * fz * 0.0005f : DyRef * MathF.Pow(MathF.Max(fz, 1f) / Fz0, LsExpY - 1);
    public float StaticDx(float fz) => LinearD ? Dx0 + Dx1 * fz * 0.0005f : DxRef * MathF.Pow(MathF.Max(fz, 1f) / Fz0, LsExpX - 1);

    /// <summary>Peak slip angle at FZ0 (rad), for planning and steering limits.</summary>
    public float PeakAngle => MathF.Atan(PeakTan);

    /// <summary>Peak combined slip at a load (tan units) with grip multiplier <paramref name="u"/>.</summary>
    public float PeakSlip(float fz, float u) => ((fz - Fz0) / Fz0 * (PeakTan2 - PeakTan) + PeakTan) * ((u - 1) * 0.75f + 1);

    /// <summary>
    /// Tyre forces in the wheel's frame (fx along it, fy towards +y; a positive slip angle is the contact patch moving towards +y) and the
    /// combined slip relative to the peak (1 = at the peak).
    /// </summary>
    /// <param name="lean">Lean of the wheel towards +y (rad, against the road).</param>
    /// <param name="u">Grip multiplier (track, temperature, wear).</param>
    /// <param name="noFalloff">No grip loss past the peak (AC: the AI's ultra grip above 1).</param>
    public (float Fx, float Fy, float Slip) Solve(float fz, float alpha, float kappa, float lean, float speed, float u, bool noFalloff)
    {
        if (fz <= 0 || (alpha == 0 && kappa == 0 && lean == 0)) return (0, 0, 0);
        float camber = -lean; // AC's sign: positive leans towards −y
        float aEff = alpha + MathF.Sin(camber) * CamberGain;
        float tanA = MathF.Tan(aEff);
        float dy = StaticDy(fz) * u, dx = StaticDx(fz) * u;
        if (kappa < 0) dx *= BrakeDxMod;
        // leaning into the force (camber and slip the same sign in AC's terms) helps
        float c = MathF.Abs(camber);
        if ((camber < 0 || aEff < 0) && (camber > 0 || aEff > 0)) c = -c;
        c = -c;
        float fac = c * DCamber0 - c * c * DCamber1;
        if (fac <= -1) fac = -0.9f;
        dy /= fac + 1;
        float slide = MathF.Sqrt(Sq(speed * MathF.Sin(alpha)) + Sq(MathF.Cos(alpha) * speed * kappa));
        float ss = slide * SpeedSensitivity + 1;
        dy /= ss;
        dx /= ss;
        float sp = PeakSlip(fz, u);
        float k1 = MathF.Max(kappa, -0.99999f) + 1;
        float sx = kappa / k1, sy = tanA / k1;
        float s = CombinedFactor > 0 && CombinedFactor != 2
            ? MathF.Pow(MathF.Pow(MathF.Abs(sy), CombinedFactor) + MathF.Pow(MathF.Abs(sx), CombinedFactor), 1 / CombinedFactor)
            : MathF.Sqrt(sx * sx + sy * sy);
        if (s < 1e-9f) return (0, 0, 0);
        float fl = noFalloff ? 1 : FalloffLevel;
        float shY = Shape(s, sp, fl), shX = Shape(s, sp / MathF.Max(0.05f, CxMult), fl);
        return (shX * dx * (sx / s) * fz, -shY * dy * (sy / s) * fz, s / sp);
    }

    private float Shape(float s, float sp, float falloffLevel)
    {
        if (s <= sp)
        {
            float x = 1 - s / sp;
            return 1 - x * x * x;
        }
        return (1 - falloffLevel) / ((s - sp) * FalloffSpeed + 1) + falloffLevel;
    }

    private static float Sq(float x) => x * x;

    public static TyreData From(IniFile tyres, string section, float fallbackGrip)
    {
        float G(string k, float d) => tyres.GetFloat(section, k, d);
        var t = new TyreData
        {
            Radius = Pos(G("RADIUS", 0), 0.33f),
            Width = Pos(G("WIDTH", 0), 0.25f),
            AngularInertia = Pos(G("ANGULAR_INERTIA", 0), 1.2f),
            Rate = Pos(G("RATE", 0), 250000),
            Damp = Pos(G("DAMP", 0), 500),
            Fz0 = Pos(G("FZ0", 0), 3000),
            LsExpY = Pos(G("LS_EXPY", 0), 0.8f),
            LsExpX = Pos(G("LS_EXPX", 0), 0.85f),
            FalloffLevel = Math.Clamp(Pos(G("FALLOFF_LEVEL", 0), 0.92f), 0.3f, 1f),
            FalloffSpeed = Pos(G("FALLOFF_SPEED", 0), 1),
            SpeedSensitivity = G("SPEED_SENSITIVITY", 0),
            CamberGain = G("CAMBER_GAIN", 0),
            DCamber0 = G("DCAMBER_0", 0),
            DCamber1 = G("DCAMBER_1", 0),
            CxMult = Pos(G("CX_MULT", 0), 1),
            BrakeDxMod = G("BRAKE_DX_MOD", 0) is var b && b != 0 ? b + 1 : 1,
            CombinedFactor = G("COMBINED_FACTOR", 0),
            RelaxationLength = Pos(G("RELAXATION_LENGTH", 0), 0.07f),
            Rr0 = G("ROLLING_RESISTANCE_0", 10),
            Rr1 = G("ROLLING_RESISTANCE_1", 0.001f),
            RrSlip = G("ROLLING_RESISTANCE_SLIP", 0),
            RadiusAngularK = G("RADIUS_ANGULAR_K", 0),
        };
        float dyRef = G("DY_REF", 0), dxRef = G("DX_REF", 0);
        if (dyRef > 0 && G("LS_EXPY", 0) > 0)
        {
            t.DyRef = dyRef;
            t.DxRef = dxRef > 0 ? dxRef : dyRef;
        }
        else if (G("DY0", 0) > 0)
        {
            t.LinearD = true;
            t.Dy0 = G("DY0", 0);
            t.Dy1 = G("DY1", 0);
            t.Dx0 = Pos(G("DX0", 0), t.Dy0);
            t.Dx1 = G("DX1", t.Dy1);
        }
        else t.DyRef = t.DxRef = fallbackGrip;
        // FRICTION_LIMIT_ANGLE (AC's default 7.5°) and FLEX_GAIN: the peak slip at FZ0 and 2·FZ0
        float fla = Pos(G("FRICTION_LIMIT_ANGLE", 0), 7.5f) * MathF.PI / 180;
        t.PeakTan = MathF.Tan(fla);
        t.PeakTan2 = MathF.Tan(fla * (1 + G("FLEX_GAIN", 0)));
        return t;
    }

    private static float Pos(float v, float fallback) => v > 0 ? v : fallback;
}
