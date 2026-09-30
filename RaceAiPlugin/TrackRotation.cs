using System.Text.Json;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using Microsoft.Extensions.Hosting;
using Serilog;
using YamlDotNet.Serialization;

namespace RaceAiPlugin;

/// <summary>rotation.yml in the server folder.</summary>
public sealed class TrackRotationConfiguration
{
    public bool Enabled { get; set; }
    /// <summary>Preset folder names in presets/ ("default" = the cfg/ folder), in this order.</summary>
    public List<string> Tracks { get; set; } = [];
    /// <summary>Change the track after this many finished races (0 = only by time).</summary>
    public int RacesPerTrack { get; set; } = 1;
    /// <summary>Races per track for single tracks, e.g. trialmountain: 3 (the others use RacesPerTrack).</summary>
    public Dictionary<string, int> Races { get; set; } = new();
    /// <summary>Or after this many minutes (0 = off). Only changed between sessions, never during a race.</summary>
    public int MinutesPerTrack { get; set; }
    /// <summary>Random order instead of the list order (never the same track twice in a row).</summary>
    public bool Random { get; set; }
    /// <summary>Nobody online: change right away when it's due.</summary>
    public bool ChangeWhenEmpty { get; set; } = true;
    /// <summary>Warning in the chat this many seconds before the change.</summary>
    public int AnnounceSeconds { get; set; } = 20;
    /// <summary>
    /// Players with CSP are reconnected automatically after the change. Seconds to wait for the new server when it has never
    /// started this track before (later the measured start time is used).
    /// </summary>
    public int FirstStartSeconds { get; set; } = 60;
    /// <summary>Names for the chat and the welcome message, e.g. default: Nordschleife.</summary>
    public Dictionary<string, string> Titles { get; set; } = new();
}

/// <summary>
/// Track rotation: after a number of races (or minutes) the server restarts itself with the next preset (presets/&lt;name&gt;/ with its own
/// server_cfg.ini, entry_list.ini, extra_cfg.yml and plugin cfgs). Players with CSP are reconnected into their car automatically.
/// Changes only happen between sessions, never during a race. State in rotation.state, the running preset in current-preset
/// (read by race-ai/server-supervisor.sh, so a restart continues on the same track).
/// </summary>
public sealed class TrackRotation : BackgroundService
{
    private readonly ACServerConfiguration _serverConfig;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly RaceAiService _service;
    private readonly RaceAiConfiguration _raceConfig;
    private TrackRotationConfiguration _cfg = new();
    private readonly string _current;
    private int _racesDone;
    private DateTime _since = DateTime.UtcNow;
    private bool _changing;
    private readonly object _lock = new();

    private sealed class State
    {
        public Dictionary<string, double> StartSeconds { get; set; } = new();
        public string? SwitchTo { get; set; }
        public DateTime SwitchAt { get; set; }
        /// <summary>Races already finished on <see cref="RacesTrack"/> (kept over a normal server restart).</summary>
        public string? RacesTrack { get; set; }
        public int RacesDone { get; set; }
    }

    public TrackRotation(ACServerConfiguration serverConfig, SessionManager sessionManager, EntryCarManager entryCarManager,
        RaceAiService service, RaceAiConfiguration raceConfig, CSPServerExtraOptions extraOptions)
    {
        // the welcome message gets a line about the rotation (current and next track)
        extraOptions.WelcomeMessageSending += (_, args) =>
        {
            if (!Active) return;
            var left = RacesHere > 0 ? Math.Max(0, RacesHere - _racesDone) : 0;
            args.Builder.Append(T($"\n\nTrack rotation: now {Title(_current)}, next {Title(NextTrack())}" + (left > 0 ? $" after {left} race(s)." : "."),
                $"\n\nStrecken-Rotation: jetzt {Title(_current)}, danach {Title(NextTrack())}" + (left > 0 ? $" nach {left} Rennen." : ".")));
        };
        _serverConfig = serverConfig;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _service = service;
        _raceConfig = raceConfig;
        _current = string.IsNullOrEmpty(serverConfig.Preset) ? "default" : serverConfig.Preset;
    }

    public bool Active => _cfg.Enabled && _cfg.Tracks.Count >= 2;

    /// <summary>Races on the current track before the change.</summary>
    private int RacesHere => RacesFor(_current);
    public int RacesFor(string track) => _cfg.Races.TryGetValue(track, out var n) ? n : _cfg.RacesPerTrack;
    public string Current => _current;

    private string T(string en, string de) => _raceConfig.ChatLanguage == "de" ? de : en;

    private static State LoadState()
    {
        try { return File.Exists("rotation.state") ? JsonSerializer.Deserialize<State>(File.ReadAllText("rotation.state")) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveState(State s)
        => File.WriteAllText("rotation.state", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists("rotation.yml"))
                _cfg = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                    .Deserialize<TrackRotationConfiguration>(File.ReadAllText("rotation.yml")) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Race AI: rotation.yml not readable, no track rotation");
            return;
        }
        File.WriteAllText("current-preset", _current == "default" ? "" : _current);
        if (!Active) return;
        foreach (var t in _cfg.Tracks.Where(t => t != "default" && !Directory.Exists(Path.Join("presets", t))))
            Log.Warning("Race AI: rotation track {Track} has no folder presets/{Track}", t, t);
        var state = LoadState();
        // a normal restart (not a track change) continues the race count of this track
        if (state.SwitchTo != _current && state.RacesTrack == _current) _racesDone = state.RacesDone;
        Log.Information("Race AI: track rotation {Tracks}, now {Current}; change after {Races} race(s){Minutes}{Done}",
            string.Join(" → ", _cfg.Tracks), _current, RacesHere, _cfg.MinutesPerTrack > 0 ? $" or {_cfg.MinutesPerTrack} min" : "",
            _racesDone > 0 ? $", {_racesDone} done" : "");

        _sessionManager.SessionChanged += OnSessionChanged;

        // how long this track took to start after a change (for the reconnect of the players next time)
        if (state.SwitchTo == _current && state.SwitchAt > DateTime.MinValue)
        {
            while (!_service.Enabled && !token.IsCancellationRequested) await Task.Delay(500, token);
            state.StartSeconds[_current] = (DateTime.UtcNow - state.SwitchAt).TotalSeconds;
            state.SwitchTo = null;
            SaveState(state);
            Log.Information("Race AI: {Track} was ready {Seconds:F0} s after the track change", _current, state.StartSeconds[_current]);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token))
        {
            // nobody online and it's due by time: change now
            if (_cfg.ChangeWhenEmpty && _entryCarManager.ConnectedCars.Count == 0 && Due())
                StartChange(T("time is up", "Zeit abgelaufen"));
        }
    }

    private bool Due()
        => (RacesHere > 0 && _racesDone >= RacesHere)
           || (_cfg.MinutesPerTrack > 0 && (DateTime.UtcNow - _since).TotalMinutes >= _cfg.MinutesPerTrack);

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        if (args.PreviousSession?.Configuration.Type == SessionType.Race)
        {
            _racesDone++;
            try
            {
                var st = LoadState();
                st.RacesTrack = _current;
                st.RacesDone = _racesDone;
                SaveState(st);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Race AI: rotation state not saved");
            }
        }
        // between sessions: the next one hasn't really started yet
        if (Due()) StartChange(T("race over", "Rennen vorbei"));
    }

    /// <summary>Display name of a rotation entry (Titles in rotation.yml, else the folder name).</summary>
    public string Title(string track) => _cfg.Titles.TryGetValue(track, out var t) ? t : track;

    public string NextTrack()
    {
        var list = _cfg.Tracks;
        if (_cfg.Random)
        {
            var others = list.Where(t => t != _current).ToList();
            return others[System.Random.Shared.Next(others.Count)];
        }
        int i = list.IndexOf(_current);
        return list[(i + 1) % list.Count];
    }

    public object Info() => new
    {
        active = Active,
        current = _current,
        next = Active ? NextTrack() : null,
        titles = _cfg.Titles,
        tracks = _cfg.Tracks,
        racesDone = _racesDone,
        racesPerTrack = RacesHere,
        minutesLeft = _cfg.MinutesPerTrack > 0 ? Math.Max(0, _cfg.MinutesPerTrack - (DateTime.UtcNow - _since).TotalMinutes) : (double?)null,
        changing = _changing
    };

    /// <summary>Announce, reconnect the players (CSP) and restart the server with the next preset.</summary>
    public bool StartChange(string reason, string? to = null)
    {
        lock (_lock)
        {
            if (_changing || !Active) return false;
            _changing = true;
        }
        string next = to != null && _cfg.Tracks.Contains(to) && to != _current ? to : NextTrack();
        string preset = next == "default" ? "" : next;
        var state = LoadState();
        double start = state.StartSeconds.TryGetValue(next, out var s) ? s : _cfg.FirstStartSeconds;
        int wait = (int)Math.Clamp(start + 8, 12, 240); // reconnect a little after the new server is ready
        Log.Information("Race AI: track change to {Next} ({Reason}), players reconnect after {Wait} s", next, reason, wait);

        _ = Task.Run(async () =>
        {
            try
            {
                int announce = Math.Max(0, _cfg.AnnounceSeconds);
                if (_entryCarManager.ConnectedCars.Count > 0 && announce > 0)
                {
                    _entryCarManager.BroadcastChat(T($"Track change to {Title(next)} in {announce} s. With CSP you're reconnected automatically (about {wait} s), otherwise please rejoin.",
                        $"Streckenwechsel zu {Title(next)} in {announce} s. Mit CSP wirst du automatisch neu verbunden (etwa {wait} s), sonst bitte neu beitreten."));
                    await Task.Delay(announce * 1000);
                }
                foreach (var car in _entryCarManager.EntryCars.Where(c => c.Client != null))
                    _service.SendTrackChange(car.Client!, Title(next), wait);
                await Task.Delay(1500);
                state.SwitchTo = next;
                state.SwitchAt = DateTime.UtcNow;
                state.RacesTrack = next;
                state.RacesDone = 0;
                SaveState(state);
                File.WriteAllText("current-preset", preset);
                AssettoServer.Program.RestartServer(string.IsNullOrEmpty(preset) ? null : preset, portOverrides: new PortOverrides
                {
                    TcpPort = _serverConfig.Server.TcpPort,
                    UdpPort = _serverConfig.Server.UdpPort,
                    HttpPort = _serverConfig.Server.HttpPort
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Race AI: track change failed");
                _changing = false;
            }
        });
        return true;
    }
}
