using Content.Client.Administration.Managers;
using Content.Client.UserInterface.Systems.Sandbox;
using Content.Shared.Administration;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Console;

namespace Content.Client.Sandbox.MappingTransparency;

public sealed class MappingTransparencySystem : EntitySystem
{
    [Dependency] private readonly IClientAdminManager _admin = default!;
    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    public const int DefaultTransparencyPercent = 70;

    private MappingTransparencyOverlay? _mappingOverlay;

    public bool Enabled { get; private set; }

    public bool CanEnable => _admin.HasFlag(AdminFlags.Mapping);

    public override void Initialize()
    {
        base.Initialize();

        _admin.AdminStatusUpdated += OnAdminStatusUpdated;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _admin.AdminStatusUpdated -= OnAdminStatusUpdated;
        SetEnabled(false);
    }

    public bool TrySetEnabled(bool enabled)
    {
        if (enabled && !CanEnable)
            return false;

        SetEnabled(enabled);
        return true;
    }

    private void OnAdminStatusUpdated()
    {
        if (Enabled && !CanEnable)
            SetEnabled(false);

        var controller = _ui.GetUIController<SandboxUIController>();
        controller.SetMappingTransparencyVisible(CanEnable);
        controller.SetToggleMappingTransparency(Enabled);
    }

    private void SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
            return;

        Enabled = enabled;

        if (enabled)
        {
            _mappingOverlay = new MappingTransparencyOverlay();
            _overlay.AddOverlay(_mappingOverlay);
        }
        else
        {
            _mappingOverlay?.ResetTransparency();

            if (_mappingOverlay != null)
                _overlay.RemoveOverlay(_mappingOverlay);

            _mappingOverlay?.Dispose();
            _mappingOverlay = null;
        }

        var controller = _ui.GetUIController<SandboxUIController>();
        controller.SetToggleMappingTransparency(Enabled);
    }
}

public sealed class ShowMappingTransparencyCommand : LocalizedEntityCommands
{
    [Dependency] private readonly MappingTransparencySystem _mappingTransparency = default!;

    public override string Command => "showmappingtransparency";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _mappingTransparency.TrySetEnabled(!_mappingTransparency.Enabled);
    }
}
