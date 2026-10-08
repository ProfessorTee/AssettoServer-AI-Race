using AssettoServer.Network.Tcp;
using AssettoServer.Shared.Network.Packets.Outgoing;
using BotDriverPlugin.Core;
using Serilog;

namespace BotDriverPlugin;

/// <summary>
/// Duel: an admin turns one bot into the clone of a recorded player (his line, his speeds, his lap times).
/// Everybody can race it; every lap is compared with the recorded best lap.
/// </summary>
public sealed partial class BotDriverService
{
    private sealed record DuelBackup(string Name, CloneProfile? OldClone, float Pace, float Consistency, float Errors, float Level, float ClonePace);

    private BotSlot? _duelSlot;
    private DuelBackup? _duelBackup;

    public string? DuelInfo
    {
        get { lock (_lock) return _duelSlot?.Bot.Clone is { } c ? $"{c.PlayerName} ({FormatLap(c.BestLap)})" : null; }
    }

    /// <summary>/bots_duel player [bot name or car number] [pace %]. Null when it worked (everybody got the chat message).</summary>
    public string? StartDuel(string player, string? bot, float pacePercent = 100)
    {
        lock (_lock)
        {
            if (_world == null || _clones == null) return T("Bots are not active.", "Bots sind nicht aktiv.");
            var guid = _clones.FindGuid(player);
            if (guid == null) return T($"No recordings of '{player}' on this track (/rec list).", $"Keine Aufzeichnungen von '{player}' auf dieser Strecke (/rec list).");

            StopDuelLocked(announce: false);
            var candidates = _slots.Where(s => s.TakeoverGuid == null && !s.Benched && s.Active).ToList();
            var slot = string.IsNullOrWhiteSpace(bot)
                ? candidates.OrderByDescending(s => s.Bot.Driver.Level).FirstOrDefault()
                : candidates.FirstOrDefault(s => s.Bot.Name.Contains(bot, StringComparison.OrdinalIgnoreCase) || s.EntryCar.SessionId.ToString() == bot.Trim());
            if (slot == null) return T("No free bot for the duel (all switched off or driven by clones).", "Kein freier Bot für das Duell (alle ausgeschaltet oder Klone).");

            var profile = _clones.Get(guid, slot.EntryCar.Model, anyCar: true);
            if (profile == null) return T($"'{player}' has no clean recorded lap here.", $"'{player}' hat hier keine saubere aufgezeichnete Runde.");

            var b = slot.Bot;
            _duelBackup = new DuelBackup(b.Name, b.Clone, b.Driver.Pace, b.Driver.Consistency, b.Driver.Errors, b.Driver.Level, b.ClonePace);
            _duelSlot = slot;
            b.Clone = profile;
            b.ClonePace = Math.Clamp(pacePercent, 80, 110) / 100f;
            _field.ApplyClone(b, profile);
            b.Name = _config.NamePrefix + T($"{profile.PlayerName} (duel)", $"{profile.PlayerName} (Duell)");
            slot.EntryCar.AiName = b.Name;
            _entryCarManager.BroadcastPacket(new CarConnected { SessionId = slot.EntryCar.SessionId, Name = b.Name, Nation = slot.Nation });

            string pace = MathF.Abs(b.ClonePace - 1) > 0.001f ? T($", pace {pacePercent:F0} %", $", Tempo {pacePercent:F0} %") : "";
            string msg = T($"Duel: car #{slot.EntryCar.SessionId} now drives {profile.PlayerName}'s line - best {FormatLap(profile.BestLap)}, average {FormatLap(profile.AverageLap)} ({profile.CleanLaps} clean laps{pace}). Beat it!",
                $"Duell: Auto #{slot.EntryCar.SessionId} fährt jetzt die Linie von {profile.PlayerName} - Bestzeit {FormatLap(profile.BestLap)}, Schnitt {FormatLap(profile.AverageLap)} ({profile.CleanLaps} saubere Runden{pace}). Schlag sie!");
            _entryCarManager.BroadcastChat(msg);
            Log.Information("BotDriver: duel against {Player}'s clone in car {Car} (best {Best}, average {Avg}, pace {Pace})",
                profile.PlayerName, slot.EntryCar.SessionId, FormatLap(profile.BestLap), FormatLap(profile.AverageLap), b.ClonePace);
            return null;
        }
    }

    public string? StopDuel()
    {
        lock (_lock)
        {
            if (_duelSlot == null) return T("No duel running.", "Kein Duell aktiv.");
            StopDuelLocked(announce: true);
            return null;
        }
    }

    private void StopDuelLocked(bool announce)
    {
        if (_duelSlot is not { } slot || _duelBackup is not { } o) return;
        var b = slot.Bot;
        b.Name = o.Name;
        b.Clone = o.OldClone;
        b.ClonePace = o.ClonePace;
        b.Driver.Pace = o.Pace;
        b.Driver.Consistency = o.Consistency;
        b.Driver.Errors = o.Errors;
        b.Driver.Level = o.Level;
        if (slot.Active && slot.TakeoverGuid == null)
        {
            slot.EntryCar.AiName = b.Name;
            _entryCarManager.BroadcastPacket(new CarConnected { SessionId = slot.EntryCar.SessionId, Name = b.Name, Nation = slot.Nation });
        }
        if (announce) _entryCarManager.BroadcastChat(T("Duel ended.", "Duell beendet."));
        _duelSlot = null;
        _duelBackup = null;
    }

    /// <summary>A player finished a lap while a duel runs: compare with the recorded best lap.</summary>
    public void DuelLap(ACTcpClient client, uint ms, int cuts)
    {
        CloneProfile? c;
        lock (_lock) c = _duelSlot?.Bot.Clone;
        if (c == null || ms == 0 || IsBotCar(client.SessionId)) return;
        float diff = ms / 1000f - c.BestLap / _duelSlot!.Bot.ClonePace;
        string d = $"{(diff < 0 ? "-" : "+")}{MathF.Abs(diff):F3}s";
        string msg = cuts > 0
            ? T($"Duel: {client.Name} {FormatLap(ms / 1000f)} ({d}), but {cuts} cut(s) - doesn't count.", $"Duell: {client.Name} {FormatLap(ms / 1000f)} ({d}), aber {cuts} Cut(s) - zählt nicht.")
            : diff < 0
                ? T($"Duel: {client.Name} {FormatLap(ms / 1000f)} - {d} faster than {c.PlayerName}'s best!", $"Duell: {client.Name} {FormatLap(ms / 1000f)} - {d} schneller als die Bestzeit von {c.PlayerName}!")
                : T($"Duel: {client.Name} {FormatLap(ms / 1000f)} ({d} to {c.PlayerName}'s best)", $"Duell: {client.Name} {FormatLap(ms / 1000f)} ({d} auf die Bestzeit von {c.PlayerName})");
        _entryCarManager.BroadcastChat(msg);
    }
}
