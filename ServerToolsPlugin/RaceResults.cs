using AssettoServer.Server;
using AssettoServer.Shared.Model;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ServerToolsPlugin;

/// <summary>
/// Final classification of a race into the chat (top 10, everybody else gets his own place): the game's result screen doesn't always
/// list cars driven by the server (bots).
/// </summary>
public sealed class RaceResults : BackgroundService
{
    private readonly EntryCarManager _entryCarManager;
    private readonly ServerToolsConfiguration _config;

    public RaceResults(SessionManager sessionManager, EntryCarManager entryCarManager, ServerToolsConfiguration config)
    {
        _entryCarManager = entryCarManager;
        _config = config;
        sessionManager.SessionChanged += (_, args) =>
        {
            if (_config.AnnounceRaceResult && args.PreviousSession is { Configuration.Type: SessionType.Race, Results: not null } race)
                Announce(race);
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private string T(string en, string de) => _config.ChatLanguage == "de" ? de : en;

    private void Announce(SessionState race)
    {
        var rows = race.Results!
            .Where(r => r.Value.NumLaps > 0)
            .OrderByDescending(r => r.Value.NumLaps).ThenBy(r => r.Value.TotalTime)
            .ToList();
        if (rows.Count == 0) return;
        var lead = rows[0].Value;
        string Gap(EntryCarResult r)
        {
            if (r == lead) return "";
            uint down = lead.NumLaps - r.NumLaps;
            if (down > 0) return T($"+{down} lap{(down > 1 ? "s" : "")}", $"+{down} Rd.");
            return $"+{(r.TotalTime - lead.TotalTime) / 1000.0:F1} s";
        }
        string Total(uint ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
        string Name(EntryCarResult r) => string.IsNullOrEmpty(r.Name) ? "?" : r.Name;

        var fastest = rows.MinBy(r => r.Value.BestLap).Value;
        _entryCarManager.BroadcastChat(T($"Result: {Name(lead)} wins, {lead.NumLaps} laps in {Total(lead.TotalTime)}. Fastest lap {Name(fastest)} {Total(fastest.BestLap)}",
            $"Ergebnis: {Name(lead)} gewinnt, {lead.NumLaps} Runden in {Total(lead.TotalTime)}. Schnellste Runde {Name(fastest)} {Total(fastest.BestLap)}"));
        for (int i = 1; i < Math.Min(10, rows.Count); i++)
            _entryCarManager.BroadcastChat($"{i + 1}. {Name(rows[i].Value)} {Gap(rows[i].Value)}");
        for (int i = 10; i < rows.Count; i++)
            if (_entryCarManager.EntryCars.FirstOrDefault(c => c.SessionId == rows[i].Key)?.Client is { } client)
                client.SendChatMessage(T($"You finished {i + 1}. {Gap(rows[i].Value)}", $"Du bist {i + 1}. geworden {Gap(rows[i].Value)}"));
        Log.Information("Race result: {Result}", string.Join(", ", rows.Select((r, i) => $"{i + 1}. {Name(r.Value)} {r.Value.NumLaps} {Gap(r.Value)}")));
    }
}
