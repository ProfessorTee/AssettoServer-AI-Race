using System.Globalization;
using System.Text;
using BotDriverPlugin.Core;

namespace BotDriverPlugin;

/// <summary>
/// Debug telemetry (only while debugging): every bot twice a second into bot-debug/&lt;start&gt;_&lt;session&gt;.csv: where it is, which way it
/// points, speed, inputs, tyres, fuel, damage and what it intends. For evaluating real races afterwards.
/// </summary>
public sealed partial class BotDriverService
{
    private const string TelemetryFolder = "bot-debug";
    private const double TelemetryInterval = 0.5;
    private readonly StringBuilder _telemetry = new();
    private string? _telemetryFile;
    private double _nextTelemetry, _nextTelemetryFlush;

    private const string TelemetryHeader = "time;session;car;name;phase;lap;s;offset;targetOffset;x;y;z;headingDeg;headingErrorDeg;kmh;targetKmh;"
        + "lateral;accel;throttle;brake;gear;rpm;compound;tyreTempF;tyreTempR;tyreKm;wearGrip;carGrip;fuel;damageGrip;suspension;"
        + "dirtyAir;draft;pressure;impatience;intention\n";

    /// <summary>The file of this session: a new one after a session change or when debugging gets switched on again.</summary>
    private void EndTelemetryFile()
    {
        FlushTelemetry();
        _telemetryFile = null;
    }

    private bool TelemetryDue(double now) => DebugOn && now >= _nextTelemetry;

    /// <summary>Under the race lock, from the tick: one row for a bot.</summary>
    private void TelemetryRow(RaceWorld world, BotSlot slot, in BotPose pose, double now)
    {
        if (_telemetryFile == null)
        {
            string session = _sessionManager.CurrentSession.Configuration.Name ?? _sessionType.ToString();
            _telemetryFile = Path.Join(TelemetryFolder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{string.Concat(session.Split(Path.GetInvalidFileNameChars()))}.csv");
        }
        var b = slot.Bot;
        float heading = (pose.Rotation.X + MathF.PI / 2) * 180 / MathF.PI;
        var c = CultureInfo.InvariantCulture;
        _telemetry.Append(c, $"{now:F2};{_sessionType};{slot.EntryCar.SessionId};{Csv(b.Name)};{b.Phase};{b.LapsCompleted};{world.Line.WrapS((float)b.Distance):F1};")
            .Append(c, $"{b.Offset:F2};{b.TargetOffset:F2};{pose.Position.X:F2};{pose.Position.Y:F2};{pose.Position.Z:F2};{heading:F1};{world.HeadingError(b, pose):F1};")
            .Append(c, $"{b.Speed * 3.6f:F1};{b.TargetSpeed * 3.6f:F1};{b.LateralSpeed:F2};{b.Accel:F2};{b.Throttle:F2};{b.Brake:F2};{pose.Gear};{pose.Rpm};")
            .Append(c, $"{RaceWorld.CompoundName(b)};{b.TyreTempFront:F1};{b.TyreTempRear:F1};{b.TyreVirtualKm:F1};{RaceWorld.WearGrip(b, b.TyreVirtualKm):F3};{b.CarGrip:F3};")
            .Append(c, $"{b.Fuel:F1};{RaceWorld.DamageGrip(b):F3};{b.Suspension:F2};{b.DirtyAir:F2};{b.Draft:F2};{b.Pressure:F2};{b.Impatience:F2};{Csv(world.Intention(b))}\n");
    }

    /// <summary>After the bots of a tick: the next sample time, and to disk every 5 s.</summary>
    private void TelemetryAfterTick(double now, bool sampled)
    {
        if (sampled) _nextTelemetry = now + TelemetryInterval;
        if (now < _nextTelemetryFlush) return;
        _nextTelemetryFlush = now + 5;
        FlushTelemetry();
    }

    /// <summary>Written off the race lock and the tick.</summary>
    private void FlushTelemetry()
    {
        if (_telemetry.Length == 0 || _telemetryFile is not { } path) return;
        string rows = _telemetry.ToString();
        _telemetry.Clear();
        _ = Task.Run(() =>
        {
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(TelemetryFolder);
                    File.AppendAllText(path, (File.Exists(path) ? "" : TelemetryHeader) + rows);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "BotDriver: debug telemetry not writable");
            }
        });
    }
}
