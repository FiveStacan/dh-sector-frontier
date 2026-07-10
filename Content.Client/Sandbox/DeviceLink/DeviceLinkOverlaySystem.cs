using Content.Client.Administration.Managers;
using Content.Client.UserInterface.Systems.Sandbox;
using Content.Shared.Sandbox;
using Robust.Client.Console;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Random;

namespace Content.Client.Sandbox.DeviceLink;

public sealed class DeviceLinkOverlaySystem : EntitySystem
{
    public const string ToggleCommand = "showdevicelink";

    [Dependency] private readonly IClientAdminManager _admin = default!;
    [Dependency] private readonly IClientConsoleHost _console = default!;
    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    private static readonly Color[] RayColors =
    {
        Color.Red,
        Color.Orange,
        Color.Yellow,
        Color.YellowGreen,
        Color.LimeGreen,
        Color.LightBlue,
        Color.Blue,
        Color.HotPink,
        Color.BlueViolet,
    };

    private DeviceLinkDebugOverlay? _debugOverlay;

    public readonly Dictionary<EntityUid, List<EntityUid>> Rays = new();
    public readonly Dictionary<EntityUid, Color> SourceColors = new();

    public bool Enabled { get; private set; }

    public bool CanEnable => _admin.CanCommand(ToggleCommand);

    public override void Initialize()
    {
        base.Initialize();

        _admin.AdminStatusUpdated += OnAdminStatusUpdated;
        SubscribeNetworkEvent<DeviceLinkOverlayToggledEvent>(OnOverlayToggled);
        SubscribeNetworkEvent<DeviceLinkOverlayDataEvent>(OnDebugOverlayData);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _admin.AdminStatusUpdated -= OnAdminStatusUpdated;
        RemoveOverlay();
    }

    public bool TrySetEnabled(bool enabled)
    {
        if (Enabled == enabled)
            return true;

        if (enabled && !CanEnable)
            return false;

        _console.ExecuteCommand(ToggleCommand);
        return true;
    }

    private void OnAdminStatusUpdated()
    {
        var controller = _ui.GetUIController<SandboxUIController>();
        controller.SetDeviceLinkVisible(Enabled || CanEnable);
        controller.SetToggleDeviceLink(Enabled);
    }

    private void OnOverlayToggled(DeviceLinkOverlayToggledEvent args)
    {
        Enabled = args.IsEnabled;

        if (Enabled)
            AddOverlay();
        else
            RemoveOverlay();

        OnAdminStatusUpdated();
    }

    private void OnDebugOverlayData(DeviceLinkOverlayDataEvent args)
    {
        if (_debugOverlay == null)
            return;

        Rays.Clear();

        foreach (var ray in args.Rays)
        {
            var source = GetEntity(ray.Source);
            if (!source.Valid || Transform(source).MapUid == null)
                continue;

            var entities = new List<EntityUid>();
            foreach (var connection in ray.Connections)
            {
                var entity = GetEntity(connection);
                if (!entity.Valid || Transform(entity).MapUid == null)
                    continue;

                entities.Add(entity);
            }

            if (entities.Count == 0)
                continue;

            if (!SourceColors.ContainsKey(source))
                SourceColors.Add(source, _random.Pick(RayColors));

            Rays[source] = entities;
        }
    }

    private void AddOverlay()
    {
        if (_debugOverlay != null)
            return;

        _debugOverlay = new DeviceLinkDebugOverlay();
        _overlay.AddOverlay(_debugOverlay);
    }

    private void RemoveOverlay()
    {
        SourceColors.Clear();
        Rays.Clear();

        if (_debugOverlay == null)
            return;

        _overlay.RemoveOverlay(_debugOverlay);
        _debugOverlay.Dispose();
        _debugOverlay = null;
    }
}
