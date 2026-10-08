using System.Text.Json;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using AssettoServer.Network.Tcp;
using SharedPresets;
using AssettoServer.Shared.Model;
using Microsoft.Extensions.Hosting;
using Serilog;
using YamlDotNet.Serialization;

namespace ServerToolsPlugin;

/// <summary>
/// Track rotation: after a number of races (or minutes) the server restarts itself with the next track and the configured class
/// (preset "&lt;track&gt;+&lt;class&gt;": cfg/ → presets/tracks/&lt;track&gt;/ → presets/classes/&lt;class&gt;/, see PresetOverlay). Players with CSP get a banner with a countdown to rejoin.
/// Changes only happen between sessions, never during a race. State in rotation.state, the running preset in current-preset
/// (read by tools/server-supervisor.sh, so a restart continues on the same track).
/// </summary>
public sealed class TrackRotation : BackgroundService
{
    private readonly ACServerConfiguration _serverConfig;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly IReadOnlyList<IDrivenCars> _drivers;
    private readonly ServerToolsConfiguration _toolsConfig;
    private readonly ServerRestart _restart;
    private TrackRotationConfiguration _cfg = new();
    private readonly string _current;
    /// <summary>The rotation entry (Tracks) the running preset belongs to.</summary>
    private string _currentEntry;
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
        /// <summary>The change was a class change on the same track: the race count goes on.</summary>
        public bool KeepRaces { get; set; }
    }

    public TrackRotation(ACServerConfiguration serverConfig, SessionManager sessionManager, EntryCarManager entryCarManager,
        IEnumerable<IDrivenCars> drivers, ServerToolsConfiguration toolsConfig, ServerRestart restart, CSPServerExtraOptions extraOptions,
        CSPServerScriptProvider scriptProvider)
    {
        // the welcome message gets a line about the rotation (current and next track)
        extraOptions.WelcomeMessageSending += (_, args) =>
        {
            if (!Active) return;
            var left = RacesHere > 0 ? Math.Max(0, RacesHere - _racesDone) : 0;
            args.Builder.Append(T($"\n\nTrack rotation: now {Title(_currentEntry)}, next {Title(NextTrack())}" + (left > 0 ? $" after {left} race(s)." : "."),
                $"\n\nStrecken-Rotation: jetzt {Title(_currentEntry)}, danach {Title(NextTrack())}" + (left > 0 ? $" nach {left} Rennen." : ".")));
        };
        _serverConfig = serverConfig;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _drivers = drivers.ToList();
        _toolsConfig = toolsConfig;
        _restart = restart;
        TrackChangeBanner.Register(scriptProvider);
        _current = string.IsNullOrEmpty(serverConfig.Preset) ? "default" : serverConfig.Preset;
        _currentEntry = _current;
    }

    public bool Active => _cfg.Enabled && _cfg.Tracks.Count >= 2;

    /// <summary>Races on the current track before the change.</summary>
    private int RacesHere => RacesFor(_currentEntry);
    public int RacesFor(string track) => _cfg.Races.TryGetValue(track, out var n) ? n : _cfg.RacesPerTrack;
    public string Current => _current;

    private string T(string en, string de) => _toolsConfig.ChatLanguage == "de" ? de : en;

    private static State LoadState()
    {
        try { return File.Exists("rotation.state") ? JsonSerializer.Deserialize<State>(File.ReadAllText("rotation.state")) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveState(State s)
        => File.WriteAllText("rotation.state", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        // stopping (server restart, track change) cancels the waits: that's not an error
        try { await RunAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists("rotation.yml"))
                _cfg = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                    .Deserialize<TrackRotationConfiguration>(File.ReadAllText("rotation.yml")) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Track rotation: rotation.yml not readable, no track rotation");
            return;
        }
        _currentEntry = EntryOf(_current);
        var cls = ConfiguredClass;
        if (!string.IsNullOrWhiteSpace(_cfg.Class) && cls == null)
            Log.Warning("Track rotation: unknown vehicle class {Class} in rotation.yml (known: {Known})", _cfg.Class, string.Join(", ", ClassCatalog.All.Select(c => c.Key)));

        // Without tools/server-supervisor.sh (e.g. at a game server host whose panel always starts the cfg/ track):
        // continue with the track that ran last
        string? last = null;
        try { if (File.Exists("current-preset")) last = File.ReadAllText("current-preset").Trim(); } catch { }
        last = string.IsNullOrEmpty(last) ? "default" : last;
        string start = _current;
        if (Environment.GetEnvironmentVariable("RACEAI_SUPERVISED") != "1" && last != _current && TrackExists(last)
            && Active && _cfg.Tracks.Contains(EntryOf(last)))
            start = last;
        // the configured vehicle class (none configured: the class it was started with stays)
        if (cls != null) start = PresetFor(EntryOf(start));
        if (start != _current)
        {
            Log.Information("Track rotation: the server was started with {Current}, continuing with {Start}{Why}", _current, start,
                cls != null ? $" (class {cls.Label})" : " (the track that ran last)");
            File.WriteAllText("current-preset", start == "default" ? "" : start);
            RestartInto(start == "default" ? null : start);
            return;
        }
        File.WriteAllText("current-preset", _current == "default" ? "" : _current);
        if (cls != null) Log.Information("Track rotation: vehicle class {Class} ({Preset})", cls.Label, _current);
        if (!Active) return;
        foreach (var t in _cfg.Tracks.Where(t => !TrackExists(t)))
            Log.Warning("Track rotation: rotation track {Track} has no folder {Folder}", t, PresetOverlay.TrackFolder(PresetOverlay.Split(t).Track));
        var state = LoadState();
        // a normal restart or a class change (not a track change) continues the race count of this track
        if (state.SwitchTo != _current && state.RacesTrack == _currentEntry) _racesDone = state.RacesDone;
        else if (state.SwitchTo == _current && state.KeepRaces && state.RacesTrack == _currentEntry) _racesDone = state.RacesDone;
        Log.Information("Track rotation: track rotation {Tracks}, now {Current}; change after {Races} race(s){Minutes}{Done}",
            string.Join(" → ", _cfg.Tracks), _currentEntry, RacesHere, _cfg.MinutesPerTrack > 0 ? $" or {_cfg.MinutesPerTrack} min" : "",
            _racesDone > 0 ? $", {_racesDone} done" : "");

        _sessionManager.SessionChanged += OnSessionChanged;

        // how long this track took to start after a change (for the reconnect of the players next time)
        if (state.SwitchTo == _current && state.SwitchAt > DateTime.MinValue)
        {
            // ready when every plugin that drives cars has started (loading tracks, calibrating)
            while (_drivers.Any(d => !d.Ready) && !token.IsCancellationRequested) await Task.Delay(500, token);
            state.StartSeconds[_current] = (DateTime.UtcNow - state.SwitchAt).TotalSeconds;
            state.SwitchTo = null;
            SaveState(state);
            Log.Information("Track rotation: {Track} was ready {Seconds:F0} s after the track change", _current, state.StartSeconds[_current]);
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
                st.RacesTrack = _currentEntry;
                st.RacesDone = _racesDone;
                SaveState(st);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Track rotation: rotation state not saved");
            }
        }
        // between sessions: the next one hasn't really started yet
        if (!Due()) return;
        if (args.PreviousSession?.Configuration.Type == SessionType.Race && _cfg.ResultSeconds > 0)
            _ = Task.Delay(_cfg.ResultSeconds * 1000).ContinueWith(_ => { if (Due()) StartChange(T("race over", "Rennen vorbei")); });
        else
            StartChange(T("race over", "Rennen vorbei"));
    }

    /// <summary>Display name of a rotation entry ([PRESET] TRACK_TITLE of the track), with the class it runs with.</summary>
    public string Title(string entry)
    {
        var (track, _) = PresetOverlay.Split(entry);
        string t = ClassCatalog.TrackTitle(track);
        return ClassCatalog.Get(PresetOverlay.Split(PresetFor(entry)).Class) is { } c ? $"{t} ({c.Label})" : t;
    }

    public VehicleClass? ConfiguredClass => ClassCatalog.Get(_cfg.Class);

    private static bool TrackExists(string preset)
    {
        var (track, cls) = PresetOverlay.Split(preset);
        return (track is "" or "default" || File.Exists(Path.Join(PresetOverlay.TrackFolder(track), "server_cfg.ini")))
               && (cls == null || ClassCatalog.Get(cls) != null);
    }

    /// <summary>The rotation entry of a preset: itself when it's in the list, else its track.</summary>
    private string EntryOf(string preset)
    {
        if (_cfg.Tracks.Contains(preset)) return preset;
        var track = PresetOverlay.Split(preset).Track;
        return track == "" ? "default" : track;
    }

    /// <summary>The preset that runs for a rotation entry: with its own class, else the configured one, else the running one.</summary>
    private string PresetFor(string entry)
    {
        var (track, cls) = PresetOverlay.Split(entry);
        cls ??= ConfiguredClass?.Key ?? PresetOverlay.Split(_current).Class;
        string preset = PresetOverlay.Join(track == "" ? "default" : track, cls);
        return preset == "" ? "default" : preset;
    }

    public string NextTrack()
    {
        var list = _cfg.Tracks;
        if (_cfg.Random)
        {
            var others = list.Where(t => t != _currentEntry).ToList();
            return others[System.Random.Shared.Next(others.Count)];
        }
        int i = list.IndexOf(_currentEntry);
        return list[(i + 1) % list.Count];
    }

    public object Info() => new
    {
        active = Active,
        current = _currentEntry,
        preset = _current,
        cls = ConfiguredClass?.Key,
        runningClass = PresetOverlay.Split(_current).Class,
        classes = ClassCatalog.All.Select(c => new { key = c.Key, label = c.Label, description = c.Description, missing = c.MissingModels() }),
        next = Active ? NextTrack() : null,
        titles = _cfg.Tracks.ToDictionary(t => t, t => ClassCatalog.TrackTitle(PresetOverlay.Split(t).Track)),
        tracks = _cfg.Tracks,
        racesDone = _racesDone,
        racesPerTrack = RacesHere,
        minutesLeft = _cfg.MinutesPerTrack > 0 ? Math.Max(0, _cfg.MinutesPerTrack - (DateTime.UtcNow - _since).TotalMinutes) : (double?)null,
        changing = _changing
    };

    /// <summary>Restart the server in this process with a preset (null = cfg/), same ports.</summary>
    public void RestartInto(string? preset) => _restart.RestartInto(preset);

    /// <summary>Announce, reconnect the players (CSP) and restart the server with the next track.</summary>
    public bool StartChange(string reason, string? to = null)
    {
        if (!Active) return false;
        string next = to != null && _cfg.Tracks.Contains(to) && to != _currentEntry ? to : NextTrack();
        return Change(PresetFor(next), next, reason, keepRaces: false,
            T($"Track change to {Title(next)}", $"Streckenwechsel zu {Title(next)}"));
    }

    /// <summary>
    /// Vehicle class: <paramref name="key"/> into rotation.yml (Class:), <paramref name="now"/> = change to it right away (the server restarts
    /// with the class preset of this track), else from the next track change / server start on. Returns the answer for the admin.
    /// </summary>
    public string SetClass(string key, bool now)
    {
        var cls = ClassCatalog.Get(key);
        if (cls == null)
            return T($"Unknown class {key}. Classes: {string.Join(", ", ClassCatalog.All.Select(c => c.Key))}",
                $"Unbekannte Klasse {key}. Klassen: {string.Join(", ", ClassCatalog.All.Select(c => c.Key))}");
        var missing = cls.MissingModels();
        if (missing.Count > 0)
            return T($"{cls.Label}: the server has no data for {string.Join(", ", missing)} (content/cars/<model>/data.acd). Upload these car folders first.",
                $"{cls.Label}: Auf dem Server fehlen die Daten von {string.Join(", ", missing)} (content/cars/<modell>/data.acd). Erst diese Auto-Ordner hochladen.");
        try
        {
            WriteClass(cls.Key);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Track rotation: rotation.yml not written");
            return T("rotation.yml could not be written.", "rotation.yml konnte nicht geschrieben werden.");
        }
        _cfg.Class = cls.Key;
        Log.Information("Track rotation: vehicle class set to {Class} ({When})", cls.Label, now ? "now" : "next track change");

        string entry = _currentEntry;
        string preset = PresetFor(entry);
        if (preset == _current)
            return T($"Class {cls.Label} set, this track already runs with it.", $"Klasse {cls.Label} eingestellt, diese Strecke fährt schon damit.");
        if (!now)
            return T($"Class {cls.Label} set: from the next track change{(Active ? "" : " or server restart")} on ({preset}).",
                $"Klasse {cls.Label} eingestellt: ab dem nächsten {(Active ? "Streckenwechsel" : "Server-Neustart")} ({preset}).");
        return Change(preset, entry, "class " + cls.Label, keepRaces: true, T($"Class change to {cls.Label}", $"Klassenwechsel zu {cls.Label}"))
            ? T($"Class change to {cls.Label} started ({preset}).", $"Klassenwechsel zu {cls.Label} läuft ({preset}).")
            : T("A change is already running.", "Es läuft schon ein Wechsel.");
    }

    /// <summary>Class: in rotation.yml (the line is replaced or added; the file is made when there is none).</summary>
    private static void WriteClass(string key)
    {
        const string comment = "# Vehicle class for every track (folder in presets/classes/); /server_class <class> changes it. Empty = cars of cfg/entry_list.ini\n";
        if (!File.Exists("rotation.yml"))
        {
            File.WriteAllText("rotation.yml", "Enabled: false\n" + comment + $"Class: {key}\n");
            return;
        }
        string text = File.ReadAllText("rotation.yml");
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^Class:"))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^Class:[^\r\n]*", "Class: " + key);
        else
            text = (text.Length > 0 && !text.EndsWith('\n') ? text + nl : text) + comment.Replace("\n", nl) + $"Class: {key}" + nl;
        File.WriteAllText("rotation.yml", text);
    }

    /// <summary>Announce, reconnect the players (CSP) and restart the server with <paramref name="preset"/>.</summary>
    private bool Change(string preset, string entry, string reason, bool keepRaces, string what)
    {
        lock (_lock)
        {
            if (_changing) return false;
            _changing = true;
        }
        var state = LoadState();
        double start = state.StartSeconds.TryGetValue(preset, out var s) ? s : _cfg.FirstStartSeconds;
        int wait = (int)Math.Clamp(start + 8, 12, 240); // reconnect a little after the new server is ready
        string title = Title(entry);
        Log.Information("Track rotation: change to {Preset} ({Reason}), players reconnect after {Wait} s", preset, reason, wait);

        _ = Task.Run(async () =>
        {
            try
            {
                int announce = Math.Max(0, _cfg.AnnounceSeconds);
                if (_entryCarManager.ConnectedCars.Count > 0 && announce > 0)
                {
                    _entryCarManager.BroadcastChat(T($"{what} in {announce} s. The server restarts, please rejoin via Content Manager after about {wait} s.",
                        $"{what} in {announce} s. Der Server startet neu, bitte nach etwa {wait} s über Content Manager neu beitreten."));
                    await Task.Delay(announce * 1000);
                }
                foreach (var car in _entryCarManager.EntryCars.Where(c => c.Client != null))
                    TrackChangeBanner.Send(car.Client!, title, wait);
                await Task.Delay(1500);
                state.SwitchTo = preset;
                state.SwitchAt = DateTime.UtcNow;
                state.KeepRaces = keepRaces;
                if (keepRaces)
                {
                    state.RacesTrack = entry;
                    state.RacesDone = _racesDone;
                }
                else
                {
                    state.RacesTrack = entry;
                    state.RacesDone = 0;
                }
                SaveState(state);
                File.WriteAllText("current-preset", preset == "default" ? "" : preset);
                RestartInto(preset == "default" ? null : preset);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Track rotation: change failed");
                _changing = false;
            }
        });
        return true;
    }
}
