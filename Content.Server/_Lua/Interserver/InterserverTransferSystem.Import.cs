using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server._Lua.Interserver.Components;
using Content.Shared.Lua.CLVar;
using Content.Shared._Lua.Interserver;
using Content.Shared.Shuttles.Components;
using Robust.Shared.EntitySerialization;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Lua.Interserver;

public sealed partial class InterserverTransferSystem
{
    private InterserverTransferResponse CommitIncoming(
        InterserverPeerRecord peer,
        InterserverTransferRequest request)
    {
        var record = _inbox.Transfers.FirstOrDefault(x => x.TransferId == request.TransferId &&
            x.SourceServerId == peer.Id);
        if (record == null)
            return new InterserverTransferResponse(false, "Missing", "Transfer not found");
        if (record.Stage is InterserverJournalStage.Committed or InterserverJournalStage.Completed)
        {
            EnsureIncomingBypasses(record);
            return ResponseFor(record, true);
        }
        if (record.Stage is not (InterserverJournalStage.Uploaded or InterserverJournalStage.Committing))
            return ResponseFor(record, false, "Snapshot was not uploaded");

        if (record.Stage == InterserverJournalStage.Uploaded)
        {
            record.Stage = InterserverJournalStage.Committing;
            record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveInbox();
        }
        try
        {
            if (!TryEnsureIncomingShuttle(record, out var error))
            {
                record.Stage = InterserverJournalStage.Failed;
                record.Error = error;
                SaveInbox();
                return ResponseFor(record, false, error);
            }
            record.Stage = InterserverJournalStage.Committed;
            record.Error = string.Empty;
            record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveInbox();
            EnsureIncomingBypasses(record);
            return ResponseFor(record, true);
        }
        catch (Exception e)
        {
            record.Stage = InterserverJournalStage.Failed;
            record.Error = e.Message;
            SaveInbox();
            _log.Error("Failed to import transfer {Transfer}: {Error}", record.TransferId, e);
            return ResponseFor(record, false, "Import failed");
        }
    }

    private bool TryEnsureIncomingShuttle(InterserverTransferRecord record, out string error)
    {
        error = string.Empty;
        var oldShips = new List<EntityUid>();
        var exactEpoch = new List<(EntityUid Uid, InterserverShipComponent Identity)>();
        var epochs = new List<long>();
        var identities = AllEntityQuery<InterserverShipComponent>();
        while (identities.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId != record.ShipId)
                continue;
            epochs.Add(identity.OwnershipEpoch);
            if (identity.OwnershipEpoch < record.OwnershipEpoch)
                oldShips.Add(uid);
            else if (identity.OwnershipEpoch == record.OwnershipEpoch)
                exactEpoch.Add((uid, identity));
        }

        if (InterserverTransferProtocol.IsStaleOwnershipEpoch(record.OwnershipEpoch, epochs))
        {
            error = "A newer ownership epoch already exists";
            return false;
        }
        if (exactEpoch.Count > 0)
        {
            if (exactEpoch.Any(x => x.Identity.Primary && x.Identity.TransferId == record.TransferId))
                return true;
            error = "A ship fragment with the same identity and epoch already exists";
            return false;
        }

        // A lower epoch is an obsolete persisted copy. Fence it before importing the newer owner.
        foreach (var old in oldShips)
            QueueDel(old);

        if (!TryResolveMap(record.DestinationMapId, out var targetMap, out _))
        {
            error = "Destination map disappeared";
            return false;
        }
        var snapshotPath = new ResPath(record.SnapshotFile).ToRootedPath();
        if (!_resources.UserData.Exists(snapshotPath))
        {
            error = "Snapshot file is missing";
            return false;
        }
        byte[] bytes;
        using (var stream = _resources.UserData.OpenRead(snapshotPath))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        if (!string.Equals(Sha256Hex(bytes), record.SnapshotSha256, StringComparison.OrdinalIgnoreCase))
        {
            error = "Stored snapshot hash mismatch";
            return false;
        }

        var stagingUid = _map.CreateMap(out var stagingMap);
        _map.InitializeMap(stagingUid);
        LoadResult? result = null;
        try
        {
            using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
            var options = MapLoadOptions.Default with
            {
                MergeMap = stagingMap,
                DeserializationOptions = DeserializationOptions.Default with
                {
                    LogInvalidEntities = false,
                },
            };
            if (!_loader.TryLoadGeneric(reader, $"interserver:{record.TransferId}", out result, options))
            {
                error = "Snapshot deserialization failed";
                _map.DeleteMap(stagingMap);
                return false;
            }

            EntityUid? primary = null;
            foreach (var grid in result!.Grids)
            {
                if (TryComp<FTLComponent>(grid.Owner, out _))
                    RemComp<FTLComponent>(grid.Owner);
                if (!TryComp<InterserverShipComponent>(grid.Owner, out var identity))
                    continue;
                identity.TransferId = record.TransferId;
                identity.ShipId = record.ShipId;
                identity.OwnershipEpoch = record.OwnershipEpoch;
                if (identity.Primary)
                    primary = grid.Owner;
            }
            if (primary == null || !TryComp<ShuttleComponent>(primary.Value, out var shuttle))
            {
                error = "Snapshot has no primary shuttle grid";
                DeleteLoaded(result);
                _map.DeleteMap(stagingMap);
                return false;
            }

            var arrival = EnsureComp<InterserverArrivalComponent>(primary.Value);
            arrival.TransferId = record.TransferId;
            arrival.DestinationMapId = record.DestinationMapId;
            arrival.DestinationPosition = new Vector2(record.DestinationX, record.DestinationY);
            arrival.StagingMap = stagingMap;
            var targetMapUid = _maps.GetMapEntityId(targetMap);
            _shuttle.FTLToCoordinates(
                primary.Value,
                shuttle,
                new EntityCoordinates(targetMapUid, arrival.DestinationPosition),
                Angle.Zero,
                startupTime: 0f,
                hyperspaceTime: Math.Max(5f, _cfg.GetCVar(CLVars.InterserverArrivalTravelSeconds)));
            return true;
        }
        catch
        {
            if (result != null)
                DeleteLoaded(result);
            if (_maps.MapExists(stagingMap))
                _map.DeleteMap(stagingMap);
            throw;
        }
    }

    private void DeleteLoaded(LoadResult result)
    {
        foreach (var root in result.RootNodes)
        {
            if (Exists(root))
                QueueDel(root);
        }
    }

    private EntityUid? FindPrimaryShip(string shipId, long epoch)
    {
        var query = AllEntityQuery<InterserverShipComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.Primary && identity.ShipId == shipId && identity.OwnershipEpoch == epoch)
                return uid;
        }
        return null;
    }

    private void OnIncomingFtlCompleted(EntityUid uid, InterserverArrivalComponent component, ref FTLCompletedEvent args)
    {
        var record = _inbox.Transfers.FirstOrDefault(x => x.TransferId == component.TransferId);
        if (record != null)
        {
            record.Stage = InterserverJournalStage.Completed;
            record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveInbox();
        }
        var staging = component.StagingMap;
        RemCompDeferred<InterserverArrivalComponent>(uid);
        Timer.Spawn(TimeSpan.FromSeconds(1), () =>
        {
            if (_maps.MapExists(staging) && !_maps.GetAllGrids(staging).Any())
                _map.DeleteMap(staging);
        });
    }

    private void EnsureIncomingBypasses(InterserverTransferRecord record)
    {
        foreach (var raw in record.PassengerUserIds)
        {
            if (!Guid.TryParse(raw, out var guid))
                continue;
            var user = new NetUserId(guid);
            _pendingIncomingUsers[user] = record.TransferId;
            _connections.AddTemporaryConnectBypass(user, TimeSpan.FromMinutes(5));
        }
    }

    private void RebuildPendingIncomingUsers()
    {
        foreach (var record in _inbox.Transfers)
        {
            if (record.Stage is InterserverJournalStage.Committed or InterserverJournalStage.Completed)
                EnsureIncomingBypasses(record);
        }
    }

    private void RecoverCommittedIncomingTransfers()
    {
        foreach (var record in _inbox.Transfers.ToList())
        {
            if (record.Stage is not (InterserverJournalStage.Committed or InterserverJournalStage.Committing or
                InterserverJournalStage.Completed))
                continue;

            if (HasNewerShipEpoch(record.ShipId, record.OwnershipEpoch))
            {
                // This journal entry was superseded by a later legitimate transfer of the same stable ship.
                record.Stage = InterserverJournalStage.Completed;
                record.Error = string.Empty;
                SaveInbox();
                continue;
            }

            // A completed transfer already represented by this save generation must not resurrect after later
            // in-world destruction. Older saves still replay the retained snapshot after a crash.
            if (record.Stage == InterserverJournalStage.Completed && _ticker.LoadedPersistenceSave &&
                _ticker.PersistenceContainsInterserverTransfer(record.TransferId))
                continue;

            // If commit was durably recorded but the world save predates the import, the retained snapshot is the
            // recovery source. Committing is also replay-safe because stable identity prevents a duplicate.
            if (TryEnsureIncomingShuttle(record, out var error))
            {
                if (record.Stage == InterserverJournalStage.Committing)
                    record.Stage = InterserverJournalStage.Committed;
                record.Error = string.Empty;
                EnsureIncomingBypasses(record);
            }
            else
            {
                record.Error = error;
                if (record.Stage == InterserverJournalStage.Committing)
                    record.Stage = InterserverJournalStage.Failed;
                _log.Error("Unable to recover committed incoming transfer {Transfer}: {Error}", record.TransferId, error);
            }
            record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveInbox();
        }
    }

    private bool HasNewerShipEpoch(string shipId, long epoch)
    {
        var query = AllEntityQuery<InterserverShipComponent>();
        while (query.MoveNext(out _, out var identity))
        {
            if (identity.ShipId == shipId && identity.OwnershipEpoch > epoch)
                return true;
        }
        return false;
    }

    private void RecoverSourceOwnership()
    {
        foreach (var record in _outbox.Transfers.ToList())
        {
            if (record.Stage is InterserverJournalStage.Committed or InterserverJournalStage.Completed)
            {
                DeleteObsoleteSourceShip(record);
                if (record.Stage == InterserverJournalStage.Committed)
                {
                    record.Stage = InterserverJournalStage.Completed;
                    record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    SaveOutbox();
                }
                continue;
            }
            if (record.Stage is not (InterserverJournalStage.Committing or InterserverJournalStage.Uploaded or
                InterserverJournalStage.Charging))
                continue;
            if (record.Stage == InterserverJournalStage.Committing)
                QuarantineAmbiguousSource(record);
            if (_active.ContainsKey(record.TransferId) ||
                !TryGetPeer(record.DestinationServerId, out var peer) ||
                !_sourceRecoveryInFlight.Add(record.TransferId))
                continue;
            _ = RecoverAmbiguousSourceAsync(record, peer);
        }
    }

    private async Task RecoverAmbiguousSourceAsync(InterserverTransferRecord record, InterserverPeerRecord peer)
    {
        InterserverTransferResponse? response = null;
        try
        {
            var path = record.Stage == InterserverJournalStage.Committing
                ? "/api/interserver/commit"
                : "/api/interserver/status";
            response = await SendPeerRequest<InterserverTransferRequest, InterserverTransferResponse>(peer,
                path,
                new InterserverTransferRequest(InterserverTransferConstants.ProtocolVersion,
                    record.TransferId, record.SourceServerId), TimeSpan.FromSeconds(15));
        }
        catch
        {
            RunOnMainThread(() => _sourceRecoveryInFlight.Remove(record.TransferId));
            return;
        }
        RunOnMainThread(() =>
        {
            _sourceRecoveryInFlight.Remove(record.TransferId);
            if (InterserverTransferProtocol.IsDestinationCommitted(response))
            {
                record.Stage = InterserverJournalStage.Completed;
                record.Error = string.Empty;
                record.DestinationAddress = SanitizePublicAddress(
                    response!.PublicAddress, record.DestinationAddress);
                record.DestinationName = response.DisplayName ?? record.DestinationName;
                DeleteObsoleteSourceShip(record);
                RedirectPassengers(record);
            }
            else if (InterserverTransferProtocol.ProvesDestinationDidNotCommit(response))
            {
                record.Stage = InterserverJournalStage.Aborted;
                record.Error = "Transfer was not committed after source recovery";
                var ship = FindPrimaryShip(record.ShipId, record.OwnershipEpoch);
                if (ship != null && TryComp<InterserverShipComponent>(ship.Value, out var identity))
                    identity.TransferId = string.Empty;
            }
            else
            {
                record.Error = "Destination ownership is still ambiguous; retrying status";
            }
            record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveOutbox();
        });
    }

    private void DeleteObsoleteSourceShip(InterserverTransferRecord record)
    {
        var roots = new List<EntityUid>();
        var query = AllEntityQuery<InterserverShipComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId == record.ShipId && identity.OwnershipEpoch <= record.OwnershipEpoch)
                roots.Add(uid);
        }
        foreach (var uid in roots)
            QueueDel(uid);
    }
}
