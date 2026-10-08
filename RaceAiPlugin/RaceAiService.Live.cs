using AssettoServer.Server;
using AssettoServer.Server.Extensions;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;

namespace RaceAiPlugin;

/// <summary>Raw data for the public live page (<see cref="LiveFeed"/>): one call per frame, everything read under the lock at once.</summary>
public sealed partial class RaceAiService
{
    internal sealed class LiveCar
    {
        public byte Id;
        public string Name = "";
        public string Model = "";
        public string Kind = ""; // "ai", "clone", "player"
        public ulong Guid;
        public float X, Z, Vx, Vz, Speed;
        public int Gear, Rpm;
        public float Throttle;
        public bool Brake;
        public uint Laps, Best, Last;
        public bool Finished;
        public float LapFraction; // 0..1 from the start line
        public int Sector;
        public bool InPit;
        public int Stops;
        public float? Tyres;
    }

    internal sealed class LiveFrame
    {
        public string Server = "";
        public string Track = "";
        public string Session = "";
        public string SessionType = "";
        public long? TimeLeft;
        public int Laps;
        public int LeaderLap;
        public bool RaceStarted;
        public long ServerMs;
        public long SessionStart;
        public string Weather = "";
        public float Ambient, Road, Rain, Wetness;
        public string TimeOfDay = "";
        public int Sectors;
        public List<LiveCar> Cars = [];
    }

    private readonly Dictionary<byte, int> _liveHints = new();

    internal LiveFrame LiveSnapshot()
    {
        lock (_lock)
        {
            var session = _sessionManager.CurrentSession;
            var weather = _weatherManager.CurrentWeather;
            var line = _track?.Line;
            var f = new LiveFrame
            {
                Server = _serverConfig.Server.Name,
                Track = _track == null ? "" : TrackKey(),
                Session = session.Configuration.Name,
                SessionType = _sessionType.ToString(),
                TimeLeft = session.Configuration.Laps > 0 && _sessionType == SessionType.Race ? null : session.TimeLeftMilliseconds / 1000,
                Laps = session.Configuration.Laps,
                LeaderLap = (int)session.LeaderLapCount,
                RaceStarted = _raceStarted,
                ServerMs = _sessionManager.ServerTimeMilliseconds,
                SessionStart = session.StartTimeMilliseconds,
                Weather = weather.Type.WeatherFxType.ToString(),
                Ambient = weather.TemperatureAmbient,
                Road = weather.TemperatureRoad,
                Rain = weather.RainIntensity,
                Wetness = weather.RainWetness,
                TimeOfDay = _weatherManager.CurrentDateTime.TimeOfDay.ToString("HH:mm", null),
                Sectors = _sectorSplits.Count + 1,
            };

            foreach (var car in _entryCarManager.EntryCars)
            {
                _slotsBySessionId.TryGetValue(car.SessionId, out var slot);
                bool bot = slot is { Active: true };
                var client = car.Client;
                if (!bot && client == null) continue;
                var st = bot ? slot!.Status : car.Status;
                var result = session.Results != null && session.Results.TryGetValue(car.SessionId, out var r) ? r : null;
                var c = new LiveCar
                {
                    Id = car.SessionId,
                    Name = bot ? slot!.TakeoverGuid != null ? slot.TakeoverPlayer : slot.Bot.Name : client!.Name ?? "",
                    Model = car.Model,
                    Kind = bot ? slot!.TakeoverGuid != null ? "clone" : "ai" : "player",
                    Guid = bot ? slot!.TakeoverGuid ?? 0 : client!.Guid,
                    X = st.Position.X, Z = st.Position.Z, Vx = st.Velocity.X, Vz = st.Velocity.Z,
                    Speed = st.Velocity.Length() * 3.6f,
                    Gear = st.Gear - 1, // network: 0 = reverse, 1 = neutral
                    Rpm = st.EngineRpm,
                    Throttle = st.Gas / 255f,
                    Brake = (st.StatusFlag & CarStatusFlags.BrakeLightsOn) != 0,
                    Laps = result?.NumLaps ?? 0,
                    Best = result?.BestLap ?? 0,
                    Last = result?.LastLap ?? 0,
                    Finished = result?.HasCompletedLastLap ?? false,
                };
                if (line != null)
                {
                    _liveHints.TryGetValue(car.SessionId, out int hint);
                    var pr = line.Project(st.Position, _liveHints.ContainsKey(car.SessionId) ? hint : -1);
                    _liveHints[car.SessionId] = pr.Index;
                    float fromStart = line.WrapS(pr.S - _track!.StartLineS);
                    c.LapFraction = fromStart / line.Length;
                    c.Sector = SectorAt(pr.S);
                }
                if (bot)
                {
                    var b = slot!.Bot;
                    c.InPit = b.InPitLane;
                    c.Stops = b.PitStops;
                    c.Tyres = MathF.Round(b.Car.TyreGripAt(b.TyreVirtualKm) * 100, 1);
                }
                else
                {
                    c.InPit = _world?.ExternalInPitLane(car.SessionId) ?? false;
                }
                f.Cars.Add(c);
            }
            return f;
        }
    }

    // ------------------------------------------------------------------ IDrivenCars (other plugins: live page, server name)

    public bool Ready => _world != null;

    public DrivenCarInfo? Get(EntryCar car)
    {
        lock (_lock)
        {
            if (!_slotsBySessionId.TryGetValue(car.SessionId, out var slot) || !slot.Active) return null;
            var b = slot.Bot;
            return new DrivenCarInfo
            {
                Name = slot.TakeoverGuid != null ? slot.TakeoverPlayer : b.Name,
                Kind = slot.TakeoverGuid != null ? "clone" : "ai",
                Guid = slot.TakeoverGuid ?? 0,
                Status = slot.Status,
                InPitLane = b.InPitLane,
                PitStops = b.PitStops,
                TyrePercent = MathF.Round(b.Car.TyreGripAt(b.TyreVirtualKm) * 100, 1)
            };
        }
    }

    public bool? PlayerInPitLane(EntryCar car)
    {
        lock (_lock) return _world?.ExternalInPitLane(car.SessionId);
    }
}

