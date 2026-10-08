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
    }

    private string CarName(int id)
    {
        if (_slotsBySessionId.TryGetValue((byte)id, out var slot) && slot.Active) return slot.Bot.Name;
        return id < _entryCarManager.EntryCars.Length ? _entryCarManager.EntryCars[id].Client?.Name ?? $"#{id}" : $"#{id}";
    }
}
