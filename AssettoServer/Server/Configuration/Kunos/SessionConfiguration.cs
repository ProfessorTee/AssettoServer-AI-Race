using AssettoServer.Shared.Model;
using AssettoServer.Utils;
using JetBrains.Annotations;

namespace AssettoServer.Server.Configuration.Kunos;

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class SessionConfiguration : Session
{
    [IniField("NAME")] public override string? Name { get; set; } = "Default";
    [IniField("TIME")] public override int Time { get; set; } = 60;
    [IniField("LAPS")] public override int Laps { get; set; }
    [IniField("WAIT_TIME")] public uint WaitTime { get; set; }
    [IniField("IS_OPEN")] public IsOpenMode IsOpen { get; set; } = IsOpenMode.Open;
    [IniField("INFINITE")] public bool Infinite { get; set; }
    public bool IsTimedRace => Time > 0 && Laps == 0;
    /// <summary>
    /// Length for the server lists (lobby, /INFO): practice and qualifying always last TIME minutes (LAPS is ignored there),
    /// a race is either laps or seconds. Race AI patch: a LAPS= line in [PRACTICE]/[QUALIFY] made them show up as "2 s" and a
    /// timed race as "1800 laps".
    /// </summary>
    public int ListDuration => Type == SessionType.Race && !IsTimedRace ? Laps : Time * 60;
}

public enum IsOpenMode : ushort
{
    Closed = 0,
    Open = 1,
    CloseAtStart = 2
}
