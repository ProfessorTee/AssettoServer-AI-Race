using AssettoServer.Commands;
using JetBrains.Annotations;
using Qmmands;

namespace DriverRecorderPlugin;

[UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
public class DriverRecorderCommandModule : ACModuleBase
{
    private readonly DriverRecorderService _service;

    public DriverRecorderCommandModule(DriverRecorderService service)
    {
        _service = service;
    }

    private string T(string en, string de) => _service.Language == "de" ? de : en;

    [Command("rec")]
    public void Rec(string what = "info")
    {
        if (Client == null)
        {
            Reply("Only for players in the game.");
            return;
        }

        switch (what.ToLowerInvariant())
        {
            case "on" or "an" or "ein":
                _service.SetOptIn(Client, true);
                Reply(T("Driver Recorder: recording ON. Clean laps are saved for your AI clone (a red REC mark shows it, needs CSP). /rec off stops it.",
                    "Driver Recorder: Aufzeichnung AN. Saubere Runden werden für deinen KI-Klon gespeichert (rotes REC oben links, CSP nötig). /rec off beendet sie."));
                break;
            case "off" or "aus":
                _service.SetOptIn(Client, false);
                Reply(T("Driver Recorder: recording off. Your saved laps stay (/rec delete removes them).",
                    "Driver Recorder: Aufzeichnung aus. Gespeicherte Runden bleiben (/rec delete löscht sie)."));
                break;
            case "delete" or "löschen" or "loeschen":
                int n = _service.DeleteAll(Client);
                Reply(T($"Driver Recorder: {n} laps deleted, recording off.", $"Driver Recorder: {n} Runden gelöscht, Aufzeichnung aus."));
                break;
            default:
                Reply(_service.Info(Client));
                break;
        }
    }
}
