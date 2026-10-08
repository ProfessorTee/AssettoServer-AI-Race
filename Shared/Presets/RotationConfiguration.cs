namespace SharedPresets;

/// <summary>rotation.yml in the server folder.</summary>
public sealed class TrackRotationConfiguration
{
    public bool Enabled { get; set; }
    /// <summary>Tracks (folders in presets/tracks/, "default" = the track of cfg/), in this order; "nordschleife+lmp1" = always with that class.</summary>
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
    /// <summary>After a race: wait this many seconds before the change (announcement included), so the players can look at the result.</summary>
    public int ResultSeconds { get; set; } = 30;
    /// <summary>
    /// Players see a countdown until they can rejoin (the game has to load the new track, so no automatic reconnect). Seconds to wait for the new server when it has never
    /// started this track before (later the measured start time is used).
    /// </summary>
    public int FirstStartSeconds { get; set; } = 60;
    /// <summary>
    /// Vehicle class (folder in presets/classes/: gt3, gte, lmp1, jdm ...): every track runs with the cars of this class.
    /// Empty = the cars of cfg/entry_list.ini. /server_class changes it.
    /// </summary>
    public string? Class { get; set; }
}
