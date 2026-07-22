using System;
using System.Linq;
using Content.Server._Lua.Sectors;
using Content.Server.Maps;
using Content.Server.Persistence.Components;
using Content.Server._Lua.Interserver;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Shared._NF.Roles.Components;
using Content.Shared.Administration;
using Content.Shared.Administration.Events;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.GameTicking;

// Lua/Frontier persistence (session-save): additive save/restore of secondary Lua sector maps (and the
// shuttles + stations on them) alongside the existing single-DefaultMap persistence. The main sector
// (DefaultMap) is saved/restored by the unchanged GameMapManager path; this file only adds the extra
// sector maps so player shuttles parked in those sectors survive a server restart too.
public sealed partial class GameTicker
{
    private readonly Dictionary<NetUserId, EntityUid> _persistentPlayerCharacters = new();
    private long _lastPersistenceSaveUnixTime;
    private readonly HashSet<string> _persistenceInterserverTransfers = new(StringComparer.OrdinalIgnoreCase);
    private bool _persistencePrimaryLoadFailed;
    private bool _loadedPersistenceFromBackup;

    /// <summary>
    /// True only when the current DefaultMap was actually restored from a persistence save, rather than when
    /// persistence merely happens to be enabled. Consumers use this to avoid duplicating round-start content.
    /// </summary>
    public bool LoadedPersistenceSave { get; private set; }
    public long LastPersistenceSaveUnixTime => _lastPersistenceSaveUnixTime;

    public bool PersistenceContainsInterserverTransfer(string transferId) =>
        _persistenceInterserverTransfers.Contains(transferId);

    private void ResetPersistenceSaveMetadata()
    {
        _lastPersistenceSaveUnixTime = 0;
        _persistenceInterserverTransfers.Clear();
    }

    private void RestorePersistenceSaveTimestamp()
    {
        if (!LoadedPersistenceSave || !_map.MapExists(DefaultMap))
            return;
        var mapUid = _map.GetMap(DefaultMap);
        if (TryComp<PersistenceSaveMetadataComponent>(mapUid, out var metadata))
        {
            _lastPersistenceSaveUnixTime = Math.Max(0, metadata.SaveUnixTime);
            _persistenceInterserverTransfers.UnionWith(metadata.CompletedInterserverTransfers);
        }
    }

    private static ResPath SectorSavePath(string savePath) => new(savePath + ".sectors");
    private static ResPath PersistenceBackupPath(ResPath path) =>
        new(path.ToString() + GameMapManager.PersistenceBackupSuffix);
    private static ResPath PersistenceTempPath(ResPath path) => new(path.ToString() + ".tmp");

    private void DeletePersistenceFileIfExists(ResPath path)
    {
        path = path.ToRootedPath();
        if (_resourceManager.UserData.Exists(path))
            _resourceManager.UserData.Delete(path);
    }

    /// <summary>
    /// Publishes a fully-written temporary save while retaining the last known save as a rollback file.
    /// </summary>
    private void PromotePersistenceFile(ResPath temporary, ResPath target, bool preserveBackup)
    {
        temporary = temporary.ToRootedPath();
        target = target.ToRootedPath();
        var backup = PersistenceBackupPath(target);

        if (!_resourceManager.UserData.Exists(temporary))
            throw new InvalidOperationException($"Persistence temporary file {temporary} was not written.");

        if (_resourceManager.UserData.Exists(target))
        {
            if (preserveBackup)
            {
                // The primary file already failed to load, so never rotate it over the known rollback file.
                _resourceManager.UserData.Delete(target);
            }
            else
            {
                DeletePersistenceFileIfExists(backup);
                _resourceManager.UserData.Rename(target, backup);
            }
        }

        try
        {
            _resourceManager.UserData.Rename(temporary, target);
        }
        catch
        {
            // If publishing failed after rotating the primary, put the previous save back in place. If that also
            // fails, GameMapManager will still discover the .previous file on the next startup.
            if (!_resourceManager.UserData.Exists(target) && _resourceManager.UserData.Exists(backup))
            {
                try
                {
                    _resourceManager.UserData.Rename(backup, target);
                }
                catch (Exception rollbackError)
                {
                    Log.Error($"[Persistence] Failed to restore {backup} after save publication failed:\n{rollbackError}");
                }
            }

            throw;
        }
    }

    private void InitializePersistence()
    {
        SubscribeLocalEvent<BeforeSerializationEvent>(OnBeforePersistenceSave);
        SubscribeLocalEvent<PersistentPlayerCharacterComponent, ComponentStartup>(OnPersistentCharacterStartup);
        SubscribeLocalEvent<PersistentPlayerCharacterComponent, ComponentShutdown>(OnPersistentCharacterShutdown);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _persistentPlayerCharacters.Clear());
        SubscribeNetworkEvent<PersistenceAdminRequestStateEvent>(OnPersistenceAdminRequestState);
        SubscribeNetworkEvent<PersistenceAdminApplySettingsEvent>(OnPersistenceAdminApplySettings);
        SubscribeNetworkEvent<PersistenceAdminSaveNowEvent>(OnPersistenceAdminSaveNow);
    }

    private bool CanManagePersistence(ICommonSession session)
    {
        return _adminManager.HasAdminFlag(session, AdminFlags.Host);
    }

    private void OnPersistenceAdminRequestState(
        PersistenceAdminRequestStateEvent message,
        EntitySessionEventArgs args)
    {
        if (!CanManagePersistence(args.SenderSession))
            return;

        SendPersistenceAdminState(args.SenderSession);
    }

    private void OnPersistenceAdminApplySettings(
        PersistenceAdminApplySettingsEvent message,
        EntitySessionEventArgs args)
    {
        if (!CanManagePersistence(args.SenderSession))
            return;

        if (message.AutosaveIntervalMinutes is < 1 or > 10080)
        {
            SendPersistenceAdminState(args.SenderSession, PersistenceAdminFeedback.InvalidInterval);
            return;
        }

        var oldUsePersistence = _cfg.GetCVar(CCVars.UsePersistence);
        var oldAutosaveEnabled = _cfg.GetCVar(CCVars.AutoSaveEnabled);
        var oldInterval = _cfg.GetCVar(CCVars.AutoSaveInterval);
        var oldSavePath = _cfg.GetCVar(CCVars.GameMap);
        var requestedSavePath = message.SavePath.Trim();
        var savePathValid = IsValidPersistenceSavePath(requestedSavePath);
        if (message.UsePersistence && !savePathValid)
        {
            SendPersistenceAdminState(args.SenderSession, PersistenceAdminFeedback.InvalidPath);
            return;
        }

        // GameMapManager interprets a non-prototype game.map as a persistence path only while persistence
        // is enabled, so enable first and apply the requested final state last to keep its callback coherent.
        if (savePathValid && requestedSavePath != oldSavePath)
        {
            _cfg.SetCVar(CCVars.UsePersistence, true);
            _cfg.SetCVar(CCVars.GameMap, requestedSavePath);
        }

        _cfg.SetCVar(CCVars.AutoSaveEnabled, message.AutosaveEnabled);
        _cfg.SetCVar(CCVars.AutoSaveInterval, message.AutosaveIntervalMinutes);
        _cfg.SetCVar(CCVars.UsePersistence, message.UsePersistence);

        _cfg.SaveToFile();

        var effectiveSavePath = savePathValid ? requestedSavePath : oldSavePath;

        _adminLogger.Add(LogType.AdminCommands, LogImpact.Extreme,
            $"Host {args.SenderSession.Name} ({args.SenderSession.UserId}) changed persistence settings: " +
            $"enabled {oldUsePersistence}->{message.UsePersistence}, autosave {oldAutosaveEnabled}->{message.AutosaveEnabled}, " +
            $"interval {oldInterval}->{message.AutosaveIntervalMinutes}, path '{oldSavePath}'->'{effectiveSavePath}'.");

        SendPersistenceAdminState(args.SenderSession, PersistenceAdminFeedback.SettingsApplied);
    }

    private void OnPersistenceAdminSaveNow(
        PersistenceAdminSaveNowEvent message,
        EntitySessionEventArgs args)
    {
        if (!CanManagePersistence(args.SenderSession))
            return;

        if (!_cfg.GetCVar(CCVars.UsePersistence))
        {
            SendPersistenceAdminState(args.SenderSession, PersistenceAdminFeedback.PersistenceDisabled);
            return;
        }

        if (RunLevel != GameRunLevel.InRound || !_map.MapExists(DefaultMap))
        {
            SendPersistenceAdminState(args.SenderSession, PersistenceAdminFeedback.NotInRound);
            return;
        }

        if (EntityManager.System<ShuttleSystem>().IsAnyFtlInProgress())
        {
            SendPersistenceAdminState(args.SenderSession, PersistenceAdminFeedback.FtlInProgress);
            return;
        }

        var saved = false;
        try
        {
            saved = SaveMaps();
            if (saved)
            {
                _timeToNextSave = TimeSpan.Zero;
                _warnings = 3;
            }
        }
        catch (Exception e)
        {
            Log.Error($"[Persistence] Host-triggered save failed:\n{e}");
        }

        _adminLogger.Add(LogType.AdminCommands, saved ? LogImpact.High : LogImpact.Extreme,
            $"Host {args.SenderSession.Name} ({args.SenderSession.UserId}) triggered a persistence save: {saved}.");
        SendPersistenceAdminState(args.SenderSession, saved
            ? PersistenceAdminFeedback.SaveSucceeded
            : PersistenceAdminFeedback.SaveFailed);
    }

    private bool IsValidPersistenceSavePath(string savePath)
    {
        if (string.IsNullOrWhiteSpace(savePath) ||
            savePath.Length > 256 ||
            !ResPath.IsValidPath(savePath) ||
            savePath.Any(char.IsControl) ||
            savePath.Contains(':') ||
            savePath.EndsWith('/') ||
            !new ResPath(savePath).IsRooted ||
            savePath.Split('/').Any(part => part == "..") ||
            savePath is "." or "/")
        {
            return false;
        }

        // A map prototype ID takes precedence over persistence loading in GameMapManager. Accepting one here
        // would appear to save successfully but load a fresh prototype after restart.
        return !_prototypeManager.TryIndex<GameMapPrototype>(savePath, out _);
    }

    private void SendPersistenceAdminState(
        ICommonSession session,
        PersistenceAdminFeedback feedback = PersistenceAdminFeedback.None)
    {
        var enabled = _cfg.GetCVar(CCVars.UsePersistence);
        var autosaveEnabled = _cfg.GetCVar(CCVars.AutoSaveEnabled);
        var interval = _cfg.GetCVar(CCVars.AutoSaveInterval);
        var savePath = _cfg.GetCVar(CCVars.GameMap);
        var roundRunning = RunLevel == GameRunLevel.InRound;
        var saveFileExists = IsValidPersistenceSavePath(savePath) &&
                             _resourceManager.UserData.Exists(new ResPath(savePath));

        var secondsUntilAutosave = -1;
        if (enabled && autosaveEnabled && roundRunning)
        {
            var remaining = TimeSpan.FromMinutes(interval) - _timeToNextSave;
            secondsUntilAutosave = (int) Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        }

        RaiseNetworkEvent(new PersistenceAdminStateEvent(
                enabled,
                autosaveEnabled,
                interval,
                savePath,
                saveFileExists,
                roundRunning,
                secondsUntilAutosave,
                _lastPersistenceSaveUnixTime,
                feedback),
            session.Channel);
    }

    /// <summary>
    /// Rebuilds the account-to-character links immediately before a map is serialized. Old markers are
    /// removed first so a respawned player cannot retain ownership of both their old and current bodies.
    /// For a returnable observer, OwnedEntity is still the original body while CurrentEntity is the ghost,
    /// which preserves the same return-to-body relationship across the restart.
    /// </summary>
    private void OnBeforePersistenceSave(BeforeSerializationEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.UsePersistence))
            return;

        var markersToRemove = new List<EntityUid>();
        var markerQuery = AllEntityQuery<PersistentPlayerCharacterComponent, TransformComponent>();
        while (markerQuery.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapID != MapId.Nullspace && ev.MapIds.Contains(xform.MapID))
                markersToRemove.Add(uid);
        }

        foreach (var uid in markersToRemove)
            RemComp<PersistentPlayerCharacterComponent>(uid);

        var marked = 0;
        var mindQuery = AllEntityQuery<MindComponent>();
        while (mindQuery.MoveNext(out _, out var mind))
        {
            if (mind.UserId is not { } userId ||
                mind.OwnedEntity is not { Valid: true } character ||
                TerminatingOrDeleted(character) ||
                HasComp<GhostComponent>(character) ||
                !TryComp<TransformComponent>(character, out var xform) ||
                xform.MapID == MapId.Nullspace ||
                !ev.MapIds.Contains(xform.MapID))
            {
                continue;
            }

            var marker = EnsureComp<PersistentPlayerCharacterComponent>(character);
            marker.UserId = userId;
            _persistentPlayerCharacters[userId] = character;
            marked++;
        }

        Log.Debug($"[Persistence] Marked {marked} player character(s) on {ev.MapIds.Count} saved map(s).");

        SuppressTransientStationAiEyeTargets(ev.MapIds);
    }

    /// <summary>
    /// Station AI brains look through a runtime-only StationAiHolo entity. The holo has DoNotMap and therefore
    /// cannot be part of a persistence save, but Eye.Target would otherwise serialize a dangling entity reference.
    /// Clear it only for the synchronous serialization pass and restore normal runtime state on the next tick.
    /// </summary>
    private void SuppressTransientStationAiEyeTargets(IReadOnlySet<MapId> savedMaps)
    {
        var suppressed = new List<(EntityUid Brain, EntityUid Target)>();
        var eyeSystem = EntityManager.System<SharedEyeSystem>();
        var query = AllEntityQuery<StationAiHeldComponent, EyeComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var eye, out var xform))
        {
            if (eye.Target is not { } target ||
                xform.MapID == MapId.Nullspace ||
                !savedMaps.Contains(xform.MapID))
            {
                continue;
            }

            suppressed.Add((uid, target));
            eyeSystem.SetTarget(uid, null, eye);
        }

        if (suppressed.Count == 0)
            return;

        Timer.Spawn(0, () =>
        {
            foreach (var (brain, target) in suppressed)
            {
                if (TerminatingOrDeleted(brain) ||
                    TerminatingOrDeleted(target) ||
                    !TryComp<EyeComponent>(brain, out var eye) ||
                    eye.Target != null)
                {
                    continue;
                }

                eyeSystem.SetTarget(brain, target, eye);
            }
        });
    }

    private void OnPersistentCharacterStartup(
        EntityUid uid,
        PersistentPlayerCharacterComponent component,
        ComponentStartup args)
    {
        if (component.UserId == default)
            return;

        if (_persistentPlayerCharacters.TryGetValue(component.UserId, out var existing) &&
            existing != uid &&
            !TerminatingOrDeleted(existing))
        {
            Log.Error($"[Persistence] Multiple saved characters claim player {component.UserId}: " +
                      $"{ToPrettyString(existing)} and {ToPrettyString(uid)}. Keeping the first one.");
            return;
        }

        _persistentPlayerCharacters[component.UserId] = uid;
    }

    private void OnPersistentCharacterShutdown(
        EntityUid uid,
        PersistentPlayerCharacterComponent component,
        ComponentShutdown args)
    {
        if (_persistentPlayerCharacters.TryGetValue(component.UserId, out var indexed) && indexed == uid)
            _persistentPlayerCharacters.Remove(component.UserId);
    }

    /// <summary>
    /// Rebinds a player's saved mind to the character carrying that player's persistence marker. If the
    /// save has no usable mind, creates a replacement. Returns false when there is no saved character,
    /// leaving normal spawn flow intact.
    /// </summary>
    private bool TryRestorePersistentCharacter(ICommonSession player, out EntityUid character)
    {
        character = default;

        var interserver = EntityManager.SystemOrNull<InterserverTransferSystem>();
        if (!_cfg.GetCVar(CCVars.UsePersistence) &&
            (interserver == null || !interserver.HasPendingIncomingCharacter(player.UserId)))
            return false;

        if (!_persistentPlayerCharacters.TryGetValue(player.UserId, out var indexed) ||
            TerminatingOrDeleted(indexed) ||
            !TryComp<PersistentPlayerCharacterComponent>(indexed, out var marker) ||
            marker.UserId != player.UserId ||
            HasComp<GhostComponent>(indexed))
        {
            _persistentPlayerCharacters.Remove(player.UserId);
            return false;
        }

        character = indexed;

        // Null-space minds referenced by a character normally ride along with a map save. A player who connected
        // while the server was still in the pre-round lobby already has ContentPlayerData by the time that mind is
        // loaded, however ComponentStartup cannot populate that player data retroactively. Re-set the same user ID
        // to rebuild the runtime session/PVS links and retain all serialized roles and objectives.
        EntityUid? existingMindId = null;
        MindComponent? existingMind = null;
        var mindQuery = AllEntityQuery<MindComponent>();
        while (mindQuery.MoveNext(out var candidateMindId, out var candidateMind))
        {
            if (candidateMind.UserId != player.UserId)
                continue;

            existingMindId = candidateMindId;
            existingMind = candidateMind;
            break;
        }

        if (existingMindId != null && existingMind != null)
        {
            if (existingMind.OwnedEntity != character)
            {
                Log.Error($"[Persistence] Saved mind {ToPrettyString(existingMindId)} for {player.UserId} owns " +
                          $"{ToPrettyString(existingMind.OwnedEntity)}, not marked character {ToPrettyString(character)}.");
                character = default;
                return false;
            }

            _mind.SetUserId(existingMindId.Value, null, existingMind);
            _mind.SetUserId(existingMindId.Value, player.UserId, existingMind);
            LogPersistentCharacterRestore(player, character);
            return true;
        }

        // If the referenced null-space mind was not indexed with a user, adopt it only when its original owner
        // matches. Never steal a body if a valid runtime mind belonging to somebody else already controls it.
        if (TryComp<MindContainerComponent>(character, out var container) &&
            container.Mind is { } savedMind &&
            TryComp<MindComponent>(savedMind, out var savedMindComponent))
        {
            if (savedMindComponent.UserId == null && savedMindComponent.OriginalOwnerUserId == player.UserId)
            {
                _mind.SetUserId(savedMind, player.UserId, savedMindComponent);
                LogPersistentCharacterRestore(player, character);
                return true;
            }

            Log.Error($"[Persistence] Refusing to restore {player.UserId} into already controlled character " +
                      $"{ToPrettyString(character)} (mind {ToPrettyString(savedMind)}).");
            character = default;
            return false;
        }

        var mind = _mind.CreateMind(player.UserId, Name(character));
        _mind.TransferTo(mind, character);

        // This fallback save had no usable mind. Restore its ordinary job role from character-side tracking so
        // job-aware UI and admin tooling continue to identify the player even though other mind data was unavailable.
        if (TryComp<JobTrackingComponent>(character, out var job) && job.Job != null)
            _roles.MindAddJobRole(mind, silent: true, jobPrototype: job.Job);

        LogPersistentCharacterRestore(player, character);
        return true;
    }

    private void LogPersistentCharacterRestore(ICommonSession player, EntityUid character)
    {
        EntityManager.SystemOrNull<InterserverTransferSystem>()?.MarkIncomingCharacterAttached(player.UserId);
        _adminLogger.Add(LogType.LateJoin, LogImpact.Medium,
            $"Player {player.Name} reattached to persistent character {ToPrettyString(character)}.");
        Log.Info($"[Persistence] Restored player {player.UserId} to {ToPrettyString(character)}.");
    }

    /// <summary>
    /// Saves every persistent Lua sector map (those carrying <see cref="PersistentSectorMapComponent"/>)
    /// plus the nullspace station meta-entities whose grids live on them, into one FileCategory.Save file
    /// next to the main world save. Called from SaveMaps after the DefaultMap save. Shuttles parked/docked
    /// on a sector map ride along automatically. The main DefaultMap is not marked, so it is excluded here.
    /// </summary>
    private bool SaveSectorMaps(string savePath, out bool wroteFile)
    {
        wroteFile = false;
        var sectorMapUids = new HashSet<EntityUid>();
        var sectorMapIds = new HashSet<MapId>();
        foreach (var mapId in _map.GetAllMapIds())
        {
            if (!_map.TryGetMap(mapId, out var mapUid))
                continue;
            if (!HasComp<PersistentSectorMapComponent>(mapUid.Value))
                continue;
            sectorMapUids.Add(mapUid.Value);
            sectorMapIds.Add(mapId);
        }

        if (sectorMapUids.Count == 0)
            return true;

        wroteFile = true;

        // Include the (nullspace) station entities for those maps so their stations restore already linked
        // to their grids — StationDataComponent.Grids is a serialized DataField, so the cross-references are
        // remapped on load. This is what keeps POI/sector stations working after a restore.
        var saveSet = new HashSet<EntityUid>(sectorMapUids);
        var stationQuery = AllEntityQuery<StationDataComponent>();
        while (stationQuery.MoveNext(out var stationUid, out var data))
        {
            foreach (var grid in data.Grids)
            {
                if (TryComp<TransformComponent>(grid, out var gridXform) && sectorMapIds.Contains(gridXform.MapID))
                {
                    saveSet.Add(stationUid);
                    break;
                }
            }
        }

        var pausedMaps = new List<MapId>();
        var saved = false;
        try
        {
            foreach (var mapId in sectorMapIds)
            {
                _map.SetPaused(mapId, true);
                pausedMaps.Add(mapId);
            }

            saved = _loader.TrySaveGeneric(
                saveSet,
                SectorSavePath(savePath),
                out var category,
                new SerializationOptions { Category = FileCategory.Save });

            _adminLogger.Add(LogType.EventRan, LogImpact.High,
                $"SECTOR SAVE STATUS: {saved} CATEGORY: {category} MAPS: {sectorMapUids.Count} STATIONS: {saveSet.Count - sectorMapUids.Count}");
        }
        finally
        {
            foreach (var mapId in pausedMaps)
                _map.SetPaused(mapId, false);
        }

        return saved;
    }

    /// <summary>
    /// Restores the persistent Lua sector maps saved by <see cref="SaveSectorMaps"/>. Runs at the end of
    /// LoadMaps (after DefaultMap is set) only when game.usepersistence is on and the sibling save exists.
    /// Loads every saved sector map (with its grids, shuttles and station entities), re-registers each into
    /// <see cref="SectorSystem"/> so Starmap/FTL-by-id resolve, and initializes the map. EnsureSector's
    /// ContainsKey guard then skips re-creating these sectors at RoundStarting, so nothing is duplicated.
    /// Any failure is logged and the round simply continues with freshly-generated sectors.
    /// </summary>
    private void RestoreSectors()
    {
        if (!_cfg.GetCVar(CCVars.UsePersistence))
            return;

        var savePath = _cfg.GetCVar(CCVars.GameMap);
        if (string.IsNullOrWhiteSpace(savePath))
            return;

        var primaryPath = SectorSavePath(savePath).ToRootedPath();
        var backupPath = PersistenceBackupPath(primaryPath);
        var path = _loadedPersistenceFromBackup ? backupPath : primaryPath;
        if (!_resourceManager.UserData.Exists(path))
        {
            if (path == primaryPath && _resourceManager.UserData.Exists(backupPath))
                path = backupPath;
            else
                return;
        }

        var options = new MapLoadOptions
        {
            ExpectedCategory = FileCategory.Save,
            DeserializationOptions = new DeserializationOptions
            {
                InitializeMaps = false,
                PauseMaps = false,
            },
        };

        HashSet<Entity<MapComponent>>? maps = null;
        var loaded = TryLoadSectorSave(path, options, out maps);
        if (!loaded && path == primaryPath && _resourceManager.UserData.Exists(backupPath))
        {
            Log.Warning($"[Persistence] Sector save {primaryPath} failed; trying rollback {backupPath}.");
            path = backupPath;
            loaded = TryLoadSectorSave(path, options, out maps);
        }

        if (!loaded || maps == null)
        {
            Log.Error("[Persistence] Sector restore failed; sectors will be generated fresh.");
            return;
        }

        try
        {
            var sectors = EntityManager.System<SectorSystem>();
            var restored = 0;
            foreach (var map in maps)
            {
                if (!TryComp<PersistentSectorMapComponent>(map.Owner, out var marker) || string.IsNullOrEmpty(marker.ConfigId))
                {
                    // Dark Haven - persistence: a loaded sector map with no usable marker is unmanaged (no system
                    // tracks it), so delete it instead of leaving it live to be re-saved as a growing duplicate.
                    Log.Warning($"[Persistence] Sector restore: dropping unmarked loaded map {map.Comp.MapId}.");
                    _map.DeleteMap(map.Comp.MapId);
                    continue;
                }

                if (!sectors.RestoreSectorInstance(marker.ConfigId, map.Comp.MapId, map.Owner))
                {
                    // Dark Haven - persistence: config unknown/renamed or the sector is already registered -> this
                    // loaded map is an orphan/duplicate. Delete it so it isn't left untracked and re-saved.
                    Log.Warning($"[Persistence] Sector restore: dropping unclaimable sector '{marker.ConfigId}' (map {map.Comp.MapId}).");
                    _map.DeleteMap(map.Comp.MapId);
                    continue;
                }

                if (_map.IsInitialized(map.Comp.MapId))
                    _map.SetPaused(map.Comp.MapId, false);
                else
                    _map.InitializeMap(map.Comp.MapId);
                restored++;
            }

            _adminLogger.Add(LogType.EventRan, LogImpact.High,
                $"SECTOR RESTORE: loaded {maps.Count} map(s), restored {restored} sector(s).");
        }
        catch (Exception e)
        {
            Log.Error($"[Persistence] Sector restore threw; sectors will be generated fresh:\n{e}");
        }
    }

    private bool TryLoadSectorSave(
        ResPath path,
        MapLoadOptions options,
        out HashSet<Entity<MapComponent>>? maps)
    {
        try
        {
            return _loader.TryLoadGeneric(path, out maps, out _, options);
        }
        catch (Exception error)
        {
            maps = null;
            Log.Error($"[Persistence] Sector restore could not load {path}:\n{error}");
            return false;
        }
    }
}
