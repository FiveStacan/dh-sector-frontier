using Content.Shared.Administration.Events;

namespace Content.Client.Administration.Systems;

public sealed class PersistenceAdminSystem : EntitySystem
{
    public event Action<PersistenceAdminStateEvent>? StateUpdated;

    public PersistenceAdminStateEvent? LastState { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<PersistenceAdminStateEvent>(OnStateUpdated);
    }

    public void RequestState()
    {
        RaiseNetworkEvent(new PersistenceAdminRequestStateEvent());
    }

    public void ApplySettings(bool usePersistence, bool autosaveEnabled, int intervalMinutes, string savePath)
    {
        RaiseNetworkEvent(new PersistenceAdminApplySettingsEvent(
            usePersistence,
            autosaveEnabled,
            intervalMinutes,
            savePath));
    }

    public void SaveNow()
    {
        RaiseNetworkEvent(new PersistenceAdminSaveNowEvent());
    }

    private void OnStateUpdated(PersistenceAdminStateEvent state)
    {
        LastState = state;
        StateUpdated?.Invoke(state);
    }
}
