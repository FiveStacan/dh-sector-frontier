using Content.Shared.DeviceLinking;
using Content.Shared.Sandbox;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Sandbox;

public sealed class DeviceLinkingVisualizationSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private readonly HashSet<ICommonSession> _debugSessions = new();
    private TimeSpan _nextOverlayUpdate = TimeSpan.Zero;

    public override void Initialize()
    {
        base.Initialize();

        _player.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _debugSessions.Clear();
        _player.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_debugSessions.Count == 0 || _timing.CurTime < _nextOverlayUpdate)
            return;

        _nextOverlayUpdate = _timing.CurTime + UpdateInterval;
        UpdateOverlay();
    }

    public void ToggleDebugView(ICommonSession session)
    {
        var enabled = _debugSessions.Add(session);
        if (!enabled)
            _debugSessions.Remove(session);

        RaiseNetworkEvent(new DeviceLinkOverlayToggledEvent(enabled), session.Channel);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus != SessionStatus.Disconnected || e.OldStatus != SessionStatus.InGame)
            return;

        _debugSessions.Remove(e.Session);
    }

    private void UpdateOverlay()
    {
        var rays = new List<DebugEntityConnectionData>();
        var query = AllEntityQuery<DeviceLinkSourceComponent>();

        while (query.MoveNext(out var uid, out var source))
        {
            if (source.LinkedPorts.Count == 0)
                continue;

            var connections = new List<NetEntity>();
            foreach (var output in source.LinkedPorts)
            {
                connections.Add(GetNetEntity(output.Key));
            }

            rays.Add(new DebugEntityConnectionData(GetNetEntity(uid), connections));
        }

        foreach (var session in _debugSessions)
        {
            RaiseNetworkEvent(new DeviceLinkOverlayDataEvent(rays), session);
        }
    }
}
