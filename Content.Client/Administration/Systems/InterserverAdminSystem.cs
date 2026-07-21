using Content.Shared.Administration.Events;

namespace Content.Client.Administration.Systems;

public sealed class InterserverAdminSystem : EntitySystem
{
    public event Action<InterserverAdminStateEvent>? StateUpdated;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<InterserverAdminStateEvent>(ev => StateUpdated?.Invoke(ev));
    }

    public void RequestState() => RaiseNetworkEvent(new InterserverAdminRequestStateEvent());
    public void ApplyLocal(bool enabled, string id, string name, string address) =>
        RaiseNetworkEvent(new InterserverAdminApplyLocalEvent(enabled, id, name, address));
    public void UpsertPeer(string id, string name, string api, string address, string secret, bool approved, string maps) =>
        RaiseNetworkEvent(new InterserverAdminUpsertPeerEvent(id, name, api, address, secret, approved, maps));
    public void RemovePeer(string id) => RaiseNetworkEvent(new InterserverAdminRemovePeerEvent(id));
    public void TestPeer(string id) => RaiseNetworkEvent(new InterserverAdminTestPeerEvent(id));
}
