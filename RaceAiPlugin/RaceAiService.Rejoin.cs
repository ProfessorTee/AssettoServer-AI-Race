using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using Serilog;

namespace RaceAiPlugin;

/// <summary>A player who loses the connection during a race may come back into his car for <see cref="RaceAiConfiguration.RejoinSeconds"/>.</summary>
public sealed partial class RaceAiService
{
    private sealed record Rejoin(byte SessionId, string Name, DateTime Until);
    private readonly Dictionary<ulong, Rejoin> _rejoins = new();

    /// <summary>Called once at start: tell the core about the rejoin window.</summary>
    private void SetupRejoin()
    {
        if (_config.RejoinSeconds <= 0) return;
        _serverConfig.EmptyRaceGraceMilliseconds = Math.Max(3000, _config.RejoinSeconds * 1000L + 3000);
        _serverConfig.MayJoinClosedSession = guid =>
        {
            lock (_lock) return _rejoins.TryGetValue(guid, out var r) && r.Until > DateTime.UtcNow;
        };
    }

    private void ReserveForRejoin(ACTcpClient client)
    {
        if (_config.RejoinSeconds <= 0 || _sessionType != SessionType.Race || !_raceStarted) return;
        _rejoins[client.Guid] = new Rejoin(client.SessionId, client.Name ?? "?", DateTime.UtcNow.AddSeconds(_config.RejoinSeconds));
        Log.Information("Race AI: {Player} lost the connection, his car {Car} is kept for {Seconds} s", client.Name, client.SessionId, _config.RejoinSeconds);
        _entryCarManager.BroadcastChat(T($"{client.Name} lost the connection, he can rejoin within {_config.RejoinSeconds} s.",
            $"{client.Name} hat die Verbindung verloren und kann innerhalb von {_config.RejoinSeconds} s wieder einsteigen."));
    }

    /// <summary>Slot filter: the car kept for a player (null = no opinion).</summary>
    public bool? RejoinSlotOpen(EntryCar entryCar, ulong guid)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var old in _rejoins.Where(r => r.Value.Until <= now).Select(r => r.Key).ToList()) _rejoins.Remove(old);
            if (_rejoins.TryGetValue(guid, out var mine)) return entryCar.SessionId == mine.SessionId;
            if (_rejoins.Values.Any(r => r.SessionId == entryCar.SessionId)) return false; // kept for somebody else
            return null;
        }
    }

    /// <summary>He's back: the reservation is used up.</summary>
    private void OnRejoined(ACTcpClient client)
    {
        if (_rejoins.Remove(client.Guid, out var r) && r.SessionId == client.SessionId)
        {
            Log.Information("Race AI: {Player} rejoined his car {Car}", client.Name, client.SessionId);
            _entryCarManager.BroadcastChat(T($"{client.Name} is back in the race.", $"{client.Name} ist zurück im Rennen."));
        }
    }

    /// <summary>Every tick: a reservation ran out; a bot car kept for the player gets its bot back.</summary>
    private void ExpireRejoins()
    {
        if (_rejoins.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var (guid, r) in _rejoins.Where(r => r.Value.Until <= now).ToList())
        {
            _rejoins.Remove(guid);
            Log.Information("Race AI: {Player} didn't come back in time", r.Name);
            if (_slotsBySessionId.TryGetValue(r.SessionId, out var slot) && !slot.Active && !slot.Benched && slot.EntryCar.Client == null)
            {
                TakeSlot(slot, broadcast: true);
                PlaceForCurrentSession(slot, late: true);
            }
        }
    }

    /// <summary>New session: old reservations are void.</summary>
    private void ClearRejoins() => _rejoins.Clear();
}
