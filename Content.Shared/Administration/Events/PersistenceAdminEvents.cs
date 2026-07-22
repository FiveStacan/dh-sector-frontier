using Robust.Shared.Serialization;

namespace Content.Shared.Administration.Events;

/// <summary>Requests the current server persistence administration state.</summary>
[Serializable, NetSerializable]
public sealed class PersistenceAdminRequestStateEvent : EntityEventArgs;

/// <summary>Applies persistence and autosave settings supplied by an administrator.</summary>
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

/// <summary>Requests an immediate persistence save.</summary>
[Serializable, NetSerializable]
public sealed class PersistenceAdminSaveNowEvent : EntityEventArgs;

/// <summary>Describes the current persistence configuration, schedule, and last operation result.</summary>
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

/// <summary>Identifies the result of the latest persistence administration operation.</summary>
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
