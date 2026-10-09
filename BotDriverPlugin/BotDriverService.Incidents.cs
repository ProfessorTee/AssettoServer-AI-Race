using AssettoServer.Server.Extensions;
using BotDriverPlugin.Core;

namespace BotDriverPlugin;

/// <summary>
/// Incident log for the admin page (/api/bots/incidents): contacts, yellow flags, damage and pit decisions, tyre choices, with
/// time, session, lap and place. To tune the bots after real races, not only in the simulator.
/// </summary>
public sealed partial class BotDriverService
{
    public sealed record IncidentEntry(string Time, string Session, int Lap, int Car, string Name, string Kind, string Text, string? Other, string? Where);

    private const int MaxIncidents = 400;
    private readonly List<IncidentEntry> _incidents = [];
    private readonly List<DrivenCarEvent> _spectator = [];
    private long _spectatorNumber;

    /// <summary>Spins, crashes and stopped bots for the live page (IDrivenCars).</summary>
    public IReadOnlyList<DrivenCarEvent> SpectatorEvents
    {
        get { lock (_lock) return _spectator.ToList(); }
    }

    /// <summary>Newest first.</summary>
    public List<IncidentEntry> Incidents()
    {
        lock (_lock) return Enumerable.Reverse(_incidents).ToList();
    }

    private void OnWorldIncident(int carId, int otherId, string kind, string text) => AddIncident(carId, otherId, kind, text);

    private void OnYellowIncident(YellowFlagEvent e) => AddIncident(e.CarId, -1, e.Kind, e.Kind switch
    {
        "spin" => "spun (yellow flag)",
        "crash" => "crash (yellow flag)",
        _ => "stopped on the track (yellow flag)"
    });

    /// <summary>Under the race lock (the world's events come from the tick and the session setup).</summary>
    private void AddIncident(int carId, int otherId, string kind, string text)
    {
        var world = _world;
        var track = _track;
        float? s = null;
        int lap = 0;
        if (world?.Bots.FirstOrDefault(b => b.Id == carId) is { } bot)
        {
            s = (float)bot.Distance;
            lap = bot.LapsCompleted + 1;
        }
        else if (world?.Externals.FirstOrDefault(e => e.Id == carId) is { Valid: true } ext)
        {
            s = ext.S;
            lap = ext.Laps + 1;
        }
        string? where = s is { } d && track != null ? track.Info.SectionAt(track.Line.WrapS(d - track.StartLineS) / track.Line.Length) : null;
        _incidents.Add(new IncidentEntry(DateTime.Now.ToString("HH:mm:ss"), _sessionManager.CurrentSession.Configuration.Name ?? "", lap, carId,
            CarName(carId), kind, text, otherId >= 0 ? CarName(otherId) : null, where));
        if (_incidents.Count > MaxIncidents) _incidents.RemoveRange(0, _incidents.Count - MaxIncidents);
        SaveIncident(_incidents[^1]);

        // for spectators: what you would see on TV (contacts with players come from the players' own collision messages)
        string at = where != null ? $" ({where})" : "";
        string? show = kind switch
        {
            "spin" => $"{CarName(carId)} dreht sich{at}",
            "stopped" => $"{CarName(carId)} steht auf der Strecke{at}",
            "crash" when otherId >= 0 && _slotsBySessionId.ContainsKey((byte)otherId) => $"Unfall: {CarName(carId)} und {CarName(otherId)}{at}",
            _ => null
        };
        if (show != null)
        {
            _spectator.Add(new DrivenCarEvent(++_spectatorNumber, show));
            if (_spectator.Count > 30) _spectator.RemoveAt(0);
        }
    }

    // ---- the log on disk: one CSV per day, so it survives restarts and track changes

    private readonly object _fileLock = new();

    private string? IncidentFile(DateTime day)
        => string.IsNullOrWhiteSpace(_config.IncidentLogFolder) ? null : Path.Join(_config.IncidentLogFolder, $"{day:yyyy-MM-dd}.csv");

    private static string Csv(string? s) => (s ?? "").Replace(';', ',').Replace('\n', ' ');

    private void SaveIncident(IncidentEntry e)
    {
        if (!DebugOn || IncidentFile(DateTime.Now) is not { } path) return; // the file only while debugging, the admin page always
        string row = string.Join(";", e.Time, Csv(e.Session), e.Lap, e.Car, Csv(e.Name), e.Kind, Csv(e.Text), Csv(e.Other), Csv(e.Where), Csv(TrackKey()));
        // off the race lock and the bots' tick
        _ = Task.Run(() =>
        {
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    bool header = !File.Exists(path);
                    File.AppendAllText(path, (header ? "time;session;lap;car;name;kind;text;other;where;track\n" : "") + row + "\n");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "BotDriver: incident log not writable");
            }
        });
    }

    /// <summary>Today's incidents back into the list after a restart (newest 400).</summary>
    private void LoadIncidents()
    {
        try
        {
            if (IncidentFile(DateTime.Now) is not { } path || !File.Exists(path)) return;
            foreach (var line in File.ReadLines(path).Skip(1).TakeLast(MaxIncidents))
            {
                var p = line.Split(';');
                if (p.Length < 9 || !int.TryParse(p[2], out int lap) || !int.TryParse(p[3], out int car)) continue;
                _incidents.Add(new IncidentEntry(p[0], p[1], lap, car, p[4], p[5], p[6], p[7] == "" ? null : p[7], p[8] == "" ? null : p[8]));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "BotDriver: incident log not readable");
        }
    }

    private string CarName(int id)
    {
        if (_slotsBySessionId.TryGetValue((byte)id, out var slot) && slot.Active) return slot.Bot.Name;
        return id < _entryCarManager.EntryCars.Length ? _entryCarManager.EntryCars[id].Client?.Name ?? $"#{id}" : $"#{id}";
    }
}
