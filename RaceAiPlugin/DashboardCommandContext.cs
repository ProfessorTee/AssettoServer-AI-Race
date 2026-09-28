using System.Text;
using AssettoServer.Commands.Contexts;
using AssettoServer.Server;

namespace RaceAiPlugin;

/// <summary>Runs server commands from the dashboard with admin rights, collecting the replies.</summary>
public sealed class DashboardCommandContext(EntryCarManager entryCarManager, IServiceProvider services)
    : BaseCommandContext(entryCarManager, services)
{
    public StringBuilder Output { get; } = new();
    public override bool IsAdministrator => true;
    public override void Reply(string message) => Output.AppendLine(message);

    public override void Broadcast(string message)
    {
        base.Broadcast(message);
        Output.AppendLine(message);
    }
}
