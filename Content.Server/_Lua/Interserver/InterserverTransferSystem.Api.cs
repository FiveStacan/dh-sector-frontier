using System.Net;
using System.Net.Http;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Shared.Lua.CLVar;
using Content.Shared._Lua.Interserver;
using Robust.Server.ServerStatus;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.Server._Lua.Interserver;

public sealed partial class InterserverTransferSystem
{
    private void RegisterHttpHandlers()
    {
        AddPostHandler("/api/interserver/catalog", HandleCatalogHttp);
        AddPostHandler("/api/interserver/reserve", HandleReserveHttp);
        AddPostHandler("/api/interserver/upload", HandleUploadHttp);
        AddPostHandler("/api/interserver/commit", HandleCommitHttp);
        AddPostHandler("/api/interserver/status", HandleStatusHttp);
        AddPostHandler("/api/interserver/abort", HandleAbortHttp);
    }

    private void AddPostHandler(string path, Func<IStatusHandlerContext, InterserverPeerRecord, Task> handler)
    {
        _statusHost.AddHandler(async context =>
        {
            if (context.RequestMethod != HttpMethod.Post || context.Url.AbsolutePath != path)
                return false;
            if (!TryAuthenticatePeer(context, out var peer))
            {
                await context.RespondAsync("Unauthorized interserver peer", HttpStatusCode.Unauthorized);
                return true;
            }
            if (!_cfg.GetCVar(CLVars.InterserverEnabled))
            {
                await context.RespondAsync("Interserver transfers disabled", HttpStatusCode.ServiceUnavailable);
                return true;
            }

            try
            {
                await handler(context, peer);
            }
            catch (Exception e)
            {
                _log.Error("HTTP {Path} failed for peer {Peer}: {Error}", path, peer.Id, e);
                await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Internal error"),
                    HttpStatusCode.InternalServerError);
            }
            return true;
        });
    }

    private bool TryAuthenticatePeer(IStatusHandlerContext context, out InterserverPeerRecord peer)
    {
        peer = new InterserverPeerRecord();
        if (!context.RequestHeaders.TryGetValue("X-Interserver-Server", out var serverHeader) ||
            !context.RequestHeaders.TryGetValue("X-Interserver-Secret", out var secretHeader) ||
            !TryGetPeer(serverHeader.ToString(), out peer))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(peer.SharedSecret);
        var supplied = Encoding.UTF8.GetBytes(secretHeader.ToString());
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static bool ValidProtocol(int protocol) => protocol == InterserverTransferConstants.ProtocolVersion;

    private async Task HandleCatalogHttp(IStatusHandlerContext context, InterserverPeerRecord peer)
    {
        var request = await context.RequestBodyJsonAsync<InterserverCatalogRequest>();
        if (request == null || !ValidProtocol(request.ProtocolVersion) || request.SourceServerId != peer.Id)
        {
            await context.RespondJsonAsync(new InterserverCatalogResponse(false, "", "", "", new(),
                "Protocol or source mismatch"), HttpStatusCode.BadRequest);
            return;
        }

        var maps = await RunOnMainThreadAsync(() => GetLocalCatalog(peer));
        await context.RespondJsonAsync(new InterserverCatalogResponse(
            true,
            _cfg.GetCVar(CLVars.InterserverServerId),
            _cfg.GetCVar(CLVars.InterserverDisplayName),
            _cfg.GetCVar(CLVars.InterserverPublicAddress),
            maps));
    }

    private async Task HandleReserveHttp(IStatusHandlerContext context, InterserverPeerRecord peer)
    {
        var request = await context.RequestBodyJsonAsync<InterserverReserveRequest>();
        if (request == null || !ValidReserveRequest(request, peer))
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Invalid reservation"),
                HttpStatusCode.BadRequest);
            return;
        }

        var result = await RunOnMainThreadAsync(() => ReserveIncoming(peer, request));
        await context.RespondJsonAsync(result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict);
    }

    private bool ValidReserveRequest(InterserverReserveRequest request, InterserverPeerRecord peer)
    {
        return ValidProtocol(request.ProtocolVersion) &&
               request.SourceServerId == peer.Id &&
               Guid.TryParse(request.TransferId, out _) &&
               Guid.TryParse(request.ShipId, out _) &&
               request.OwnershipEpoch > 0 &&
               request.Width is > 0 and < 4096 &&
               request.Height is > 0 and < 4096 &&
               IsValidIdentifier(request.DestinationMapId);
    }

    private InterserverTransferResponse ReserveIncoming(
        InterserverPeerRecord peer,
        InterserverReserveRequest request)
    {
        var existing = _inbox.Transfers.FirstOrDefault(x => x.TransferId == request.TransferId);
        if (existing != null)
        {
            if (!InterserverTransferProtocol.MatchesReservation(existing, request, peer.Id))
                return new InterserverTransferResponse(false, existing.Stage.ToString(), "Transfer ID collision");
            var existingNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (InterserverTransferProtocol.ReservationExpired(existing, existingNow))
            {
                existing.Stage = InterserverJournalStage.Aborted;
                existing.Error = "Reservation expired";
                existing.UpdatedUnixTime = existingNow;
                SaveInbox();
            }
            if (existing.Stage is InterserverJournalStage.Aborted or InterserverJournalStage.Failed)
                return ResponseFor(existing, false);
            return ResponseFor(existing, true);
        }

        if (!peer.AllowedMapIds.Contains(request.DestinationMapId, StringComparer.OrdinalIgnoreCase))
            return new InterserverTransferResponse(false, "Failed", "Map is not allowed for this peer");
        if (!TryResolveMap(request.DestinationMapId, out var targetMap, out _))
            return new InterserverTransferResponse(false, "Failed", "Destination map is unavailable");

        var position = FindArrivalPosition(targetMap, request.DestinationMapId, request.Width, request.Height);
        if (position == null)
            return new InterserverTransferResponse(false, "Failed", "No free arrival area");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var record = new InterserverTransferRecord
        {
            TransferId = request.TransferId,
            ShipId = request.ShipId,
            OwnershipEpoch = request.OwnershipEpoch,
            SourceServerId = peer.Id,
            DestinationServerId = _cfg.GetCVar(CLVars.InterserverServerId),
            DestinationMapId = request.DestinationMapId,
            DestinationAddress = _cfg.GetCVar(CLVars.InterserverPublicAddress),
            DestinationName = _cfg.GetCVar(CLVars.InterserverDisplayName),
            Stage = InterserverJournalStage.Reserved,
            Width = request.Width,
            Height = request.Height,
            DestinationX = position.Value.X,
            DestinationY = position.Value.Y,
            UpdatedUnixTime = now,
            ExpiresUnixTime = now + Math.Max(30, _cfg.GetCVar(CLVars.InterserverReservationSeconds)),
        };
        _inbox.Transfers.Add(record);
        SaveInbox();
        _log.Info("Reserved transfer {Transfer} from {Peer} to {Map} at {Position}",
            record.TransferId, peer.Id, record.DestinationMapId, position.Value);
        return ResponseFor(record, true);
    }

    private Vector2? FindArrivalPosition(MapId targetMap, string logicalMapId, float width, float height)
    {
        var occupied = new List<Box2>();
        foreach (var grid in _maps.GetAllGrids(targetMap))
        {
            var bounds = _transform.GetWorldMatrix(grid.Owner).TransformBox(grid.Comp.LocalAABB).Enlarged(20f);
            occupied.Add(bounds);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var transfer in _inbox.Transfers)
        {
            if (!string.Equals(transfer.DestinationMapId, logicalMapId, StringComparison.OrdinalIgnoreCase) ||
                transfer.Stage is InterserverJournalStage.Completed or InterserverJournalStage.Aborted or InterserverJournalStage.Failed ||
                transfer.ExpiresUnixTime < now)
                continue;
            occupied.Add(InterserverPlacement.BoundsAt(
                new Vector2(transfer.DestinationX, transfer.DestinationY),
                new Vector2(transfer.Width, transfer.Height), 40f));
        }

        var min = Math.Max(0, _cfg.GetCVar(CLVars.InterserverArrivalMinRadius));
        var max = Math.Max(min + 100, _cfg.GetCVar(CLVars.InterserverArrivalMaxRadius));
        return InterserverPlacement.FindFree(
            new Vector2(width, height), min, max, 40f, 96,
            () => _random.NextVector2(min, max), occupied);
    }

    private async Task HandleUploadHttp(IStatusHandlerContext context, InterserverPeerRecord peer)
    {
        var maxBytes = Math.Clamp(_cfg.GetCVar(CLVars.InterserverMaxSnapshotMiB), 1, 512) * 1024L * 1024L;
        var maxRequestBytes = maxBytes * 4 / 3 + 1024 * 1024;
        if (context.RequestHeaders.TryGetValue("Content-Length", out var lengthHeader) &&
            long.TryParse(lengthHeader.ToString(), out var contentLength) && contentLength > maxRequestBytes)
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Upload is too large"),
                (HttpStatusCode) 413);
            return;
        }

        var request = await context.RequestBodyJsonAsync<InterserverUploadRequest>();
        if (request == null || !ValidProtocol(request.ProtocolVersion) || request.SourceServerId != peer.Id ||
            !Guid.TryParse(request.TransferId, out _) || !Guid.TryParse(request.ShipId, out _))
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Invalid upload"),
                HttpStatusCode.BadRequest);
            return;
        }

        byte[] snapshot;
        try
        {
            snapshot = Convert.FromBase64String(request.SnapshotBase64);
        }
        catch (FormatException)
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Invalid base64"),
                HttpStatusCode.BadRequest);
            return;
        }

        if (snapshot.LongLength > maxBytes || !string.Equals(Sha256Hex(snapshot), request.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Snapshot size or hash mismatch"),
                HttpStatusCode.BadRequest);
            return;
        }

        var result = await RunOnMainThreadAsync(() => AcceptUpload(peer, request, snapshot));
        await context.RespondJsonAsync(result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict);
    }

    private InterserverTransferResponse AcceptUpload(
        InterserverPeerRecord peer,
        InterserverUploadRequest request,
        byte[] snapshot)
    {
        var record = _inbox.Transfers.FirstOrDefault(x => x.TransferId == request.TransferId);
        if (record == null || record.SourceServerId != peer.Id || record.ShipId != request.ShipId ||
            record.OwnershipEpoch != request.OwnershipEpoch)
            return new InterserverTransferResponse(false, "Failed", "Reservation not found");
        if (record.Stage is InterserverJournalStage.Committed or InterserverJournalStage.Completed)
            return ResponseFor(record, true);
        if (record.Stage is InterserverJournalStage.Aborted or InterserverJournalStage.Failed)
            return new InterserverTransferResponse(false, record.Stage.ToString(), "Reservation is no longer usable");
        if (record.Stage == InterserverJournalStage.Uploaded)
        {
            return string.Equals(record.SnapshotSha256, request.Sha256, StringComparison.OrdinalIgnoreCase)
                ? ResponseFor(record, true)
                : new InterserverTransferResponse(false, record.Stage.ToString(), "Upload retry hash mismatch");
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (InterserverTransferProtocol.ReservationExpired(record, now))
        {
            record.Stage = InterserverJournalStage.Aborted;
            record.Error = "Reservation expired before upload";
            record.UpdatedUnixTime = now;
            SaveInbox();
            return ResponseFor(record, false);
        }

        var path = SnapshotPath(record.TransferId).ToRootedPath();
        _resources.UserData.CreateDir(path.Directory);
        using (var stream = _resources.UserData.OpenWrite(path))
        {
            stream.Write(snapshot);
            stream.Flush();
        }

        record.SnapshotFile = path.ToString();
        record.SnapshotSha256 = request.Sha256.ToLowerInvariant();
        record.PassengerUserIds = request.PassengerUserIds
            .Where(x => Guid.TryParse(x, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(256)
            .ToList();
        record.Stage = InterserverJournalStage.Uploaded;
        record.UpdatedUnixTime = now;
        SaveInbox();
        return ResponseFor(record, true);
    }

    private async Task HandleCommitHttp(IStatusHandlerContext context, InterserverPeerRecord peer)
    {
        var request = await context.RequestBodyJsonAsync<InterserverTransferRequest>();
        if (!ValidTransferRequest(request, peer))
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Invalid commit"),
                HttpStatusCode.BadRequest);
            return;
        }
        var result = await RunOnMainThreadAsync(() => CommitIncoming(peer, request!));
        await context.RespondJsonAsync(result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict);
    }

    private async Task HandleStatusHttp(IStatusHandlerContext context, InterserverPeerRecord peer)
    {
        var request = await context.RequestBodyJsonAsync<InterserverTransferRequest>();
        if (!ValidTransferRequest(request, peer))
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Invalid status request"),
                HttpStatusCode.BadRequest);
            return;
        }
        var result = await RunOnMainThreadAsync(() =>
        {
            var record = _inbox.Transfers.FirstOrDefault(x => x.TransferId == request!.TransferId &&
                x.SourceServerId == peer.Id);
            return record == null
                ? new InterserverTransferResponse(false, "Missing", "Transfer not found")
                : ResponseFor(record, true);
        });
        await context.RespondJsonAsync(result, result.Success ? HttpStatusCode.OK : HttpStatusCode.NotFound);
    }

    private async Task HandleAbortHttp(IStatusHandlerContext context, InterserverPeerRecord peer)
    {
        var request = await context.RequestBodyJsonAsync<InterserverTransferRequest>();
        if (!ValidTransferRequest(request, peer))
        {
            await context.RespondJsonAsync(new InterserverTransferResponse(false, "Failed", "Invalid abort"),
                HttpStatusCode.BadRequest);
            return;
        }
        var result = await RunOnMainThreadAsync(() =>
        {
            var record = _inbox.Transfers.FirstOrDefault(x => x.TransferId == request!.TransferId &&
                x.SourceServerId == peer.Id);
            if (record == null)
                return new InterserverTransferResponse(true, "Missing");
            if (record.Stage is InterserverJournalStage.Committing or InterserverJournalStage.Committed or
                InterserverJournalStage.Completed)
                return ResponseFor(record, false, "Already committed");
            record.Stage = InterserverJournalStage.Aborted;
            record.UpdatedUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveInbox();
            return ResponseFor(record, true);
        });
        await context.RespondJsonAsync(result, result.Success ? HttpStatusCode.OK : HttpStatusCode.Conflict);
    }

    private static bool ValidTransferRequest(InterserverTransferRequest? request, InterserverPeerRecord peer)
    {
        return request != null && ValidProtocol(request.ProtocolVersion) && request.SourceServerId == peer.Id &&
               Guid.TryParse(request.TransferId, out _);
    }

    private InterserverTransferResponse ResponseFor(
        InterserverTransferRecord record,
        bool success,
        string? error = null) => new(
        success,
        record.Stage.ToString(),
        error ?? (string.IsNullOrWhiteSpace(record.Error) ? null : record.Error),
        record.DestinationX,
        record.DestinationY,
        _cfg.GetCVar(CLVars.InterserverPublicAddress),
        _cfg.GetCVar(CLVars.InterserverDisplayName));

    private Task<T> RunOnMainThreadAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        });
        return completion.Task;
    }
}
