using Robust.Shared.Serialization;

namespace Content.Shared.Administration.Events;

[Serializable, NetSerializable]
public sealed class PersistenceAdminRequestStateEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class PersistenceAdminApplySettingsEvent(
    bool usePersistence,
    bool autosaveEnabled,
    int autosaveIntervalMinutes,
    string savePath) : EntityEventArgs
{
    public readonly bool UsePersistence = usePersistence;
    public readonly bool AutosaveEnabled = autosaveEnabled;
    public readonly int AutosaveIntervalMinutes = autosaveIntervalMinutes;
    public readonly string SavePath = savePath;
}

[Serializable, NetSerializable]
public sealed class PersistenceAdminSaveNowEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class PersistenceAdminStateEvent(
    bool usePersistence,
    bool autosaveEnabled,
    int autosaveIntervalMinutes,
    string savePath,
    bool saveFileExists,
    bool roundRunning,
    int secondsUntilAutosave,
    long lastSaveUnixTime,
    PersistenceAdminFeedback feedback = PersistenceAdminFeedback.None) : EntityEventArgs
{
    public readonly bool UsePersistence = usePersistence;
    public readonly bool AutosaveEnabled = autosaveEnabled;
    public readonly int AutosaveIntervalMinutes = autosaveIntervalMinutes;
    public readonly string SavePath = savePath;
    public readonly bool SaveFileExists = saveFileExists;
    public readonly bool RoundRunning = roundRunning;
    public readonly int SecondsUntilAutosave = secondsUntilAutosave;
    public readonly long LastSaveUnixTime = lastSaveUnixTime;
    public readonly PersistenceAdminFeedback Feedback = feedback;
}

[Serializable, NetSerializable]
public enum PersistenceAdminFeedback : byte
{
    None,
    SettingsApplied,
    SaveSucceeded,
    SaveFailed,
    InvalidPath,
    InvalidInterval,
    NotInRound,
    PersistenceDisabled,
    FtlInProgress,
}
