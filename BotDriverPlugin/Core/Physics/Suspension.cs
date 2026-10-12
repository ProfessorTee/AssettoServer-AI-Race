using System.Globalization;
using System.Numerics;
using TrackGeometry;

namespace BotDriverPlugin.Core.Physics;

/// <summary>
/// One axle's suspension from suspensions.ini, like AC builds it (Suspension::loadINI 0x1402c1d90, Suspension::step 0x1402c3390,
/// Damper::getForce 0x1402b3280): springs (wheel rate, progressive, preloaded by ROD_LENGTH), bump stops, packers, dampers with slow and fast
/// bump/rebound, hub mass; and the linkage itself (DWB: two wishbones and a steering rod, STRUT: McPherson strut, lower arm and rod) solved in
/// 3D for every wheel travel and steering rack position. From the solved linkage come camber and toe (bump steer, Ackermann), and how the
/// contact patch and the hub move with the travel, which is how the tyre forces push on the body (roll centre, anti-dive, anti-squat).
/// Geometry of the left wheel: x inwards, y up, z forwards, relative to the wheel centre at design height; the right one is the mirror image.
/// </summary>
public sealed class SuspensionAxle
{
    public float Track = 1.6f, BaseY = -0.15f, RodLength, HubMass = 40;
    public float SpringRate = 80000, ProgressiveRate, BumpStopRate = 100000, BumpStopUp = 0.1f, BumpStopDn = 0.1f, PackerRange;
    public float DampBump = 4000, DampFastBump = 4000, DampFastBumpThreshold = 1, DampRebound = 4000, DampFastRebound = 4000, DampFastReboundThreshold = 1;
    public float StaticCamber; // rad, negative = top inwards
    public float Arb;
    /// <summary>True when the linkage was solved (DWB, STRUT); otherwise a rigid axle or unknown type: camber stays, roll centre at the hub.</summary>
    public bool HasGeometry;
    public bool Steered;
    public string Type = "";
    /// <summary>Why the linkage couldn't be solved (null when it was, or the type has none).</summary>
    public string? GeometryError;

    // tables over travel (TravelMin + i·TravelStep) and rack (−RackMax + j·RackStep)
    public const float TravelMin = -0.16f, TravelStep = 0.01f;
    public const int TravelCount = 33;
    public float RackMax, RackStep;
    public int RackCount = 1;
    /// <summary>Per (travel, rack): top of the wheel towards the car (rad, + = negative camber), front of the wheel towards the car (rad, + = toe in).</summary>
    public float[] TopIn = [], ToeIn = [];
    /// <summary>Per travel (rack in the middle): contact patch inward / forward movement per metre of travel, hub forward movement per metre, upright pitch per metre.</summary>
    public float[] DyCp = [], DxCp = [], DxHub = [], DPitch = [];

    /// <summary>Spring force (N, pushing the wheel down and the body up) at travel z (m, + = compression from the design position).</summary>
    public float SpringForce(float z)
    {
        float x = z + RodLength;
        float f = x > 0 ? (x * ProgressiveRate + SpringRate) * x : 0;
        if (BumpStopUp > 0 && z > BumpStopUp) f += BumpStopRate * (z - BumpStopUp);
        if (PackerRange > 0 && x > PackerRange) f += BumpStopRate * (x - PackerRange);
        if (BumpStopDn > 0 && z < -BumpStopDn) f += BumpStopRate * (z + BumpStopDn);
        return f;
    }

    /// <summary>Damper force (N, + pushes the body up) at travel speed v (m/s, + = compressing).</summary>
    public float DamperForce(float v)
    {
        if (v > 0)
            return v > DampFastBumpThreshold ? DampFastBumpThreshold * DampBump + (v - DampFastBumpThreshold) * DampFastBump : v * DampBump;
        float a = -v;
        return -(a > DampFastReboundThreshold ? DampFastReboundThreshold * DampRebound + (a - DampFastReboundThreshold) * DampFastRebound : a * DampRebound);
    }

    public (float TopIn, float ToeIn) Angles(float travel, float rack)
    {
        if (!HasGeometry) return (-StaticCamber, 0);
        float ti = Math.Clamp((travel - TravelMin) / TravelStep, 0, TravelCount - 1.001f);
        int i = (int)ti;
        float fi = ti - i;
        float tj = RackCount > 1 ? Math.Clamp((rack + RackMax) / RackStep, 0, RackCount - 1.001f) : 0;
        int j = (int)tj;
        float fj = tj - j;
        int j1 = Math.Min(j + 1, RackCount - 1);
        float Bi(float[] a) => (a[i * RackCount + j] * (1 - fj) + a[i * RackCount + j1] * fj) * (1 - fi)
                               + (a[(i + 1) * RackCount + j] * (1 - fj) + a[(i + 1) * RackCount + j1] * fj) * fi;
        return (Bi(TopIn), Bi(ToeIn));
    }

    public (float DyCp, float DxCp, float DxHub, float DPitch) Jacking(float travel)
    {
        if (DyCp.Length == 0) return (0, 0, 0, 0);
        float ti = Math.Clamp((travel - TravelMin) / TravelStep, 0, TravelCount - 1.001f);
        int i = (int)ti;
        float f = ti - i;
        float L(float[] a) => a[i] * (1 - f) + a[i + 1] * f;
        return (L(DyCp), L(DxCp), L(DxHub), L(DPitch));
    }

    public static SuspensionAxle Read(IniFile s, string sec, float arb, float tyreRadius, bool steered, float rackPerWheelRad)
    {
        float G(string k, float d) => s.GetFloat(sec, k, d);
        var a = new SuspensionAxle
        {
            Type = (s.Get(sec, "TYPE") ?? "").Trim().ToUpperInvariant(),
            Track = G("TRACK", 1.6f),
            BaseY = G("BASEY", -0.15f),
            RodLength = G("ROD_LENGTH", 0),
            HubMass = Math.Clamp(G("HUB_MASS", 40), 5, 150),
            SpringRate = G("SPRING_RATE", 80000),
            ProgressiveRate = G("PROGRESSIVE_SPRING_RATE", 0),
            BumpStopRate = G("BUMP_STOP_RATE", 100000),
            BumpStopUp = G("BUMPSTOP_UP", 0),
            BumpStopDn = G("BUMPSTOP_DN", 0),
            PackerRange = G("PACKER_RANGE", 0),
            DampBump = G("DAMP_BUMP", 4000),
            DampRebound = G("DAMP_REBOUND", 4000),
            StaticCamber = G("STATIC_CAMBER", 0) * MathF.PI / 180,
            Arb = arb,
            Steered = steered,
        };
        a.DampFastBump = G("DAMP_FAST_BUMP", a.DampBump);
        a.DampFastRebound = G("DAMP_FAST_REBOUND", a.DampRebound);
        a.DampFastBumpThreshold = Pos(G("DAMP_FAST_BUMPTHRESHOLD", 0), 1000);
        a.DampFastReboundThreshold = Pos(G("DAMP_FAST_REBOUNDTHRESHOLD", 0), 1000);
        try
        {
            a.Solve(s, sec, tyreRadius, G("TOE_OUT", 0), rackPerWheelRad);
        }
        catch (Exception ex)
        {
            a.HasGeometry = false;
            a.GeometryError = ex.Message;
        }
        if (!a.HasGeometry)
        {
            // rigid axle: the roll centre at hub height, the wheels stand on the road
            a.DyCp = Enumerable.Repeat(-tyreRadius / (a.Track / 2), TravelCount).ToArray();
            a.DxCp = new float[TravelCount];
            a.DxHub = new float[TravelCount];
            a.DPitch = new float[TravelCount];
        }
        return a;
    }

    private static float Pos(float v, float d) => v > 0 ? v : d;

    private static Vector3 P(IniFile s, string sec, string key)
    {
        var parts = (s.Get(sec, key) ?? throw new KeyNotFoundException(key)).Split(';')[0].Split(',');
        if (parts.Length < 3) throw new FormatException(key);
        float F(int i) => float.Parse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return new Vector3(F(0), F(1), F(2));
    }

    /// <summary>The upright as a rigid body: rotation vector and translation, solved against the links by Newton's method.</summary>
    private void Solve(IniFile s, string sec, float radius, float toeOut, float rackPerWheelRad)
    {
        bool strut = Type == "STRUT";
        if (Type != "DWB" && !strut) return;
        Vector3 cTop1 = default, cTop2 = default, tTop = default, sCar = default, sTyre = default, axis0 = default;
        if (strut)
        {
            sCar = P(s, sec, "STRUT_CAR");
            sTyre = P(s, sec, "STRUT_TYRE");
            axis0 = Vector3.Normalize(sCar - sTyre);
        }
        else
        {
            cTop1 = P(s, sec, "WBCAR_TOP_FRONT");
            cTop2 = P(s, sec, "WBCAR_TOP_REAR");
            tTop = P(s, sec, "WBTYRE_TOP");
        }
        var cBot1 = P(s, sec, "WBCAR_BOTTOM_FRONT");
        var cBot2 = P(s, sec, "WBCAR_BOTTOM_REAR");
        var tBot = P(s, sec, "WBTYRE_BOTTOM");
        var cSteer = P(s, sec, "WBCAR_STEER");
        var tSteer = P(s, sec, "WBTYRE_STEER");
        float lT1 = (tTop - cTop1).Length(), lT2 = (tTop - cTop2).Length();
        float lB1 = (tBot - cBot1).Length(), lB2 = (tBot - cBot2).Length();
        float lS0 = (tSteer - cSteer).Length();
        var cp0 = new Vector3(0, -radius, 0);

        // static toe: the steering rod is TOE_OUT longer; whichever sign turns the front of the wheel outwards
        float lS = lS0;
        if (toeOut != 0)
        {
            float best = float.NaN;
            foreach (float sign in new[] { 1f, -1f })
            {
                var q = new double[6];
                if (!Newton(q, 0, 0, lS0 + sign * MathF.Abs(toeOut))) continue;
                float toeIn = ToeOf(q);
                if (float.IsNaN(best) || (toeOut > 0 ? toeIn < best : toeIn > best)) { best = toeIn; lS = lS0 + sign * MathF.Abs(toeOut); }
            }
        }

        // rack travel for the full table: enough for 40° of wheel angle
        if (Steered && rackPerWheelRad > 0)
        {
            RackMax = Math.Clamp(rackPerWheelRad * 40 * MathF.PI / 180, 0.02f, 0.25f);
            RackCount = 33;
            RackStep = 2 * RackMax / (RackCount - 1);
        }
        TopIn = new float[TravelCount * RackCount];
        ToeIn = new float[TravelCount * RackCount];
        var cpY = new float[TravelCount];
        var cpX = new float[TravelCount];
        var hubX = new float[TravelCount];
        var pitch = new float[TravelCount];
        var start = new double[6];
        if (!Newton(start, 0, 0, lS)) { GeometryError = "design position"; return; }
        int mid = RackCount / 2;
        // the rack positions at design height, walked out from the middle (each solve starts from its neighbour)
        // past the rack travel the linkage can reach the steering stops there (the wheel angle holds)
        var atDesign = new double[RackCount][];
        var rackAt = new float[RackCount];
        atDesign[mid] = start;
        for (int dirJ = -1; dirJ <= 1; dirJ += 2)
            for (int j = mid + dirJ; j >= 0 && j < RackCount; j += dirJ)
            {
                var qj = (double[])atDesign[j - dirJ].Clone();
                float rj = -RackMax + j * RackStep;
                if (Newton(qj, 0, rj, lS)) { atDesign[j] = qj; rackAt[j] = rj; }
                else { atDesign[j] = atDesign[j - dirJ]; rackAt[j] = rackAt[j - dirJ]; }
            }
        for (int j = 0; j < RackCount; j++)
        {
            float rack = RackCount > 1 ? rackAt[j] : 0;
            // walk out from the design height both ways, each solve starting from its neighbour
            var q0 = atDesign[j];
            double[] q;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                q = (double[])q0.Clone();
                int i0 = (int)MathF.Round(-TravelMin / TravelStep);
                for (int i = i0; i >= 0 && i < TravelCount; i += dir)
                {
                    float z = TravelMin + i * TravelStep;
                    // past what the linkage can reach (rare, at full lock and full travel): keep the last pose that worked
                    var trial = (double[])q.Clone();
                    if (Newton(trial, z, rack, lS)) q = trial;
                    else if (i == i0) { GeometryError = $"travel 0 at rack {rack:F3}"; return; }
                    var (r, t) = Pose(q);
                    var up = Rot(r, Vector3.UnitY);
                    var fwd = Rot(r, Vector3.UnitZ);
                    TopIn[i * RackCount + j] = MathF.Atan2(up.X, up.Y) - StaticCamber;
                    ToeIn[i * RackCount + j] = MathF.Atan2(fwd.X, fwd.Z);
                    if (j == mid)
                    {
                        var cp = Rot(r, cp0) + t;
                        cpY[i] = cp.X;
                        cpX[i] = cp.Z;
                        hubX[i] = t.Z;
                        pitch[i] = MathF.Atan2(-fwd.Y, fwd.Z);
                    }
                }
            }
        }
        DyCp = Deriv(cpY);
        DxCp = Deriv(cpX);
        DxHub = Deriv(hubX);
        DPitch = Deriv(pitch);
        HasGeometry = true;

        float ToeOf(double[] q) => MathF.Atan2(Rot(Pose(q).R, Vector3.UnitZ).X, Rot(Pose(q).R, Vector3.UnitZ).Z);

        // residuals: link lengths, strut line, travel of the hub
        bool Newton(double[] q, float travel, float rack, float rodLen)
        {
            var res = new double[6];
            var jac = new double[36];
            for (int it = 0; it < 30; it++)
            {
                Residual(q, travel, rack, rodLen, res);
                double norm = res.Sum(x => x * x);
                if (norm < 1e-16) return true;
                for (int k = 0; k < 6; k++)
                {
                    var qq = (double[])q.Clone();
                    qq[k] += 1e-6;
                    var r2 = new double[6];
                    Residual(qq, travel, rack, rodLen, r2);
                    for (int m = 0; m < 6; m++) jac[m * 6 + k] = (r2[m] - res[m]) / 1e-6;
                }
                var dq = SolveLinear(jac, res.Select(x => -x).ToArray());
                if (dq == null) return false;
                for (int k = 0; k < 6; k++) q[k] += dq[k];
            }
            Residual(q, travel, rack, rodLen, res);
            return res.Sum(x => x * x) < 1e-10;
        }

        void Residual(double[] q, float travel, float rack, float rodLen, double[] res)
        {
            var (r, t) = Pose(q);
            Vector3 W(Vector3 p) => Rot(r, p) + t;
            var steerCar = cSteer + new Vector3(rack, 0, 0);
            if (strut)
            {
                var sT = W(sTyre);
                var ax = Rot(r, axis0);
                var e1 = Vector3.Normalize(Vector3.Cross(ax, MathF.Abs(ax.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
                var e2 = Vector3.Cross(ax, e1);
                var d = sCar - sT;
                res[0] = Vector3.Dot(d, e1);
                res[1] = Vector3.Dot(d, e2);
            }
            else
            {
                var tt = W(tTop);
                res[0] = (tt - cTop1).Length() - lT1;
                res[1] = (tt - cTop2).Length() - lT2;
            }
            var tb = W(tBot);
            res[2] = (tb - cBot1).Length() - lB1;
            res[3] = (tb - cBot2).Length() - lB2;
            res[4] = (W(tSteer) - steerCar).Length() - rodLen;
            res[5] = t.Y - travel;
        }
    }

    private static float[] Deriv(float[] a)
    {
        var d = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(a.Length - 1, i + 1);
            d[i] = (a[i1] - a[i0]) / ((i1 - i0) * TravelStep);
        }
        return d;
    }

    private static (Vector3 R, Vector3 T) Pose(double[] q) => (new Vector3((float)q[0], (float)q[1], (float)q[2]), new Vector3((float)q[3], (float)q[4], (float)q[5]));

    /// <summary>Rotation of <paramref name="p"/> by the rotation vector <paramref name="r"/> (Rodrigues).</summary>
    private static Vector3 Rot(Vector3 r, Vector3 p)
    {
        float th = r.Length();
        if (th < 1e-9f) return p + Vector3.Cross(r, p);
        var k = r / th;
        float c = MathF.Cos(th), s = MathF.Sin(th);
        return p * c + Vector3.Cross(k, p) * s + k * Vector3.Dot(k, p) * (1 - c);
    }

    private static double[]? SolveLinear(double[] a, double[] b)
    {
        int n = b.Length;
        var m = (double[])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r * n + c]) > Math.Abs(m[p * n + c])) p = r;
            if (Math.Abs(m[p * n + c]) < 1e-12) return null;
            if (p != c)
            {
                for (int k = 0; k < n; k++) (m[c * n + k], m[p * n + k]) = (m[p * n + k], m[c * n + k]);
                (x[c], x[p]) = (x[p], x[c]);
            }
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = m[r * n + c] / m[c * n + c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) m[r * n + k] -= f * m[c * n + k];
                x[r] -= f * x[c];
            }
        }
        for (int c = 0; c < n; c++) x[c] /= m[c * n + c];
        return x;
    }
}
