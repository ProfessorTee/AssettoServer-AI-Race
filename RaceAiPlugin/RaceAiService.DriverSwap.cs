using System.Reflection;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using RaceAiPlugin.Core;
using Serilog;

namespace RaceAiPlugin;

/// <summary>
/// Driver swap between a player and his AI clone, like a driver change in an endurance race:
/// - the player comes back while his clone drives: he joins a spare car in the pits and watches, the clone comes into the pits
///   at the next pit entry and waits in the box, the player is reconnected into his car (CSP script) and drives on;
/// - /bot (or !bot): the player drives into his box, the clone takes over there, the player watches from a spare car (a break);
/// - /play (or !play): the clone comes into the pits for the player; /bot while it's on the way: the clone drives on.
/// </summary>
public sealed partial class RaceAiService
{
    private sealed class PendingBot
    {
        public required ulong Guid;
        public required DateTime At;
        public bool Triggered;
    }

    private readonly Dictionary<ulong, PendingBot> _pendingBot = new();
    /// <summary>Messages for clients that are still connecting (nothing may be sent before the handshake is done).</summary>
    private readonly List<(ACTcpClient Client, Action Send, bool Repeat)> _outbox = new();

    private void Later(ACTcpClient client, Action send, bool repeat = false) => _outbox.Add((client, send, repeat));
    private double _nextSwapInfo;
    /// <summary>How long each player's game takes to load (s, Steam ID -> smoothed), so the driver change can start early.</summary>
    private readonly Dictionary<ulong, double> _loadSeconds = new();
    private readonly Dictionary<AssettoServer.Network.Tcp.ACTcpClient, DateTime> _connectedAt = new();
    private const double DefaultLoadSeconds = 12;

    private double LoadSecondsOf(ulong guid) => _loadSeconds.TryGetValue(guid, out var s) ? s : DefaultLoadSeconds;

    /// <summary>Handshake to first position update: the time his game needed to load the track.</summary>
    private void OnSwapFirstUpdate(AssettoServer.Network.Tcp.ACTcpClient client, EventArgs args)
    {
        client.FirstUpdateSent -= OnSwapFirstUpdate;
        lock (_lock)
        {
            if (_connectedAt.Remove(client, out var at))
            {
                double secs = Math.Clamp((DateTime.UtcNow - at).TotalSeconds, 1, 120);
                _loadSeconds[client.Guid] = _loadSeconds.TryGetValue(client.Guid, out var old) ? old * 0.5 + secs * 0.5 : secs;
            }
            // driver change: the player is in, the clone leaves his car now
            var slot = _slots.FirstOrDefault(s => s.LoadingOwner == client);
            if (slot != null)
            {
                slot.LoadingOwner = null;
                EndTakeover(slot, T($"{client.Name} takes over from his clone", $"{client.Name} übernimmt wieder von seinem Klon"));
                byte c0 = slot.EntryCar.SessionId;
                Later(client, () => SendSwap(client, 0, c0, 0, 0, 0));
            }
        }
    }

    /// <summary>About how long until the clone stands in its box (s): to the pit entry at its lap pace, then the pit lane.</summary>
    private double SecondsUntilBox(RaceBot bot)
    {
        var lane = _world?.PitLane;
        if (lane == null || _track == null) return 30;
        float limit = _world!.Settings.PitSpeedLimit * 0.85f;
        if (bot.InPitLane) return bot.Pit == PitPhase.Stopped ? 0 : MathF.Max(0, bot.PitBoxS - bot.PitS) / MathF.Max(5, MathF.Min(limit, MathF.Max(bot.Speed, 8))) + 1.5;
        float s = _track.Line.WrapS((float)bot.Distance);
        float toEntry = _track.Line.WrapS(lane.EntryTrackS - s);
        float avg = bot.Clone != null ? _track.Line.Length / MathF.Max(40, bot.Clone.AverageLap) : 40;
        return toEntry / MathF.Max(15, avg) + MathF.Max(0, bot.PitBoxS) / MathF.Max(5, limit) + 4; // + braking into the lane
    }
    private bool _swapScriptAdded;

    private void AddSwapScript()
    {
        if (_swapScriptAdded) return;
        _swapScriptAdded = true;
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RaceAiPlugin.Dashboard.driverswap.lua");
        if (stream == null)
        {
            Log.Warning("Race AI: driverswap.lua not embedded, driver swaps need a manual reconnect");
            return;
        }
        _scriptProvider.AddScript(stream, "raceai_driverswap.lua");
    }

    private void SendSwap(ACTcpClient client, byte phase, byte car, byte position, int eta, byte reconnect, string model = "")
    {
        try
        {
            client.SendPacket(new RaiSwapPacket { Phase = phase, Car = car, Position = position, Eta = (ushort)Math.Clamp(eta, 0, 65535), Reconnect = reconnect, Model = model });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Race AI: swap packet not sent");
        }
    }

    /// <summary>Track rotation: banner and automatic reconnect into the own car after <paramref name="seconds"/>.</summary>
    public void SendTrackChange(ACTcpClient client, string track, int seconds)
    {
        try
        {
            // no automatic reconnect: CSP's reconnect keeps the loaded track and the game crashes when the server has another one
            client.SendPacket(new RaiSwapPacket { Phase = 6, Car = client.SessionId, Reconnect = 0, Eta = (ushort)seconds, Info = track });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Race AI: track change packet not sent");
        }
    }

    private void Tell(ACTcpClient? client, string en, string de)
    {
        if (client == null) return;
        try { client.SendChatMessage(T(en, de)); } catch { /* gone */ }
    }

    /// <summary>A free player car (not a bot, not his own) for the player to watch from, any model.</summary>
    private EntryCar? SpareCar(EntryCar own)
        => _entryCarManager.EntryCars.Where(c => c != own && c.Client == null && !_slotsBySessionId.ContainsKey(c.SessionId))
            .OrderBy(c => c.Model == own.Model ? 0 : 1).FirstOrDefault();

    private bool SpareCarFor(ulong guid, EntryCar own) => SpareCar(own) != null;

    /// <summary>
    /// The player rejoined into his own car while his clone drives it: the clone keeps driving, the CSP script moves him on
    /// into a spare car (any model) from where he can watch. False when that isn't possible (then he takes over at once).
    /// </summary>
    private bool ArriveForWatching(ACTcpClient client, BotSlot slot)
    {
        var spare = SpareCar(slot.EntryCar);
        if (spare == null || _track?.PitLane == null) return false;
        slot.Swap = SwapPhase.Away;
        byte car = slot.EntryCar.SessionId;
        string model = spare.Model;
        Later(client, () => SendSwap(client, 5, car, 0, 0, 2, model), repeat: true); // until the script has moved him
        Later(client, () => Tell(client, $"Your clone is driving your car. You're moved to a spare car ({spare.Model}) in a moment to watch it, it comes into the pits for you. (Without CSP: /play, then wait in the pits.)",
            $"Dein Klon fährt gerade dein Auto. Du wirst gleich in ein Ersatzauto ({spare.Model}) gesetzt und kannst zuschauen, er kommt für dich an die Box. (Ohne CSP: /play und in der Box warten.)"));
        Log.Information("Race AI: {Player} is back, moved to a spare {Model} to watch his clone", client.Name, spare.Model);
        return true;
    }

    /// <summary>Slot filter: may <paramref name="guid"/> take <paramref name="entryCar"/> (null = no opinion)?</summary>
    public bool? SwapSlotOpen(EntryCar entryCar, ulong guid)
    {
        lock (_lock)
        {
            var own = _slots.FirstOrDefault(s => s.TakeoverGuid == guid);
            var owner = _slotsBySessionId.TryGetValue(entryCar.SessionId, out var s0) ? s0.TakeoverGuid : null;
            if (owner != null && owner != guid) return false; // somebody else's car, driven by his clone
            if (own == null) return null;
            // his own car: always (driver change; or passing through on his way to a spare car)
            if (entryCar == own.EntryCar) return true;
            // at the driver change only his own car
            if (own.Swap == SwapPhase.Handover) return false;
            return null;
        }
    }

    /// <summary>The player joined a spare car while his clone drives his own car.</summary>
    private void OnSwapWatcherConnected(ACTcpClient client)
    {
        client.ChatMessageReceived += OnChatForSwap;
        _connectedAt[client] = DateTime.UtcNow;
        client.FirstUpdateSent += OnSwapFirstUpdate;
        var slot = _slots.FirstOrDefault(s => s.TakeoverGuid == client.Guid);
        if (slot == null || slot.EntryCar == client.EntryCar) return;
        slot.Watcher = client;
        if (slot.ReturnOnJoin) RequestSwapPit(slot);
        else slot.Swap = SwapPhase.Watching;
        Later(client, () => Tell(client, $"Your clone is driving your car. {(slot.Swap == SwapPhase.PitRequested ? "It comes into the pits at the next pit entry, then you take over again." : "/play brings it into the pits.")} Please stay in the pits with this car.",
            $"Dein Klon fährt dein Auto. {(slot.Swap == SwapPhase.PitRequested ? "Er kommt bei der nächsten Boxeneinfahrt rein, dann übernimmst du wieder." : "/play holt ihn an die Box.")} Bitte bleib mit diesem Auto in der Box, du kannst ihm zuschauen."));
        _nextSwapInfo = 0;
    }

    private void OnSwapClientDisconnected(ACTcpClient client)
    {
        client.ChatMessageReceived -= OnChatForSwap;
        client.FirstUpdateSent -= OnSwapFirstUpdate;
        _connectedAt.Remove(client);
        foreach (var loading in _slots.Where(s => s.LoadingOwner == client)) loading.LoadingOwner = null; // gave up loading
        // passing through his own car on the way to a spare car: the others must see the clone again
        var passing = _slots.FirstOrDefault(s => s.TakeoverGuid == client.Guid && s.EntryCar == client.EntryCar && s.Active);
        if (passing != null)
        {
            _entryCarManager.BroadcastPacket(new AssettoServer.Shared.Network.Packets.Outgoing.CarConnected
                { SessionId = passing.EntryCar.SessionId, Name = passing.Bot.Name, Nation = client.NationCode ?? "" });
            return;
        }
        var watching = _slots.FirstOrDefault(s => s.Watcher == client);
        if (watching != null)
        {
            watching.Watcher = null;
            if (watching.Swap != SwapPhase.Handover) watching.Swap = SwapPhase.Away;
            return; // he left the spare car (reconnecting into his own car, or gone)
        }

        // /bot: he stopped in his box and is now being moved to a spare car: the clone takes over right there
        if (_pendingBot.TryGetValue(client.Guid, out var pending) && pending.Triggered)
        {
            _pendingBot.Remove(client.Guid);
            var slot = TryTakeOver(client, fromBox: true);
            if (slot != null)
            {
                slot.ReturnOnJoin = false;
                return;
            }
        }
        _pendingBot.Remove(client.Guid);
        if (TryTakeOver(client) == null) ReserveForRejoin(client);
    }

    private void OnChatForSwap(ACTcpClient sender, ChatMessageEventArgs args)
    {
        string msg = args.ChatMessage.Message.Trim().ToLowerInvariant();
        if (msg is not ("!bot" or "!play")) return;
        string reply = msg == "!bot" ? CommandBot(sender) : CommandPlay(sender);
        Tell(sender, reply, reply);
    }

    private void RequestSwapPit(BotSlot slot)
    {
        if (_world == null) return;
        Log.Information("Race AI: clone of {Player} comes into the pits for the driver change", slot.TakeoverPlayer);
        slot.Swap = SwapPhase.PitRequested;
        if (!slot.Bot.InPitLane) _world.RequestPitStop(slot.Bot, "driver change");
    }

    /// <summary>/bot: hand the car to the clone at the next stop in the box, or let the clone drive on.</summary>
    public string CommandBot(ACTcpClient client)
    {
        lock (_lock)
        {
            var slot = _slots.FirstOrDefault(s => s.TakeoverGuid == client.Guid);
            if (slot != null)
            {
                if (slot.Swap == SwapPhase.PitRequested && !slot.Bot.InPitLane)
                {
                    slot.Bot.Pit = PitPhase.None;
                    slot.Swap = slot.Watcher != null ? SwapPhase.Watching : SwapPhase.Away;
                    slot.ReturnOnJoin = false;
                    return T("Your clone drives on. /play brings it into the pits.", "Dein Klon fährt weiter. /play holt ihn an die Box.");
                }
                return slot.Bot.InPitLane
                    ? T("Your clone is already in the pit lane, the driver change is coming.", "Dein Klon ist schon in der Boxengasse, der Fahrerwechsel kommt gleich.")
                    : T("Your clone is driving your car. /play brings it into the pits.", "Dein Klon fährt dein Auto. /play holt ihn an die Box.");
            }

            if (_sessionType != SessionType.Race || !_raceStarted)
                return T("Driver changes only during a race.", "Fahrerwechsel gibt es nur im Rennen.");
            if (_clones?.Get(client.Guid.ToString(), client.EntryCar.Model, anyCar: true) == null)
                return T("No clone of you yet: /rec on and drive a few clean laps first.", "Noch kein Klon von dir: erst /rec on und ein paar saubere Runden fahren.");
            if (_track?.PitLane == null)
                return T("This track has no pit lane for the AI.", "Diese Strecke hat keine Boxengasse für die KI.");
            if (!SpareCarFor(client.Guid, client.EntryCar))
                return T("No free car to watch from, the driver change isn't possible right now.",
                    "Kein freies Auto zum Zuschauen, der Fahrerwechsel geht gerade nicht.");
            if (_pendingBot.Remove(client.Guid))
                return T("Driver change cancelled.", "Fahrerwechsel abgebrochen.");
            _pendingBot[client.Guid] = new PendingBot { Guid = client.Guid, At = DateTime.UtcNow };
            SendSwap(client, 4, client.SessionId, 0, 0, 0);
            return T("Driver change: drive into your pit box and stop there. Your clone takes over, you watch from a spare car. /bot again cancels.",
                "Fahrerwechsel: fahr in deine Box und halte dort an. Dein Klon übernimmt, du schaust aus einem Ersatzauto zu. Nochmal /bot bricht ab.");
        }
    }

    /// <summary>/play: the clone comes into the pits so the player can take over again.</summary>
    public string CommandPlay(ACTcpClient client)
    {
        lock (_lock)
        {
            if (_pendingBot.Remove(client.Guid))
            {
                SendSwap(client, 0, client.SessionId, 0, 0, 0);
                return T("Driver change cancelled, you drive on.", "Fahrerwechsel abgebrochen, du fährst weiter.");
            }
            var slot = _slots.FirstOrDefault(s => s.TakeoverGuid == client.Guid);
            if (slot == null) return T("You're driving yourself.", "Du fährst doch selbst.");
            if (slot.Swap is SwapPhase.PitRequested or SwapPhase.Handover)
                return T("Your clone is already on its way into the pits.", "Dein Klon ist schon auf dem Weg an die Box.");
            slot.ReturnOnJoin = true;
            RequestSwapPit(slot);
            _nextSwapInfo = 0;
            return T("Your clone comes into the pits at the next pit entry, then you take over.", "Dein Klon kommt bei der nächsten Boxeneinfahrt rein, dann übernimmst du.");
        }
    }

    /// <summary>Every tick: players who typed /bot reaching their box, clones waiting in the box, info for the watchers.</summary>
    private void UpdateDriverSwaps(RaceWorld world, double now)
    {
        // messages for clients that have finished connecting
        for (int i = _outbox.Count - 1; i >= 0; i--)
        {
            var (c, send, repeat) = _outbox[i];
            if (!c.IsConnected) { _outbox.RemoveAt(i); continue; }
            if (!c.HasSentFirstUpdate) continue;
            if (repeat && now < _nextSwapInfo) continue;
            send();
            if (!repeat) _outbox.RemoveAt(i);
        }

        // /bot: stopped in the own pit box?
        foreach (var pending in _pendingBot.Values.ToList())
        {
            var car = _entryCarManager.EntryCars.FirstOrDefault(c => c.Client?.Guid == pending.Guid);
            if (car?.Client == null || _sessionType != SessionType.Race)
            {
                _pendingBot.Remove(pending.Guid);
                continue;
            }
            if (pending.Triggered) continue;
            var box = _track!.Info.PitBoxes.FirstOrDefault(p => p.Index == car.SessionId);
            bool inBox = _track.Info.PitBoxes.Count > 0 && box.Index == car.SessionId
                         && System.Numerics.Vector3.Distance(box.Position, car.Status.Position) < 8 && car.Status.Velocity.Length() < 1.5f;
            if (!inBox) continue;
            var spare = SpareCar(car);
            if (spare == null)
            {
                _pendingBot.Remove(pending.Guid);
                Tell(car.Client, "No free car to watch from right now, the driver change is cancelled.", "Gerade kein freies Auto zum Zuschauen, der Fahrerwechsel fällt aus.");
                SendSwap(car.Client, 0, car.SessionId, 0, 0, 0);
                continue;
            }
            pending.Triggered = true;
            // the CSP script reconnects him into a spare car; the clone takes over when he leaves this one
            SendSwap(car.Client, 3, car.SessionId, 0, 0, 2, spare.Model);
            Tell(car.Client, "Driver change: you're moved to a spare car in a moment, your clone takes over. (Without CSP: leave and rejoin the server.)",
                "Fahrerwechsel: du wirst gleich in ein Ersatzauto gesetzt, dein Klon übernimmt. (Ohne CSP: Server verlassen und wieder beitreten.)");
        }

        foreach (var slot in _slots.Where(s => s.TakeoverGuid != null).ToList())
        {
            var bot = slot.Bot;
            if (slot.Swap == SwapPhase.PitRequested && bot.Pit == PitPhase.None && !bot.InPitLane && bot.Phase == BotPhase.Racing)
                world.RequestPitStop(bot, "driver change"); // e.g. the pit decision reset it

            // send the player into his car early: his game needs a while to load, he should be in when the clone stops in the box
            if (slot.Swap == SwapPhase.PitRequested && slot.Watcher != null && bot.Phase == BotPhase.Racing
                && SecondsUntilBox(bot) <= LoadSecondsOf(slot.TakeoverGuid!.Value) - 1)
            {
                slot.Swap = SwapPhase.Handover;
                slot.HandoverSince = now;
                slot.EarlyReconnect = true;
                Log.Information("Race AI: driver change for {Player}: moving him into his car {Eta:F0} s before the clone reaches the box (his game loads in about {Load:F0} s)",
                    slot.TakeoverPlayer, SecondsUntilBox(bot), LoadSecondsOf(slot.TakeoverGuid!.Value));
                SendSwap(slot.Watcher, 3, slot.EntryCar.SessionId, 0, 0, 2, slot.EntryCar.Model);
                Tell(slot.Watcher, "Driver change! You're put into your car now, your clone is coming into the pits. (Without CSP: leave and rejoin the server.)",
                    "Fahrerwechsel! Du wirst jetzt in dein Auto gesetzt, dein Klon kommt gerade an die Box. (Ohne CSP: Server verlassen und wieder beitreten.)");
                _entryCarManager.BroadcastChat(T($"Driver change: {slot.TakeoverPlayer} takes over from his clone in the pits",
                    $"Fahrerwechsel: {slot.TakeoverPlayer} übernimmt in der Box von seinem Klon"));
            }

            // in the box: wait for the player
            if (slot.Swap is SwapPhase.PitRequested or SwapPhase.Handover && bot.Pit == PitPhase.Stopped)
            {
                bot.PitServiceUntil = now + 2;
                if (slot.Swap == SwapPhase.PitRequested)
                {
                    slot.Swap = SwapPhase.Handover;
                    slot.HandoverSince = now;
                    Log.Information("Race AI: driver change for {Player}: the clone waits in the box ({Watcher})", slot.TakeoverPlayer,
                        slot.Watcher != null ? "player is being moved into his car" : "player not connected");
                    if (slot.Watcher != null)
                    {
                        SendSwap(slot.Watcher, 3, slot.EntryCar.SessionId, 0, 0, 2, slot.EntryCar.Model);
                        Tell(slot.Watcher, "Driver change! You're put into your car now. (Without CSP: leave and rejoin the server.)",
                            "Fahrerwechsel! Du wirst jetzt in dein Auto gesetzt. (Ohne CSP: Server verlassen und wieder beitreten.)");
                    }
                    _entryCarManager.BroadcastChat(T($"Driver change: {slot.TakeoverPlayer} takes over from his clone in the pits",
                        $"Fahrerwechsel: {slot.TakeoverPlayer} übernimmt in der Box von seinem Klon"));
                }
                else if (now - slot.HandoverSince > 90)
                {
                    // he didn't come: the clone drives on
                    slot.Swap = slot.Watcher != null ? SwapPhase.Watching : SwapPhase.Away;
                    slot.ReturnOnJoin = false;
                    bot.PitServiceUntil = now;
                    Tell(slot.Watcher, "The driver change didn't happen, your clone drives on. /play to try again.",
                        "Der Fahrerwechsel hat nicht geklappt, dein Klon fährt weiter. /play für einen neuen Versuch.");
                    Log.Information("Race AI: driver change for {Player} timed out, the clone drives on", slot.TakeoverPlayer);
                }
            }
        }

        // banner for the watchers every few seconds
        if (now < _nextSwapInfo) return;
        _nextSwapInfo = now + 3;
        foreach (var slot in _slots.Where(s => s.Watcher != null))
        {
            byte phase = slot.Swap switch { SwapPhase.PitRequested => 2, SwapPhase.Handover => 3, _ => 1 };
            SendSwap(slot.Watcher!, phase, slot.EntryCar.SessionId, (byte)Math.Clamp(RacePosition(slot.EntryCar), 0, 255), SecondsToBox(slot.Bot), 0);
        }
    }

    /// <summary>About how long until the bot stands in its box (s).</summary>
    private int SecondsToBox(RaceBot bot)
    {
        var lane = _track?.PitLane;
        if (lane == null || _world == null) return 0;
        if (bot.InPitLane) return 20;
        float s = _track!.Line.WrapS((float)bot.Distance);
        float toEntry = _track.Line.WrapS(lane.EntryTrackS - s);
        float avg = bot.Clone != null ? _track.Line.Length / MathF.Max(60, bot.Clone.AverageLap) : 45;
        return (int)(toEntry / MathF.Max(20, avg) + 35);
    }

    /// <summary>Race position of a car (laps and distance), 1-based.</summary>
    private int RacePosition(EntryCar car)
    {
        var session = _sessionManager.CurrentSession;
        if (session.Results == null || _track == null) return 0;
        var line = _track.Line;
        float Progress(EntryCar c)
        {
            if (!session.Results.TryGetValue(c.SessionId, out var r)) return -1;
            var pos = c.Status.Position;
            float s = line.WrapS(line.Project(pos).S - _track.StartLineS);
            return r.NumLaps + s / line.Length;
        }
        float mine = Progress(car);
        return 1 + _entryCarManager.EntryCars.Count(c => c != car && (c.Client != null || _slotsBySessionId.ContainsKey(c.SessionId)) && Progress(c) > mine);
    }
}
