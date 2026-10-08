using TrackGeometry;
using System.Numerics;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
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
                    ["aggression"] = MathF.Round(b.Driver.Aggression * 100),
                    ["personality"] = b.Driver.Personality.Name,
                    ["errors"] = MathF.Round(b.Driver.Errors, 2),
                    ["phase"] = b.Phase.ToString(),
                    ["pit"] = b.Pit.ToString(),
                    ["fuel"] = MathF.Round(b.Fuel, 1),
                    ["fuelCapacity"] = b.Car.FuelCapacity,
                    ["tyres"] = MathF.Round(b.Car.TyreGripAt(b.TyreVirtualKm) * 100, 1),
                    ["tyreTemp"] = new[] { MathF.Round(b.TyreTempFront), MathF.Round(b.TyreTempRear) },
                    ["clone"] = b.Clone?.PlayerName,
                    ["duel"] = slot == _duelSlot,
                    ["boost"] = MathF.Round(b.PaceBoost * 1000) / 10, // rubber band, % pace
                    ["takeover"] = slot.TakeoverGuid != null,
                    ["damage"] = MathF.Round(RaceWorld.BodyDamagePercent(b)),
                    ["suspension"] = MathF.Round(b.Suspension * 100),
                    ["mistake"] = b.Mistake == MistakeKind.None ? null : b.Mistake.ToString(),
                    ["mistakes"] = b.MistakeCount,
                    ["spins"] = b.SpinCount,
                    ["stops"] = b.PitStops,
                    ["ghost"] = b.GhostUntil > Now,
                    ["weaving"] = b.Weaving,
                    ["pressure"] = MathF.Round(b.Pressure, 2),
                    ["impatience"] = MathF.Round(b.Impatience, 2),
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
                    rubber = _config.RubberBanding,
                    gridOrder = _config.BotGridOrder.ToString(),
                    features = FeatureStates(),
                    personalities = _personalities.Select(p => p.Personality.Name).DefaultIfEmpty("Balanced").ToList()
                },
                bots,
                health = new { tickMs = Math.Round(_lastTickAvg, 2), tickMaxMs = Math.Round(_lastTickMax, 2), calibration = CalibrationStatus }
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

    private Dictionary<string, bool> FeatureStates()
    {
        var s = _world?.Settings;
        return new Dictionary<string, bool>
        {
            ["errors"] = s?.HumanErrors ?? false,
            ["spins"] = s?.Spins ?? false,
            ["grass"] = s?.GrassMoments ?? false,
            ["takeover"] = _config.TakeOverDisconnectedPlayers,
            ["lines"] = s?.LineErrors ?? false,
            ["contacts"] = s?.BotContacts ?? false,
            ["damage"] = s?.Damage ?? false,
            ["blueflags"] = _config.BlueFlags,
            ["yellowflags"] = s?.YellowFlags ?? false,
            ["flash"] = _config.FlashLights,
            ["highbeams"] = _config.HighBeams,
            ["raincaution"] = s?.RainCaution ?? false,
            ["yellowchat"] = _config.YellowFlagChat,
            ["overtakechat"] = _config.AnnounceOvertakes,
            ["personallines"] = s?.PersonalLines ?? _config.PersonalLines,
            ["realstart"] = s?.RealisticStart ?? _config.RealisticStart,
        };
    }

    /// <summary>Recorded players on this track (DriverRecorder), with their profile once it has been built.</summary>
    public object CloneList()
    {
        var lib = _clones;
        if (lib == null) return Array.Empty<object>();
        return lib.List().Select(e =>
        {
            var p = e.CleanLaps > 0 ? lib.Get(e.Guid, e.Car) : null;
            return new
            {
                guid = e.Guid, name = e.Name, car = e.Car, clean = e.CleanLaps, all = e.AllLaps,
                best = p?.BestLap, average = p?.AverageLap, used = p?.LapsUsed ?? 0
            };
        }).ToList();
    }

    public bool SetDashboardFeature(string feature, bool on)
    {
        lock (_lock)
        {
            switch (feature)
            {
                case "yellowchat": _config.YellowFlagChat = on; _configWriter.Set("YellowFlagChat", on); return true;
                case "overtakechat": _config.AnnounceOvertakes = on; _configWriter.Set("AnnounceOvertakes", on); return true;
                case "takeover": _config.TakeOverDisconnectedPlayers = on; _configWriter.Set("TakeOverDisconnectedPlayers", on); return true;
            }
        }
        return SetFeature(feature, on);
    }

    /// <summary>Changes one bot. Null values stay as they are.</summary>
    public bool UpdateBot(int id, float? strength, float? aggression, string? personality, bool pit)
    {
        lock (_lock)
        {
            if (_world == null || !_slotsBySessionId.TryGetValue((byte)id, out var slot) || !slot.Active) return false;
            var bot = slot.Bot;
            if (strength is { } st && _calibrations.TryGetValue(bot.Car, out var cal))
                ApplyStrength(bot, Math.Clamp(st, 50, 110), cal);
            if (aggression is { } ag) bot.Driver.Aggression = Math.Clamp(ag / 100f, 0, 1);
            if (!string.IsNullOrWhiteSpace(personality))
            {
                if (_personalities.Count == 0) PickPersonality(null);
                var p = _personalities.FirstOrDefault(x => x.Personality.Name.Equals(personality, StringComparison.OrdinalIgnoreCase)).Personality;
                if (p != null) { bot.Driver.Personality = p; _world?.ResetStyle(bot); }
            }
            if (pit) _world.RequestPitStop(bot, "admin");
            Serilog.Log.Information("BotDriver: dashboard changed {Name}: strength {Strength:F1} %, aggression {Aggression:F0}, {Personality}{Pit}",
                bot.Name, bot.Driver.Level, bot.Driver.Aggression * 100, bot.Driver.Personality.Name, pit ? ", to the pits" : "");
            return true;
        }
    }

    public float CurrentSpread => _config.AiStrengthSpread;

    public void SetGlobalStrength(float strength, float spread)
    {
        _config.AiStrength = Math.Clamp(strength, 50, 110);
        _config.AiStrengthSpread = Math.Clamp(spread, 0, 30);
        SetStrength(_config.AiStrength, _config.AiStrengthSpread);
        _configWriter.Set("AiStrength", _config.AiStrength);
        _configWriter.Set("AiStrengthSpread", _config.AiStrengthSpread);
    }

    public bool SetGridOrder(string order)
    {
        if (!Enum.TryParse<BotGridOrder>(order, true, out var o)) return false;
        _config.BotGridOrder = o;
        _configWriter.Set("BotGridOrder", o);
        Serilog.Log.Information("BotDriver: grid order for the next race: {Order}", o);
        return true;
    }

    public void SetGlobalAggression(float aggression)
    {
        _config.AiAggression = Math.Clamp(aggression, 0, 100);
        SetAggression(_config.AiAggression);
        _configWriter.Set("AiAggression", _config.AiAggression);
    }

}
