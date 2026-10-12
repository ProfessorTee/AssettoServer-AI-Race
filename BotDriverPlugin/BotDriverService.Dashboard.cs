using BotDriverPlugin.Core;

namespace BotDriverPlugin;

/// <summary>Data and actions for the admin page (WebPortalPlugin) and the bot API (/api/bots).</summary>
public sealed partial class BotDriverService
{

    /// <summary>
    /// The bots for the admin page (/api/bots/state): settings and per bot what only the bot plugin knows. The page gets everything
    /// else (session, weather, positions, laps) from the web portal and merges the bots in by car id.
    /// </summary>
    public object BotState()
    {
        lock (_lock)
        {
            var bots = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var slot in _slots)
            {
                if (!slot.Active) continue;
                var b = slot.Bot;
                bots[slot.EntryCar.SessionId.ToString()] = new Dictionary<string, object?>
                {
                    ["strength"] = MathF.Round(b.Driver.Level, 1),
                    ["aggression"] = MathF.Round(b.Driver.Aggression / 0.95f * 100),
                    ["level"] = MathF.Round(b.Driver.Pace * 1000) / 10,
                    ["phase"] = b.Phase.ToString(),
                    ["pit"] = b.Pit.ToString(),
                    ["fuel"] = MathF.Round(b.Fuel, 1),
                    ["fuelCapacity"] = b.Car.FuelCapacity,
                    ["tyres"] = MathF.Round(RaceWorld.WearGrip(b, b.TyreVirtualKm) * 100, 1),
                    ["compound"] = RaceWorld.CompoundName(b),
                    ["tyreTemp"] = new[] { MathF.Round(b.TyreTempFront), MathF.Round(b.TyreTempRear) },
                    ["damage"] = MathF.Round(RaceWorld.BodyDamagePercent(b)),
                    ["suspension"] = MathF.Round(b.Suspension * 100),
                    ["contacts"] = b.ContactCount,
                    ["stops"] = b.PitStops,
                    ["ghost"] = b.GhostUntil > Now,
                };
            }
            return new
            {
                ai = new
                {
                    enabled = _world != null,
                    strength = _config.AiStrength,
                    spread = _config.AiStrengthSpread,
                    aggression = _config.AiAggression,
                    gridOrder = _config.BotGridOrder.ToString(),
                    features = _field.FeatureStates()
                },
                bots,
                health = new { tickMs = Math.Round(_lastTickAvg, 2), tickMaxMs = Math.Round(_lastTickMax, 2), calibration = _field.CalibrationStatus }
            };
        }
    }

    // ---- server health for the dashboard: bot tick time, CPU, memory
    private double _tickSum, _tickMax;
    private int _tickCount;
    private double _lastTickAvg, _lastTickMax;
    private long _tickWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private void RecordTick(double ms)
    {
        _tickSum += ms;
        _tickCount++;
        if (ms > _tickMax) _tickMax = ms;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_tickWindowStart).TotalSeconds >= 10)
        {
            _lastTickAvg = _tickCount > 0 ? _tickSum / _tickCount : 0;
            _lastTickMax = _tickMax;
            _tickSum = _tickMax = 0;
            _tickCount = 0;
            _tickWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
}
