using System.Numerics;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using RaceAiPlugin.Core;

namespace RaceAiPlugin;

/// <summary>Data and actions for the dashboard (desktop GUI).</summary>
public sealed partial class RaceAiService
{
    public string ServerName => _serverConfig.Server.Name;
    public string AdminPassword => _serverConfig.Server.AdminPassword ?? "";
    public bool DashboardRemoteAccess => _config.DashboardRemoteAccess;

    /// <summary>Racing line, pit lane, start line, sector splits and section names in world X/Z (for the map).</summary>
    public object TrackOutline()
    {
        lock (_lock)
        {
            if (_track == null) return new { available = false };
            var line = _track.Line;
            var pts = new List<float[]>();
            for (float s = 0; s < line.Length; s += 8)
            {
                var p = line.PositionAt(s, 0);
                var i = line.IndexAt(s);
                var l = line.PositionAt(s, -line.RoomMinus[i]);
                var r = line.PositionAt(s, line.RoomPlus[i]);
                pts.Add([R(p.X), R(p.Z), R(l.X), R(l.Z), R(r.X), R(r.Z)]);
            }
            var pit = new List<float[]>();
            if (_track.PitLane is { } lane)
                for (float s = 0; s < lane.Length; s += 6)
                {
                    var p = lane.PositionAt(s, 0);
                    pit.Add([R(p.X), R(p.Z)]);
                }
            var marks = new List<object>();
            var start = line.PositionAt(_track.StartLineS, 0);
            marks.Add(new { kind = "start", x = R(start.X), z = R(start.Z), name = "Start/Ziel" });
            int n = 2;
            foreach (var split in _sectorSplits)
            {
                var p = line.PositionAt(line.WrapS(split + _track.StartLineS), 0);
                marks.Add(new { kind = "sector", x = R(p.X), z = R(p.Z), name = $"S{n++}" });
            }
            foreach (var sec in _track.Info.Sections)
            {
                float mid = (sec.Start + sec.End) / 2;
                if (sec.End < sec.Start) mid = (sec.Start + sec.End + 1) / 2 % 1;
                var p = line.PositionAt(line.WrapS(mid * line.Length + _track.StartLineS), 0);
                marks.Add(new { kind = "section", x = R(p.X), z = R(p.Z), name = sec.Name });
            }
            return new { available = true, track = _serverConfig.Server.Track, length = line.Length, line = pts, pit, marks };
        }
    }

    private static float R(float v) => MathF.Round(v, 1);
    private static float? Lap(uint ms) => ms is 0 or >= 999999999 ? null : ms / 1000f;

    /// <summary>Everything the dashboard shows, polled a few times per second.</summary>
    public object State()
    {
        lock (_lock)
        {
            var session = _sessionManager.CurrentSession;
            var weather = _weatherManager.CurrentWeather;
            var line = _track?.Line;
            var cars = new List<Dictionary<string, object?>>();

            foreach (var car in _entryCarManager.EntryCars)
            {
                _slotsBySessionId.TryGetValue(car.SessionId, out var slot);
                bool bot = slot is { Active: true };
                var client = car.Client;
                if (!bot && client == null) continue;

                var result = session.Results != null && session.Results.TryGetValue(car.SessionId, out var r) ? r : null;
                var pos = car.Status.Position;
                float s = line != null ? line.WrapS(line.Project(pos).S - _track!.StartLineS) : 0;
                var d = new Dictionary<string, object?>
                {
                    ["id"] = car.SessionId,
                    ["name"] = bot ? slot!.Bot.Name : client!.Name,
                    ["model"] = car.Model,
                    ["bot"] = bot,
                    ["x"] = R(pos.X), ["z"] = R(pos.Z),
                    ["speed"] = MathF.Round(car.Status.Velocity.Length() * 3.6f),
                    ["laps"] = result?.NumLaps ?? 0,
                    ["best"] = Lap(result?.BestLap ?? 0),
                    ["last"] = Lap(result?.LastLap ?? 0),
                    ["finished"] = result?.HasCompletedLastLap ?? false,
                    ["progress"] = line != null ? (result?.NumLaps ?? 0) + s / line.Length : 0,
                    ["sector"] = line != null ? SectorAt(line.WrapS(s + _track!.StartLineS)) : 0,
                };
                if (bot)
                {
                    var b = slot!.Bot;
                    d["strength"] = MathF.Round(b.Driver.Level, 1);
                    d["aggression"] = MathF.Round(b.Driver.Aggression * 100);
                    d["personality"] = b.Driver.Personality.Name;
                    d["errors"] = MathF.Round(b.Driver.Errors, 2);
                    d["phase"] = b.Phase.ToString();
                    d["pit"] = b.Pit.ToString();
                    d["fuel"] = MathF.Round(b.Fuel, 1);
                    d["fuelCapacity"] = b.Car.FuelCapacity;
                    d["tyres"] = MathF.Round(b.Car.TyreGripAt(b.TyreVirtualKm) * 100, 1);
                    d["damage"] = MathF.Round(RaceWorld.BodyDamagePercent(b));
                    d["suspension"] = MathF.Round(b.Suspension * 100);
                    d["mistake"] = b.Mistake == MistakeKind.None ? null : b.Mistake.ToString();
                    d["mistakes"] = b.MistakeCount;
                    d["spins"] = b.SpinCount;
                    d["stops"] = b.PitStops;
                    d["ghost"] = b.GhostUntil > Now;
                    d["weaving"] = b.Weaving;
                    d["pressure"] = MathF.Round(b.Pressure, 2);
                    d["impatience"] = MathF.Round(b.Impatience, 2);
                }
                else
                {
                    d["admin"] = client!.IsAdministrator;
                    d["guid"] = client.Guid.ToString();
                }
                cars.Add(d);
            }

            // order: race by progress, otherwise by best lap
            List<Dictionary<string, object?>> ordered = _sessionType == SessionType.Race
                ? cars.OrderByDescending(c => (float)c["progress"]!).ToList()
                : cars.OrderBy(c => (float?)c["best"] ?? float.MaxValue).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i]["pos"] = i + 1;

            return new
            {
                server = _serverConfig.Server.Name,
                session = new
                {
                    name = session.Configuration.Name,
                    type = _sessionType.ToString(),
                    timeLeft = session.Configuration.Laps > 0 && _sessionType == SessionType.Race ? (int?)null : session.TimeLeftMilliseconds / 1000,
                    laps = session.Configuration.Laps,
                    leaderLap = session.LeaderLapCount,
                    raceStarted = _raceStarted
                },
                weather = new
                {
                    type = weather.Type.WeatherFxType.ToString(),
                    ambient = MathF.Round(weather.TemperatureAmbient, 1),
                    road = MathF.Round(weather.TemperatureRoad, 1),
                    rain = MathF.Round(weather.RainIntensity, 2),
                    wetness = MathF.Round(weather.RainWetness, 2),
                    water = MathF.Round(weather.RainWater, 2),
                    grip = MathF.Round(weather.TrackGrip, 3),
                    time = _weatherManager.CurrentDateTime.TimeOfDay.ToString("HH:mm", null)
                },
                ai = new
                {
                    enabled = _world != null,
                    strength = _config.AiStrength,
                    spread = _config.AiStrengthSpread,
                    aggression = _config.AiAggression,
                    gridOrder = _config.BotGridOrder.ToString(),
                    features = FeatureStates(),
                    personalities = _personalities.Select(p => p.Personality.Name).DefaultIfEmpty("Balanced").ToList()
                },
                cars = ordered
            };
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
            ["lines"] = s?.LineErrors ?? false,
            ["contacts"] = s?.BotContacts ?? false,
            ["damage"] = s?.Damage ?? false,
            ["blueflags"] = _config.BlueFlags,
            ["yellowflags"] = s?.YellowFlags ?? false,
            ["flash"] = _config.FlashLights,
            ["highbeams"] = _config.HighBeams,
            ["raincaution"] = s?.RainCaution ?? false,
            ["realweather"] = _config.RealWeather,
            ["yellowchat"] = _config.YellowFlagChat,
            ["overtakechat"] = _config.AnnounceOvertakes,
        };
    }

    public bool SetDashboardFeature(string feature, bool on)
    {
        lock (_lock)
        {
            switch (feature)
            {
                case "yellowchat": _config.YellowFlagChat = on; return true;
                case "overtakechat": _config.AnnounceOvertakes = on; return true;
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
                if (p != null) bot.Driver.Personality = p;
            }
            if (pit) _world.RequestPitStop(bot, "admin");
            Serilog.Log.Information("Race AI: dashboard changed {Name}: strength {Strength:F1} %, aggression {Aggression:F0}, {Personality}{Pit}",
                bot.Name, bot.Driver.Level, bot.Driver.Aggression * 100, bot.Driver.Personality.Name, pit ? ", to the pits" : "");
            return true;
        }
    }

    public void SetGlobalStrength(float strength, float spread)
    {
        _config.AiStrength = Math.Clamp(strength, 50, 110);
        _config.AiStrengthSpread = Math.Clamp(spread, 0, 30);
        SetStrength(_config.AiStrength, _config.AiStrengthSpread);
    }

    public bool SetGridOrder(string order)
    {
        if (!Enum.TryParse<BotGridOrder>(order, true, out var o)) return false;
        _config.BotGridOrder = o;
        Serilog.Log.Information("Race AI: grid order for the next race: {Order}", o);
        return true;
    }

    public void SetGlobalAggression(float aggression)
    {
        _config.AiAggression = Math.Clamp(aggression, 0, 100);
        SetAggression(_config.AiAggression);
    }

    public void Chat(string message) => _entryCarManager.BroadcastChat(message);
}
