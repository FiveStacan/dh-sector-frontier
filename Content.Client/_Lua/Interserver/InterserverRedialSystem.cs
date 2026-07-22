using Content.Shared._Lua.Interserver;
using Robust.Client;
using Robust.Client.UserInterface;

namespace Content.Client._Lua.Interserver;

public sealed class InterserverRedialSystem : EntitySystem
{
    [Dependency] private readonly IGameController _gameController = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    private InterserverRedialWindow? _window;
    private string _address = string.Empty;
    private string _serverName = string.Empty;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<InterserverRedirectEvent>(OnRedirect);
    }

    private void OnRedirect(InterserverRedirectEvent ev)
    {
        _address = ev.Address;
        _serverName = ev.ServerName;
        // Some custom launchers disable redial by silently ignoring it. Keep a manual path visible regardless of
        // whether the engine reports an error.
        ShowManualFallback();
        TryRedial();
    }

    private void TryRedial()
    {
        try
        {
            _gameController.Redial(_address,
                Loc.GetString("interserver-redial-text", ("server", _serverName)));
        }
        catch
        {
            ShowManualFallback();
        }
    }

    private void ShowManualFallback()
    {
        if (_window is not { Disposed: false })
        {
            _window = _ui.CreateWindow<InterserverRedialWindow>();
            _window.Retry += TryRedial;
        }
        _window.SetTransfer(_address, _serverName);
        _window.OpenCentered();
    }
}
