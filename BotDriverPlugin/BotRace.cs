using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using BotDriverPlugin.Core;

namespace BotDriverPlugin;

/// <summary>
/// The state every part of the bot plugin works on: the world, the track, the bots' slots, the session. Owned by
/// <see cref="BotDriverService"/>, shared with <see cref="GridDirector"/>, <see cref="FieldStrength"/> and <see cref="RaceAnnouncer"/>.
/// Everything in here is read and changed under <see cref="Lock"/>.
/// </summary>
public sealed class BotRace
{
    public BotRace(BotDriverConfiguration config, ACServerConfiguration serverConfig, EntryCarManager entryCarManager, SessionManager sessionManager)
    {
        Config = config;
        ServerConfig = serverConfig;
        EntryCarManager = entryCarManager;
        SessionManager = sessionManager;
    }

    public object Lock { get; } = new();
    public BotDriverConfiguration Config { get; }
    public ACServerConfiguration ServerConfig { get; }
    public EntryCarManager EntryCarManager { get; }
    public SessionManager SessionManager { get; }
    public Random Rng { get; } = new();

    public RaceWorld? World { get; set; }
    public TrackData? Track { get; set; }
    public List<BotSlot> Slots { get; } = [];
    public Dictionary<byte, BotSlot> SlotsBySessionId { get; } = new();
    public SessionType SessionType { get; set; }
    /// <summary>Lights out in a race.</summary>
    public bool RaceStarted { get; set; }
    /// <summary>Folders with car data (content/cars of the server and of the AC installation).</summary>
    public List<string> CarRoots { get; set; } = [];
    /// <summary>Car data by (model, ballast, restrictor).</summary>
    public Dictionary<(string, float, int), CarSpec> SpecCache { get; } = new();

    public double Now => SessionManager.ServerTimeMilliseconds / 1000.0;

    public bool IsActiveBot(byte sessionId) => SlotsBySessionId.TryGetValue(sessionId, out var s) && s.Active;

    public string T(string en, string de) => Config.ChatLanguage == "de" ? de : en;

    public static string FormatLap(float seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    /// <summary>"ks_nordschleife-nordschleife": the running track for caches and files.</summary>
    public string TrackKey => TrackGeometry.TrackMap.Key(ServerConfig.Server.Track, ServerConfig.Server.TrackConfig);
}
