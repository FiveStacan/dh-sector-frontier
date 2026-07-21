using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Persistence.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server._Lua.Interserver.Components;
using Content.Shared.Lua.CLVar;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Timing;
using Content.Shared._Lua.Interserver;
using Robust.Shared.EntitySerialization;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Lua.Interserver;

public sealed partial class InterserverTransferSystem
{
    public void StartTransfer(EntityUid console, EntityUid actor, string serverId, string destinationMapId)
    {
        var primary = Transform(console).GridUid;
        if (primary == null)
            return;
        _consoleErrors.Remove(primary.Value);

        if (!_cfg.GetCVar(CLVars.InterserverEnabled))
        {
            SetConsoleError(primary.Value, "Межсерверные перелёты отключены");
            return;
        }
        if (!TryGetPeer(serverId, out var peer) || !IsValidIdentifier(destinationMapId))
        {
            SetConsoleError(primary.Value, "Сервер или карта назначения не разрешены");
            return;
        }
        if (!_catalog.TryGetValue(peer.Id, out var catalog) || !catalog.Online ||
            !catalog.Maps.Any(x => string.Equals(x.Id, destinationMapId, StringComparison.OrdinalIgnoreCase)))
        {
            SetConsoleError(primary.Value, "Сервер назначения недоступен или больше не публикует эту карту");
            return;
        }

        if (Transform(actor).GridUid != primary || !TryComp<ShuttleComponent>(primary, out var shuttle))
        {
            SetConsoleError(primary.Value, "Управлять дальним БСС можно только с шаттла");
            return;
        }
        if (!TryComp<PilotComponent>(actor, out var pilot) || pilot.Console != console)
        {
            SetConsoleError(primary.Value, "Для запуска необходимо занять место пилота у этой консоли");
            return;
        }
        if (!_shuttle.CanFTL(primary.Value, out var reason))
        {
            SetConsoleError(primary.Value, reason ?? "БСС недоступен");
            return;
        }
        if (_active.Values.Any(x => x.Grids.Contains(primary.Value)))
        {
            SetConsoleError(primary.Value, "Для этого шаттла уже выполняется межсерверный перелёт");
            return;
        }

        var grids = new HashSet<EntityUid>();
        if (!_shuttle.GetAllFTLShuttles(primary.Value, grids, out var groupReason))
        {
            SetConsoleError(primary.Value, groupReason ?? "Не удалось определить сцепленную группу шаттлов");
            return;
        }

        var ship = EnsureComp<InterserverShipComponent>(primary.Value);
        if (!Guid.TryParse(ship.ShipId, out _))
            ship.ShipId = Guid.NewGuid().ToString("D");
        ship.OwnershipEpoch = Math.Max(0, ship.OwnershipEpoch) + 1;
        ship.TransferId = Guid.NewGuid().ToString("D");
        ship.Primary = true;

        foreach (var grid in grids)
        {
            var identity = EnsureComp<InterserverShipComponent>(grid);
            identity.ShipId = ship.ShipId;
            identity.TransferId = ship.TransferId;
            identity.OwnershipEpoch = ship.OwnershipEpoch;
            identity.Primary = grid == primary.Value;
        }

        var size = GetTransferBounds(grids).Size;
        var clearance = Math.Max(size.X, size.Y);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var record = new InterserverTransferRecord
        {
            TransferId = ship.TransferId,
            ShipId = ship.ShipId,
            OwnershipEpoch = ship.OwnershipEpoch,
            SourceServerId = _cfg.GetCVar(CLVars.InterserverServerId),
            DestinationServerId = peer.Id,
            DestinationMapId = destinationMapId,
            DestinationAddress = catalog.PublicAddress,
            DestinationName = catalog.Name,
            Stage = InterserverJournalStage.Reserved,
            // Destination FTL normalizes rotation. A square reservation remains safe at every source angle.
            Width = Math.Max(1, clearance),
            Height = Math.Max(1, clearance),
            UpdatedUnixTime = now,
            ExpiresUnixTime = now + Math.Max(30, _cfg.GetCVar(CLVars.InterserverReservationSeconds)),
        };
        _outbox.Transfers.RemoveAll(x => x.TransferId == record.TransferId);
        _outbox.Transfers.Add(record);
        SaveOutbox();

        var active = new ActiveInterserverTransfer
        {
            Record = record,
            Peer = peer,
            PrimaryGrid = primary.Value,
            Grids = grids,
            OperationInFlight = true,
        };
        _active[record.TransferId] = active;
        _shuttleConsole.RefreshShuttleConsoles(primary.Value);
        _ = ReserveOutgoingAsync(active);
    }

    private Box2 GetTransferBounds(HashSet<EntityUid> grids)
    {
        var first = true;
        var min = Vector2.Zero;
        var max = Vector2.Zero;
        foreach (var uid in grids)
        {
            if (!TryComp<MapGridComponent>(uid, out var grid))
                continue;
            var box = _transform.GetWorldMatrix(uid).TransformBox(grid.LocalAABB);
            if (first)
            {
                min = box.BottomLeft;
                max = box.TopRight;
                first = false;
            }
            else
            {
                min = Vector2.Min(min, box.BottomLeft);
                max = Vector2.Max(max, box.TopRight);
            }
        }
        return first ? Box2.CenteredAround(Vector2.Zero, Vector2.One) : new Box2(min, max);
    }

    private async Task ReserveOutgoingAsync(ActiveInterserverTransfer transfer)
    {
        InterserverTransferResponse? response = null;
        Exception? failure = null;
        try
        {
            var record = transfer.Record;
            response = await SendPeerRequest<InterserverReserveRequest, InterserverTransferResponse>(
                transfer.Peer,
                "/api/interserver/reserve",
                new InterserverReserveRequest(
                    InterserverTransferConstants.ProtocolVersion,
                    record.TransferId,
                    record.ShipId,
                    record.OwnershipEpoch,
                    record.SourceServerId,
                    record.DestinationMapId,
                    record.Width,
                    record.Height),
                TimeSpan.FromSeconds(15));
        }
        catch (Exception e)
        {
            failure = e;
        }

        RunOnMainThread(() =>
        {
            transfer.OperationInFlight = false;
            if (!StillOwnsSourceGrid(transfer))
            {
                FailOutgoing(transfer, "Исходный шаттл исчез во время резервирования");
                _ = AbortRemoteAsync(transfer);
                return;
            }
            if (response is not { Success: true })
            {
                FailOutgoing(transfer, response?.Error ?? failure?.Message ?? "Сервер назначения не ответил");
                _ = AbortRemoteAsync(transfer);
                return;
            }

            transfer.Record.DestinationX = response.DestinationX;
            transfer.Record.DestinationY = response.DestinationY;
            transfer.Record.Stage = InterserverJournalStage.Charging;
            transfer.Record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveOutbox();
            if (!TryComp<ShuttleComponent>(transfer.PrimaryGrid, out var shuttle))
            {
                FailOutgoing(transfer, "Шаттл исчез до начала FTL");
                _ = AbortRemoteAsync(transfer);
                return;
            }

            var currentGroup = new HashSet<EntityUid>();
            if (!_shuttle.GetAllFTLShuttles(transfer.PrimaryGrid, currentGroup, out var groupReason) ||
                !currentGroup.SetEquals(transfer.Grids))
            {
                FailOutgoing(transfer, groupReason ?? "Состав сцепленной группы изменился; запустите перелёт повторно");
                _ = AbortRemoteAsync(transfer);
                return;
            }
            var currentSize = GetTransferBounds(currentGroup).Size;
            if (Math.Max(currentSize.X, currentSize.Y) > transfer.Record.Width + 0.1f)
            {
                FailOutgoing(transfer, "Габариты сцепленной группы изменились; запустите перелёт повторно");
                _ = AbortRemoteAsync(transfer);
                return;
            }
            if (!_shuttle.CanFTL(transfer.PrimaryGrid, out var ftlReason))
            {
                FailOutgoing(transfer, ftlReason ?? "БСС перестал быть доступен до начала перелёта");
                _ = AbortRemoteAsync(transfer);
                return;
            }

            var current = Transform(transfer.PrimaryGrid).Coordinates;
            var rotation = _transform.GetWorldRotation(transfer.PrimaryGrid);
            _shuttle.FTLToCoordinates(transfer.PrimaryGrid, shuttle, current, rotation,
                startupTime: 5f, hyperspaceTime: 120f);
            if (!HasComp<FTLComponent>(transfer.PrimaryGrid))
            {
                FailOutgoing(transfer, "Не удалось запустить исходный шаттл в FTL");
                _ = AbortRemoteAsync(transfer);
                return;
            }
            _shuttleConsole.RefreshShuttleConsoles(transfer.PrimaryGrid);
        });
    }

    private void OnSourceFtlStarted(EntityUid uid, InterserverShipComponent component, ref FTLStartedEvent args)
    {
        if (!component.Primary || !_active.TryGetValue(component.TransferId, out var transfer) ||
            transfer.PrimaryGrid != uid || transfer.OperationInFlight ||
            transfer.Record.Stage != InterserverJournalStage.Charging)
            return;

        byte[] bytes;
        List<string> passengerIds;
        try
        {
            if (!TryCreateSnapshot(transfer, out bytes, out passengerIds, out var error))
            {
                FailOutgoing(transfer, error);
                _ = AbortRemoteAsync(transfer);
                return;
            }

            var localPath = SnapshotPath(transfer.Record.TransferId).ToRootedPath();
            _resources.UserData.CreateDir(localPath.Directory);
            using var stream = _resources.UserData.OpenWrite(localPath);
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception e)
        {
            FailOutgoing(transfer, $"Не удалось записать снимок шаттла: {e.Message}");
            _ = AbortRemoteAsync(transfer);
            return;
        }

        transfer.Record.PassengerUserIds = passengerIds;
        transfer.Record.SnapshotSha256 = Sha256Hex(bytes);
        transfer.Record.SnapshotFile = SnapshotPath(transfer.Record.TransferId).ToString();
        transfer.Record.Stage = InterserverJournalStage.Uploaded;
        transfer.Record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveOutbox();
        transfer.OperationInFlight = true;
        _shuttleConsole.RefreshShuttleConsoles(uid);
        _ = UploadOutgoingAsync(transfer, bytes);
    }

    private bool TryCreateSnapshot(
        ActiveInterserverTransfer transfer,
        out byte[] bytes,
        out List<string> passengerIds,
        out string error)
    {
        bytes = Array.Empty<byte>();
        passengerIds = new List<string>();
        error = string.Empty;
        var roots = new HashSet<EntityUid>(transfer.Grids);
        var gridSet = transfer.Grids;
        var minds = AllEntityQuery<MindComponent>();
        while (minds.MoveNext(out _, out var mind))
        {
            if (mind.UserId is not { } userId || mind.OwnedEntity is not { } character ||
                !TryComp(character, out TransformComponent? xform) || xform.GridUid is not { } grid ||
                !gridSet.Contains(grid))
                continue;

            EnsureComp<PersistentPlayerCharacterComponent>(character).UserId = userId;
            passengerIds.Add(userId.UserId.ToString("D"));
        }

        var options = SerializationOptions.Default with
        {
            Category = FileCategory.Save,
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
            ErrorOnOrphan = false,
        };
        using var writer = new StringWriter();
        if (!_loader.TrySaveGeneric(roots, writer, out _, options))
        {
            error = "Не удалось сериализовать шаттл";
            return false;
        }

        bytes = Encoding.UTF8.GetBytes(writer.ToString());
        var limit = Math.Clamp(_cfg.GetCVar(CLVars.InterserverMaxSnapshotMiB), 1, 512) * 1024L * 1024L;
        if (bytes.LongLength > limit)
        {
            error = $"Снимок шаттла превышает лимит ({bytes.LongLength / 1024 / 1024} МиБ)";
            bytes = Array.Empty<byte>();
            return false;
        }
        passengerIds = passengerIds.Distinct().ToList();
        if (passengerIds.Count > 256)
        {
            error = "На сцепленной группе слишком много пассажиров (максимум 256)";
            bytes = Array.Empty<byte>();
            passengerIds.Clear();
            return false;
        }
        return true;
    }

    private async Task UploadOutgoingAsync(ActiveInterserverTransfer transfer, byte[] snapshot)
    {
        InterserverTransferResponse? response = null;
        Exception? failure = null;
        try
        {
            var record = transfer.Record;
            response = await SendPeerRequest<InterserverUploadRequest, InterserverTransferResponse>(
                transfer.Peer,
                "/api/interserver/upload",
                new InterserverUploadRequest(
                    InterserverTransferConstants.ProtocolVersion,
                    record.TransferId,
                    record.ShipId,
                    record.OwnershipEpoch,
                    record.SourceServerId,
                    record.SnapshotSha256,
                    Convert.ToBase64String(snapshot),
                    record.PassengerUserIds),
                TimeSpan.FromSeconds(90));
        }
        catch (Exception e)
        {
            failure = e;
        }

        RunOnMainThread(() =>
        {
            transfer.OperationInFlight = false;
            if (!StillOwnsSourceGrid(transfer))
            {
                FailOutgoing(transfer, "Исходный шаттл исчез во время загрузки снимка");
                _ = AbortRemoteAsync(transfer);
                return;
            }
            if (response is not { Success: true })
            {
                FailOutgoing(transfer, response?.Error ?? failure?.Message ?? "Ошибка загрузки шаттла");
                _ = AbortRemoteAsync(transfer);
                return;
            }
            transfer.Record.Stage = InterserverJournalStage.Committing;
            transfer.Record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveOutbox();
            transfer.OperationInFlight = true;
            _ = CommitOutgoingAsync(transfer);
        });
    }

    private async Task CommitOutgoingAsync(ActiveInterserverTransfer transfer)
    {
        InterserverTransferResponse? response = null;
        Exception? failure = null;
        try
        {
            response = await SendPeerRequest<InterserverTransferRequest, InterserverTransferResponse>(
                transfer.Peer,
                "/api/interserver/commit",
                new InterserverTransferRequest(
                    InterserverTransferConstants.ProtocolVersion,
                    transfer.Record.TransferId,
                    transfer.Record.SourceServerId),
                TimeSpan.FromSeconds(90));
        }
        catch (Exception e)
        {
            failure = e;
        }

        RunOnMainThread(() =>
        {
            transfer.OperationInFlight = false;
            if (InterserverTransferProtocol.IsDestinationCommitted(response))
            {
                CompleteOutgoing(transfer, response!);
                return;
            }

            // A timed-out commit is ambiguous and must never be treated as a failure. Repeat the idempotent commit;
            // only a proven non-commit is safe to abort and retain locally.
            transfer.Record.Error = failure?.Message ?? response?.Error ?? "Проверка результата commit";
            SaveOutbox();
            HoldAmbiguousSourceInFtl(transfer.PrimaryGrid);
            transfer.OperationInFlight = true;
            _ = ResolveAmbiguousCommitAsync(transfer);
        });
    }

    private async Task ResolveAmbiguousCommitAsync(ActiveInterserverTransfer transfer)
    {
        InterserverTransferResponse? response = null;
        try
        {
            response = await SendPeerRequest<InterserverTransferRequest, InterserverTransferResponse>(
                transfer.Peer,
                "/api/interserver/commit",
                new InterserverTransferRequest(InterserverTransferConstants.ProtocolVersion,
                    transfer.Record.TransferId, transfer.Record.SourceServerId),
                TimeSpan.FromSeconds(15));
        }
        catch
        {
            // Preserve Committing. Recovery will repeat the idempotent commit after restart; deleting either copy here
            // would make a network partition destructive.
        }

        RunOnMainThread(() =>
        {
            transfer.OperationInFlight = false;
            if (InterserverTransferProtocol.IsDestinationCommitted(response))
            {
                CompleteOutgoing(transfer, response!);
                return;
            }
            if (InterserverTransferProtocol.ProvesDestinationDidNotCommit(response))
            {
                FailOutgoing(transfer, "Назначение не подтвердило commit; шаттл возвращается");
                _ = AbortRemoteAsync(transfer);
                return;
            }

            transfer.Record.Error = "Связь потеряна после commit; владение будет проверено повторно";
            transfer.NextRetry = _timing.RealTime + TimeSpan.FromSeconds(10);
            HoldAmbiguousSourceInFtl(transfer.PrimaryGrid);
            SaveOutbox();
            if (Exists(transfer.PrimaryGrid))
                _shuttleConsole.RefreshShuttleConsoles(transfer.PrimaryGrid);
        });
    }

    private async Task AbortRemoteAsync(ActiveInterserverTransfer transfer)
    {
        try
        {
            await SendPeerRequest<InterserverTransferRequest, InterserverTransferResponse>(
                transfer.Peer, "/api/interserver/abort",
                new InterserverTransferRequest(InterserverTransferConstants.ProtocolVersion,
                    transfer.Record.TransferId, transfer.Record.SourceServerId), TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Expiring reservation is safe; abort is only an eager cleanup.
        }
    }

    private void CompleteOutgoing(ActiveInterserverTransfer transfer, InterserverTransferResponse response)
    {
        transfer.Record.Stage = InterserverJournalStage.Committed;
        transfer.Record.Error = string.Empty;
        transfer.Record.DestinationAddress = SanitizePublicAddress(
            response.PublicAddress, transfer.Record.DestinationAddress);
        transfer.Record.DestinationName = response.DisplayName ?? transfer.Record.DestinationName;
        transfer.Record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveOutbox();

        RedirectPassengers(transfer.Record);

        if (Exists(transfer.PrimaryGrid))
            _shuttleConsole.RefreshShuttleConsoles(transfer.PrimaryGrid);
        Timer.Spawn(TimeSpan.FromSeconds(3), () =>
        {
            foreach (var grid in transfer.Grids)
            {
                if (Exists(grid))
                    QueueDel(grid);
            }
            transfer.Record.Stage = InterserverJournalStage.Completed;
            transfer.Record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveOutbox();
            _active.Remove(transfer.Record.TransferId);
        });
    }

    private bool StillOwnsSourceGrid(ActiveInterserverTransfer transfer)
    {
        return Exists(transfer.PrimaryGrid) &&
               TryComp<InterserverShipComponent>(transfer.PrimaryGrid, out var identity) &&
               identity.TransferId == transfer.Record.TransferId && identity.OwnershipEpoch == transfer.Record.OwnershipEpoch;
    }

    private void RedirectPassengers(InterserverTransferRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DestinationAddress))
            return;
        foreach (var id in record.PassengerUserIds)
        {
            if (!Guid.TryParse(id, out var guid) || !_players.TryGetSessionById(new NetUserId(guid), out var session))
                continue;
            RaiseNetworkEvent(new InterserverRedirectEvent(
                record.DestinationAddress,
                record.DestinationName,
                record.TransferId), session.Channel);
        }
    }

    private void HoldAmbiguousSourceInFtl(EntityUid primary)
    {
        if (!TryComp<FTLComponent>(primary, out var ftl) || ftl.State != FTLState.Travelling)
            return;
        ftl.StateTime = StartEndTime.FromCurTime(_timing, TimeSpan.FromSeconds(60));
    }

    private void QuarantineAmbiguousSource(InterserverTransferRecord record)
    {
        var primary = FindPrimaryShip(record.ShipId, record.OwnershipEpoch);
        if (primary == null)
            return;
        if (HasComp<FTLComponent>(primary.Value))
        {
            HoldAmbiguousSourceInFtl(primary.Value);
            return;
        }
        if (!TryComp<ShuttleComponent>(primary.Value, out var shuttle))
            return;

        var current = Transform(primary.Value).Coordinates;
        _shuttle.FTLToCoordinates(primary.Value, shuttle, current,
            _transform.GetWorldRotation(primary.Value), startupTime: 0f, hyperspaceTime: 120f);
    }

    private void FailOutgoing(ActiveInterserverTransfer transfer, string error)
    {
        transfer.Record.Stage = InterserverJournalStage.Failed;
        transfer.Record.Error = error;
        transfer.Record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveOutbox();
        _consoleErrors[transfer.PrimaryGrid] = (error, _timing.RealTime + TimeSpan.FromMinutes(1));
        foreach (var grid in transfer.Grids)
        {
            if (TryComp<InterserverShipComponent>(grid, out var identity) &&
                identity.TransferId == transfer.Record.TransferId)
                identity.TransferId = string.Empty;
        }
        _active.Remove(transfer.Record.TransferId);
        if (Exists(transfer.PrimaryGrid))
            _shuttleConsole.RefreshShuttleConsoles(transfer.PrimaryGrid);
    }

    private void SetConsoleError(EntityUid primary, string error)
    {
        _log.Warning("Interserver transfer rejected for {Grid}: {Error}", ToPrettyString(primary), error);
        _consoleErrors[primary] = (error, _timing.RealTime + TimeSpan.FromMinutes(1));
        _shuttleConsole.RefreshShuttleConsoles(primary);
    }
}
