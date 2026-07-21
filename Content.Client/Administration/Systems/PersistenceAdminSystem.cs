using Content.Shared.Administration.Events;

namespace Content.Client.Administration.Systems;

/// <summary>
/// Exchanges persistence administration requests and state updates with the server.
/// </summary>
public sealed class PersistenceAdminSystem : EntitySystem
{
    /// <summary>Raised whenever the server publishes the current persistence state.</summary>
    public event Action<PersistenceAdminStateEvent>? StateUpdated;

    /// <summary>The most recently received persistence state, if one has been received.</summary>
    public PersistenceAdminStateEvent? LastState { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<PersistenceAdminStateEvent>(OnStateUpdated);
    }

    /// <summary>Requests the current persistence state from the server.</summary>
    public void RequestState()
    {
        RaiseNetworkEvent(new PersistenceAdminRequestStateEvent());
    }

    /// <summary>Applies persistence and autosave settings on the server.</summary>
    /// <param name="usePersistence">Whether persistence is enabled.</param>
    /// <param name="autosaveEnabled">Whether periodic saves are enabled.</param>
    /// <param name="intervalMinutes">The autosave interval in minutes.</param>
    /// <param name="savePath">The user-data path of the persistence map.</param>
    public void ApplySettings(bool usePersistence, bool autosaveEnabled, int intervalMinutes, string savePath)
    {
        RaiseNetworkEvent(new PersistenceAdminApplySettingsEvent(
            usePersistence,
            autosaveEnabled,
            intervalMinutes,
            savePath));
    }

    /// <summary>Requests an immediate persistence save.</summary>
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
