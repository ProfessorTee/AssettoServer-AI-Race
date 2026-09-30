using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Configuration.Kunos;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Incoming;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AssettoServer.Server;

public class SessionManager : BackgroundService, IHostedLifecycleService
{
    private readonly ACServerConfiguration _configuration;
    private readonly Func<SessionConfiguration, SessionState> _sessionStateFactory;
    private readonly Stopwatch _timeSource = new();
    private readonly EntryCarManager _entryCarManager;
    private readonly Lazy<WeatherManager> _weatherManager;
    private readonly IHostApplicationLifetime _applicationLifetime;

    public int CurrentSessionIndex { get; private set; } = -1;
    public bool IsLastRaceInverted { get; private set; } = false;
    public bool MustInvertGrid { get; private set; } = false;
    private long _lastPlayerSeenMilliseconds = long.MinValue / 2;
    public SessionState CurrentSession { get; private set; } = null!;

    public long ServerTimeMilliseconds => _timeSource.ElapsedMilliseconds;

    public bool IsOpen => CurrentSession.Configuration.IsOpen switch
    {
        IsOpenMode.Open => true,
        IsOpenMode.CloseAtStart => !CurrentSession.IsCutoffReached,
        _ => false,
    };

    /// <summary>
    /// Fires when a new session is started
    /// </summary>
    public event EventHandler<SessionManager, SessionChangedEventArgs>? SessionChanged;

    /// <summary>
    /// Fires when a lap of a server-driven AI slot was registered via <see cref="OnAiLapCompleted"/>
    /// </summary>
    public event EventHandler<EntryCar, LapCompletedEventArgs>? AiLapCompleted;

    public SessionManager(ACServerConfiguration configuration,
        Func<SessionConfiguration, SessionState> sessionStateFactory,
        EntryCarManager entryCarManager,
        Lazy<WeatherManager> weatherManager,
        IHostApplicationLifetime applicationLifetime)
    {
        _configuration = configuration;
        _sessionStateFactory = sessionStateFactory;
        _entryCarManager = entryCarManager;
        _weatherManager = weatherManager;
        _applicationLifetime = applicationLifetime;

        _entryCarManager.ClientConnected += OnClientConnected;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                if (IsSessionOver())
                {
                    NextSession();
                }

                switch (CurrentSession.Configuration.Type)
                {
                    case SessionType.Qualifying or SessionType.Practice:
                    {
                        if (CurrentSession is { SessionOverFlag: true, HasSentRaceOverPacket: false })
                        {
                            CalcOverTime();
                            CurrentSession.EndTimeMilliseconds = 60_000 * CurrentSession.Configuration.Time + CurrentSession.StartTimeMilliseconds;
                            if (ServerTimeMilliseconds - CurrentSession.EndTimeMilliseconds > CurrentSession.OverTimeMilliseconds)
                                SendSessionOver();
                        }

                        if (CurrentSession.HasSentRaceOverPacket
                            && ServerTimeMilliseconds > _configuration.Server.ResultScreenTime * 1000L + CurrentSession.OverTimeMilliseconds)
                        {
                            NextSession();
                        }

                        break;
                    }
                    case SessionType.Race:
                    {
                        if (CurrentSession is { EndTimeMilliseconds: not 0L, HasSentRaceOverPacket: false })
                        {
                            CalcOverTime();
                            if (ServerTimeMilliseconds - CurrentSession.EndTimeMilliseconds > CurrentSession.OverTimeMilliseconds)
                                SendSessionOver();
                        }

                        if (CurrentSession.HasSentRaceOverPacket
                            && ServerTimeMilliseconds > _configuration.Server.ResultScreenTime * 1000L + CurrentSession.OverTimeMilliseconds)
                        {
                            NextSession();
                        }

                        break;
                    }
                }

                SendSessionStart();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in session service update");
            }
        }
    }

    public bool OnLapCompleted(ACTcpClient client, LapCompletedIncoming lap)
        => OnLapCompleted(client.EntryCar, client.Name, lap.LapTime, lap.Cuts);

    /// <summary>
    /// Registers a completed lap for a slot that is driven by the server (e.g. a racing AI plugin) and
    /// broadcasts the updated lap/leaderboard to all clients.
    /// </summary>
    public bool OnAiLapCompleted(EntryCar entryCar, uint lapTime, int cuts = 0)
    {
        if (CurrentSession.Results != null && CurrentSession.Results.TryGetValue(entryCar.SessionId, out var result))
        {
            result.Name = entryCar.AiName ?? result.Name;
        }

        if (!OnLapCompleted(entryCar, entryCar.AiName, lapTime, cuts))
            return false;

        var packet = CreateLapCompletedPacket(entryCar.SessionId, lapTime, cuts);
        _entryCarManager.BroadcastPacket(packet);
        AiLapCompleted?.Invoke(entryCar, new LapCompletedEventArgs(packet));
        return true;
    }

    private bool OnLapCompleted(EntryCar entryCar, string? name, uint lapTime, int cuts)
    {
        int timestamp = (int)ServerTimeMilliseconds;

        var entryCarResult = CurrentSession.Results?[entryCar.SessionId] ?? throw new InvalidOperationException("Current session does not have results set");

        if (entryCarResult.HasCompletedLastLap)
        {
            Log.Debug("Lap rejected by {ClientName}, already finished", name);
            return false;
        }

        if (CurrentSession.Configuration.Type == SessionType.Race
            && entryCarResult.NumLaps >= CurrentSession.Configuration.Laps
            && !CurrentSession.Configuration.IsTimedRace)
        {
            Log.Debug("Lap rejected by {ClientName}, race over", name);
            return false;
        }

        Log.Information("Lap completed by {ClientName}, {NumCuts} cuts, laptime {LapTime}", name, cuts, TimeSpan.FromMilliseconds(lapTime).ToString(@"mm\:ss\.ffff"));

        if (CurrentSession.Configuration.Type == SessionType.Race || cuts == 0)
        {
            entryCarResult.LastLap = lapTime;
            entryCarResult.NumLaps++;
            entryCarResult.TotalTime = (uint)(CurrentSession.SessionTimeMilliseconds - entryCar.Ping / 2);

            if (lapTime < entryCarResult.BestLap)
            {
                entryCarResult.BestLap = lapTime;
            }

            var oldLeaderLapCount = CurrentSession.LeaderLapCount;
            if (entryCarResult.NumLaps > CurrentSession.LeaderLapCount)
            {
                CurrentSession.LeaderLapCount = entryCarResult.NumLaps;
            }

            if (CurrentSession.Configuration.Type == SessionType.Race)
            {
                foreach (var res in CurrentSession.Results
                             .OrderByDescending(car => car.Value.NumLaps)
                             .ThenBy(car => car.Value.TotalTime)
                             .Select((x, i) => new { Car = x, Index = i }) )
                {
                    res.Car.Value.RacePos = (uint)res.Index;
                }
            }

            if (CurrentSession.SessionOverFlag)
            {
                if (CurrentSession.Configuration is { Type: SessionType.Race, IsTimedRace: true })
                {
                    if (_configuration.Server.HasExtraLap)
                    {
                        if (entryCarResult.NumLaps <= oldLeaderLapCount)
                        {
                            entryCarResult.HasCompletedLastLap = CurrentSession.LeaderHasCompletedLastLap;
                        }
                        else if (CurrentSession.TargetLap > 0)
                        {
                            if (entryCarResult.NumLaps >= CurrentSession.TargetLap)
                            {
                                CurrentSession.LeaderHasCompletedLastLap = true;
                                entryCarResult.HasCompletedLastLap = true;
                            }
                        }
                        else
                        {
                            CurrentSession.TargetLap = entryCarResult.NumLaps + 1;
                        }
                    }
                    else if (entryCarResult.NumLaps <= oldLeaderLapCount)
                    {
                        entryCarResult.HasCompletedLastLap = CurrentSession.LeaderHasCompletedLastLap;
                    }
                    else
                    {
                        CurrentSession.LeaderHasCompletedLastLap = true;
                        entryCarResult.HasCompletedLastLap = true;
                    }
                }
                else
                {
                    entryCarResult.HasCompletedLastLap = true;
                }
            }

            if (CurrentSession.Configuration.Type != SessionType.Race)
            {
                if (CurrentSession.EndTimeMilliseconds != 0)
                {
                    entryCarResult.HasCompletedLastLap = true;
                }
            }
            else if (CurrentSession.Configuration.IsTimedRace)
            {
                if (CurrentSession is { LeaderHasCompletedLastLap: true, EndTimeMilliseconds: 0 })
                {
                    CurrentSession.EndTimeMilliseconds = timestamp;
                }
            }
            else if (entryCarResult.NumLaps != CurrentSession.Configuration.Laps)
            {
                if (CurrentSession.EndTimeMilliseconds == 0)
                    return true;
                entryCarResult.HasCompletedLastLap = true;
            }
            else switch (entryCarResult.HasCompletedLastLap)
            {
                case false:
                    if (CurrentSession.EndTimeMilliseconds == 0)
                        CurrentSession.EndTimeMilliseconds = timestamp;
                    entryCarResult.HasCompletedLastLap = true;
                    break;
                case true when CurrentSession.EndTimeMilliseconds == 0:
                    return true;
                case true:
                    entryCarResult.HasCompletedLastLap = true;
                    break;
            }

            return true;
        }

        if (CurrentSession.EndTimeMilliseconds == 0)
            return true;

        entryCarResult.HasCompletedLastLap = true;
        return false;
    }

    public LapCompletedOutgoing CreateLapCompletedPacket(byte sessionId, uint lapTime, int cuts)
    {
        // TODO: double check and rewrite this
        if (CurrentSession.Results == null)
            throw new ArgumentNullException(nameof(CurrentSession.Results));

        var laps = CurrentSession.Results
            .OrderBy(result => string.IsNullOrEmpty(result.Value.Name))
            .ThenBy(result => result.Value.Name)
            .Select(result => new LapCompletedOutgoing.CompletedLap
            {
                SessionId = result.Key,
                LapTime = CurrentSession.Configuration.Type == SessionType.Race ? result.Value.TotalTime : result.Value.BestLap,
                NumLaps = (ushort)result.Value.NumLaps,
                HasCompletedLastLap = (byte)(result.Value.HasCompletedLastLap ? 1 : 0),
                RacePos = (byte)result.Value.RacePos,
            })
            .OrderBy(lap => lap.LapTime);

        return new LapCompletedOutgoing
        {
            SessionId = sessionId,
            LapTime = lapTime,
            Cuts = (byte)cuts,
            Laps = laps.ToArray(),
            TrackGrip = _weatherManager.Value.CurrentWeather.TrackGrip
        };
    }

    private bool IsSessionOver()
    {
        if (CurrentSession.Configuration.Infinite)
        {
            return false;
        }

        if (CurrentSession.Configuration.Type == SessionType.Booking)
        {
            // TODO Currently unused, maybe for later, when i care about sessions without pickup mode :shrug:
            return CurrentSession.TimeLeftMilliseconds == 0;
        }

        if (CurrentSession.Configuration.Type is SessionType.Practice or SessionType.Qualifying)
        {
            return false;
        }

        var connectedCount = _entryCarManager.ConnectedCars.Count;
        // Server-driven AI slots (e.g. racing AI plugins) count as participants as soon as one player is connected,
        // so a single player can race against them. Without any player the race is skipped as before,
        // unless an AI stands in for a player who left (his clone keeps his car racing until he's back).
        var standIn = _entryCarManager.EntryCars.Any(c => c.Client == null && c.ExternalAiController is { KeepsSessionAlive: true });
        if (standIn && connectedCount == 0) connectedCount = 1;
        // the last player just left: give plugins a moment to put a stand-in into his car before the race is skipped
        if (connectedCount > 0) _lastPlayerSeenMilliseconds = ServerTimeMilliseconds;
        else if (ServerTimeMilliseconds - _lastPlayerSeenMilliseconds < _configuration.EmptyRaceGraceMilliseconds) return false;
        var participantCount = connectedCount == 0 ? 0 : connectedCount + _entryCarManager.EntryCars.Count(c => c.Client == null && c.ExternalAiController != null);
        
        switch (CurrentSession.Configuration.IsOpen)
        {
            case IsOpenMode.Closed when participantCount < 2:
                Log.Information("Skipping race session: didn't reach minimum player count before cutoff ({PlayerCount}/2). Use 'IS_OPEN=1' to allow joining during the race", participantCount);
                return true;
            case IsOpenMode.Closed:
                return false;
            case IsOpenMode.CloseAtStart when participantCount >= 2 ||
                                              ServerTimeMilliseconds <= CurrentSession.StartTimeMilliseconds:
                return false;
            case IsOpenMode.Open when connectedCount > 0 ||
                                      ServerTimeMilliseconds <= CurrentSession.StartTimeMilliseconds:
                return false;
        }
        
        Log.Information("Skipping race session: no player connected");
        return true;
    }

    private void CalcOverTime()
    {
        if (_entryCarManager.EntryCars.All(c => c.Client == null))
        {
            CurrentSession.OverTimeMilliseconds = 0;
            return;
        }

        if (CurrentSession.Configuration.Type == SessionType.Race)
        {
            var overTimeMilliseconds = _configuration.Server.RaceOverTime * 1000L;
            if (CurrentSession.OverTimeMilliseconds == 0)
                CurrentSession.OverTimeMilliseconds = overTimeMilliseconds;

            if (CurrentSession.OverTimeMilliseconds == overTimeMilliseconds)
            {
                // server-driven AI slots (racing AI plugins) finish their race too, within RACE_OVER_TIME
                if (_entryCarManager.EntryCars.Where(c => c.Client is { HasSentFirstUpdate: true } || (c.Client == null && c.ExternalAiController != null))
                    .Any(car => CurrentSession.Results?[car.SessionId] is { HasCompletedLastLap: false }))
                {
                    return;
                }
            }
        }
        else
        {
            var overTimeMilliseconds = ServerTimeMilliseconds / 100 * _configuration.Server.QualifyMaxWait;
            if (CurrentSession.OverTimeMilliseconds == 0 || CurrentSession.OverTimeMilliseconds > overTimeMilliseconds)
                CurrentSession.OverTimeMilliseconds = overTimeMilliseconds;

            if (_entryCarManager.EntryCars
                .Where(c => c.Client is { HasSentFirstUpdate: true } || (c.Client == null && c.ExternalAiController != null))
                .Any(car => CurrentSession.Results?[car.SessionId] is { HasCompletedLastLap: false }
                            && car.Status.Velocity.LengthSquared() > 5))
            {
                return;
            }
        }

        CurrentSession.OverTimeMilliseconds = 1;
    }

    private void OnClientConnected(ACTcpClient client, EventArgs eventArgs)
    {
        var currentResult = CurrentSession.Results;
        
        if (currentResult != null && currentResult[client.SessionId].Guid != client.Guid)
        {
            currentResult[client.SessionId] = new EntryCarResult(client);
        }    
    }

    public void SetSession(int sessionId)
    {
        // TODO reset sun angle

        var previousSession = CurrentSession;
        Dictionary<byte, EntryCarResult>? previousSessionResults = CurrentSession?.Results; // breaks with CurrentSession.Result don't believe the IDE

        CurrentSession = _sessionStateFactory(_configuration.Sessions[sessionId]);
        CurrentSession.Results = new Dictionary<byte, EntryCarResult>();
        CurrentSession.StartTimeMilliseconds = ServerTimeMilliseconds;

        foreach (var entryCar in _entryCarManager.EntryCars)
        {
            CurrentSession.Results?.Add(entryCar.SessionId, new EntryCarResult(entryCar.Client));
        }

        var sessionLength = CurrentSession.Configuration switch
        {
            { Infinite: true } => "Infinite",
            { IsTimedRace: false } => $"{CurrentSession.Configuration.Laps} laps",
            _ => $"{CurrentSession.Configuration.Time} minutes"
        };
        Log.Information("Next session: {SessionName} - Length: {Length}", CurrentSession.Configuration.Name, sessionLength);

        if (CurrentSession.Configuration.Type == SessionType.Race)
        {
            CurrentSession.StartTimeMilliseconds = ServerTimeMilliseconds + (CurrentSession.Configuration.WaitTime * 1000);
        }
        else
        {
            IsLastRaceInverted = false;
        }

        _configuration.Server.DynamicTrack.TransferSession();
        // TODO weather

        int invertedCount = 0;
        if (previousSessionResults == null)
        {
            CurrentSession.Grid = _entryCarManager.EntryCars;
        }
        else
        {
            var grid = previousSessionResults
                .OrderBy(result => result.Value.BestLap)
                .Select(result => _entryCarManager.EntryCars[result.Key])
                .ToList();

            if (MustInvertGrid)
            {
                var inverted = previousSessionResults
                    .Take(_configuration.Server.InvertedGridPositions)
                    .OrderByDescending(result => result.Value.BestLap)
                    .Select(result => _entryCarManager.EntryCars[result.Key])
                    .ToList();

                for (var i = 0; i < inverted.Count; i++)
                {
                    grid[i] = inverted[i];
                }

                Log.Information("Inverted {Slots} grid slots", inverted.Count);

                invertedCount = inverted.Count;
            }

            CurrentSession.Grid = grid;
        }

        SessionChanged?.Invoke(this, new SessionChangedEventArgs(previousSession, CurrentSession, invertedCount));
        SendCurrentSession();

        Log.Information("Switching session to id {Id}", sessionId);
    }

    public bool RestartSession()
    {
        // StallSessionSwitch
        if (_entryCarManager.EntryCars.Any(c => c.Client is { HasSentFirstUpdate: false }))
            return false;

        SetSession(CurrentSessionIndex);
        return true;
    }

    public bool NextSession()
    {
        // StallSessionSwitch
        if (_entryCarManager.EntryCars.Any(c => c.Client is { HasSentFirstUpdate: false }))
            return false;

        MustInvertGrid = false;
        if (_configuration.Sessions.Count - 1 == CurrentSessionIndex)
        {
            if (_configuration.Server.Loop)
            {
                Log.Information("Looping sessions");
            }
            else if (CurrentSession.Configuration.Type != SessionType.Race || _configuration.Server.InvertedGridPositions == 0 || IsLastRaceInverted)
            {
                Log.Information("Set LOOP_MODE=1 in the server_cfg.ini to loop sessions");
                _applicationLifetime.StopApplication();
                return false;
            }

            if (CurrentSession.Configuration.Type == SessionType.Race && _configuration.Server.InvertedGridPositions != 0)
            {
                if (_configuration.Sessions.Count <= 1)
                {
                    MustInvertGrid = true;
                }
                else if (!IsLastRaceInverted)
                {
                    MustInvertGrid = true;
                    IsLastRaceInverted = true;
                    --CurrentSessionIndex;
                }
            }
        }

        if (++CurrentSessionIndex >= _configuration.Sessions.Count)
        {
            CurrentSessionIndex = 0;
        }
        SetSession(CurrentSessionIndex);
        return true;
    }

    public void SendCurrentSession(ACTcpClient? target = null)
    {
        var packet = new CurrentSessionUpdate
        {
            CurrentSession = CurrentSession.Configuration,
            Grid = CurrentSession.Grid,
            TrackGrip = _weatherManager.Value.CurrentWeather.TrackGrip
        };

        if (target == null)
        {
            foreach (var car in _entryCarManager.EntryCars.Where(c => c.Client is { HasSentFirstUpdate: true }))
            {
                packet.StartTime = CurrentSession.StartTimeMilliseconds - car.TimeOffset;
                car.Client?.SendPacket(packet);
            }
        }
        else
        {
            target.SendPacket(packet);
        }
    }

    private void SendSessionStart()
    {
        if (ServerTimeMilliseconds >= CurrentSession.StartTimeMilliseconds + 5000
            && ServerTimeMilliseconds - CurrentSession.LastRaceStartUpdateMilliseconds <= 1000) return;

        foreach (var car in _entryCarManager.EntryCars.Where(c => c.Client is { HasSentFirstUpdate: true }))
        {
            car.Client?.SendPacketUdp(new RaceStart()
            {
                StartTime = (int)(CurrentSession.StartTimeMilliseconds - car.TimeOffset),
                TimeOffset = (uint)(ServerTimeMilliseconds - car.TimeOffset),
                Ping = car.Ping,
            });
        }

        CurrentSession.LastRaceStartUpdateMilliseconds = ServerTimeMilliseconds;
    }

    private void SendSessionOver()
    {
        if (CurrentSession.Results != null)
            _entryCarManager.BroadcastPacket(new RaceOver
            {
                IsRace = CurrentSession.Configuration.Type == SessionType.Race,
                PickupMode = true,
                Results = CurrentSession.Results
            });

        CurrentSession.HasSentRaceOverPacket = true;
        CurrentSession.OverTimeMilliseconds = ServerTimeMilliseconds;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _timeSource.Start();
        NextSession();
        
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
