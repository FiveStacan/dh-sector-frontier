using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Sandbox.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class ShowDeviceLinkCommand : LocalizedEntityCommands
{
    [Dependency] private readonly DeviceLinkingVisualizationSystem _deviceLinking = default!;

    public override string Command => "showdevicelink";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError(Loc.GetString("cmd-showdevicelink-denied"));
            return;
        }

        _deviceLinking.ToggleDebugView(shell.Player);
        shell.WriteLine(Loc.GetString("cmd-showdevicelink-status"));
    }
}
