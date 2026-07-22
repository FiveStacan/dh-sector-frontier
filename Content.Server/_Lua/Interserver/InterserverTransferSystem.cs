using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Connection;
using Content.Server.GameTicking;
using Content.Server.Persistence.Components;
using Content.Server._NF.RoundNotifications.Events;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Server._Lua.Interserver.Components;
using Content.Server._Lua.Sectors;
using Content.Shared.Administration;
using Content.Shared.Administration.Events;
using Content.Shared.GameTicking;
using Content.Shared.Lua.CLVar;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared._Lua.Interserver;
using Robust.Server.GameObjects;
using Robust.Server.ServerStatus;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Lua.Interserver;

/// <summary>
/// Durable, idempotent server-to-server shuttle handoff. The destination owns the inbox and placement reservation;
/// the source only removes its copy after a committed response (or a later status check) proves ownership changed.
/// </summary>
public sealed partial class InterserverTransferSystem : EntitySystem
{
    private static readonly ResPath OutboxPath = new("/interserver/outbox.json");
    private static readonly ResPath InboxPath = new("/interserver/inbox.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly IStatusHost _statusHost = default!;
    [Dependency] private readonly IHttpClientHolder _http = default!;
    [Dependency] private readonly ITaskManager _tasks = default!;
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private readonly SectorSystem _sectors = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ISharedPlayerManager _players = default!;
    [Dependency] private readonly IConnectionManager _connections = default!;
    [Dependency] private readonly IAdminManager _admins = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly ILogManager _logs = default!;

    private readonly object _registryLock = new();
    private InterserverRegistryFile _registry = new();
    private InterserverJournalFile _outbox = new();
    private InterserverJournalFile _inbox = new();
    private readonly Dictionary<string, ActiveInterserverTransfer> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InterserverServerInfo> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<NetUserId, string> _pendingIncomingUsers = new();
    private readonly Dictionary<EntityUid, (string Error, TimeSpan Expires)> _consoleErrors = new();
    private readonly HashSet<string> _sourceRecoveryInFlight = new(StringComparer.OrdinalIgnoreCase);
    private ISawmill _log = default!;
    private TimeSpan _nextCatalogRefresh;
    private TimeSpan _nextManualCatalogRefresh;
    private TimeSpan _nextRecoveryRetry;

    public override void Initialize()
    {
        base.Initialize();
        _log = _logs.GetSawmill("interserver");
        LoadPersistentState();
        RegisterHttpHandlers();
        InitializeAdmin();

        SubscribeLocalEvent<InterserverShipComponent, FTLStartedEvent>(OnSourceFtlStarted);
        SubscribeLocalEvent<InterserverArrivalComponent, FTLCompletedEvent>(OnIncomingFtlCompleted);
        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _active.Clear();
            _pendingIncomingUsers.Clear();
            _consoleErrors.Clear();
            _sourceRecoveryInFlight.Clear();
        });
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.RealTime;

        foreach (var transfer in _active.Values.ToList())
        {
            if (transfer.Record.Stage != InterserverJournalStage.Committing || transfer.OperationInFlight ||
                now < transfer.NextRetry)
                continue;
            HoldAmbiguousSourceInFtl(transfer.PrimaryGrid);
            transfer.OperationInFlight = true;
            _ = ResolveAmbiguousCommitAsync(transfer);
        }

        foreach (var (grid, failure) in _consoleErrors.ToList())
        {
            if (now < failure.Expires)
                continue;
            _consoleErrors.Remove(grid);
            if (Exists(grid))
                _shuttleConsole.RefreshShuttleConsoles(grid);
        }

        if (now >= _nextRecoveryRetry)
        {
            _nextRecoveryRetry = now + TimeSpan.FromSeconds(10);
            ExpireStaleReservations();
            RecoverSourceOwnership();
        }

        if (!_cfg.GetCVar(CLVars.InterserverEnabled) || now < _nextCatalogRefresh)
            return;

        _nextCatalogRefresh = now + TimeSpan.FromSeconds(30);
        RefreshCatalogs();
    }

    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>Builds the current long-range BSS state for a shuttle console.</summary>
    public InterserverConsoleState GetConsoleState(EntityUid console)
    {
        var servers = _catalog.Values
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var grid = Transform(console).GridUid;
        if (grid == null)
            return new InterserverConsoleState(servers);

        foreach (var transfer in _active.Values)
        {
            if (!transfer.Grids.Contains(grid.Value))
                continue;

            var stage = transfer.Record.Stage switch
            {
                InterserverJournalStage.Reserved => InterserverTransferStage.Reserving,
                InterserverJournalStage.Charging => InterserverTransferStage.Charging,
                InterserverJournalStage.Uploaded => InterserverTransferStage.Uploading,
                InterserverJournalStage.Committing => InterserverTransferStage.Committing,
                InterserverJournalStage.Committed => InterserverTransferStage.Redirecting,
                InterserverJournalStage.Completed => InterserverTransferStage.Completed,
                InterserverJournalStage.Failed => InterserverTransferStage.Failed,
                _ => InterserverTransferStage.Idle,
            };
            return new InterserverConsoleState(servers, stage, transfer.Record.Error, transfer.Record.TransferId);
        }

        if (_consoleErrors.TryGetValue(grid.Value, out var failure))
            return new InterserverConsoleState(servers, InterserverTransferStage.Failed, failure.Error);

        return new InterserverConsoleState(servers);
    }

    /// <summary>Refreshes the destination catalogs of all approved peers.</summary>
    public void RefreshCatalogs()
    {
        if (!_cfg.GetCVar(CLVars.InterserverEnabled))
        {
            _catalog.Clear();
            _shuttleConsole.RefreshShuttleConsoles();
            return;
        }

        var peers = GetApprovedPeers();
        var approvedIds = peers.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _catalog.Keys.Where(x => !approvedIds.Contains(x)).ToList();
        foreach (var id in removed)
            _catalog.Remove(id);
        if (removed.Count > 0)
            _shuttleConsole.RefreshShuttleConsoles();

        foreach (var peer in peers)
            _ = RefreshCatalogAsync(peer);
    }

    /// <summary>Requests a rate-limited manual refresh of destination catalogs.</summary>
    public void RequestCatalogRefresh()
    {
        if (_timing.RealTime < _nextManualCatalogRefresh)
            return;
        _nextManualCatalogRefresh = _timing.RealTime + TimeSpan.FromSeconds(5);
        RefreshCatalogs();
    }

    private bool HasTransfersInFlight() =>
        _active.Count > 0 ||
        _outbox.Transfers.Any(x => !InterserverTransferProtocol.IsTerminal(x.Stage)) ||
        _inbox.Transfers.Any(x => !InterserverTransferProtocol.IsTerminal(x.Stage));

    private void ExpireStaleReservations()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outboxChanged = false;
        var inboxChanged = false;
        foreach (var record in _outbox.Transfers)
        {
            if (!InterserverTransferProtocol.ReservationExpired(record, now))
                continue;
            record.Stage = InterserverJournalStage.Aborted;
            record.Error = "Source reservation expired";
            record.UpdatedUnixTime = now;
            outboxChanged = true;
        }
        foreach (var record in _inbox.Transfers)
        {
            if (!InterserverTransferProtocol.ReservationExpired(record, now))
                continue;
            record.Stage = InterserverJournalStage.Aborted;
            record.Error = "Destination reservation expired";
            record.UpdatedUnixTime = now;
            inboxChanged = true;
        }
        if (outboxChanged)
            SaveOutbox();
        if (inboxChanged)
            SaveInbox();
    }

    /// <summary>Checks whether a connecting user belongs to a committed incoming transfer.</summary>
    public bool HasPendingIncomingCharacter(NetUserId userId)
    {
        return _pendingIncomingUsers.ContainsKey(userId);
    }

    /// <summary>Checks whether a user must be redirected to the latest committed destination.</summary>
    public bool HasCommittedOutgoingRoute(NetUserId userId)
    {
        return TryGetCommittedOutgoingRoute(userId, out _);
    }

    /// <summary>Returns incoming transfer IDs that completed arrival for persistence checkpointing.</summary>
    public IEnumerable<string> GetCompletedIncomingTransferIds()
    {
        return _inbox.Transfers
            .Where(x => x.Stage == InterserverJournalStage.Completed)
            .Select(x => x.TransferId)
            .ToList();
    }

    /// <summary>Marks an incoming user as attached to their transferred character.</summary>
    public void MarkIncomingCharacterAttached(NetUserId userId)
    {
        _pendingIncomingUsers.Remove(userId);
    }

    private void OnRoundStarted(RoundStartedEvent ev)
    {
        RebuildPendingIncomingUsers();
        RecoverCommittedIncomingTransfers();
        RecoverSourceOwnership();
        RefreshCatalogs();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Connected ||
            !TryGetCommittedOutgoingRoute(args.Session.UserId, out var record))
            return;

        RaiseNetworkEvent(new InterserverRedirectEvent(
            record.DestinationAddress,
            record.DestinationName,
            record.TransferId), args.Session.Channel);
    }

    private bool TryGetCommittedOutgoingRoute(NetUserId userId, out InterserverTransferRecord record)
    {
        var raw = userId.UserId.ToString("D");
        var outgoing = InterserverTransferProtocol.FindCurrentOutgoingRoute(raw,
            _outbox.Transfers, _inbox.Transfers);
        if (outgoing == null)
        {
            record = new InterserverTransferRecord();
            return false;
        }

        record = outgoing;
        return true;
    }

    private void LoadPersistentState()
    {
        _registry = LoadJson<InterserverRegistryFile>(RegistryPath()) ?? new InterserverRegistryFile();
        _outbox = LoadJson<InterserverJournalFile>(OutboxPath) ?? new InterserverJournalFile();
        _inbox = LoadJson<InterserverJournalFile>(InboxPath) ?? new InterserverJournalFile();
    }

    private ResPath RegistryPath()
    {
        var configured = _cfg.GetCVar(CLVars.InterserverRegistryPath);
        return new ResPath(string.IsNullOrWhiteSpace(configured) ? "/interserver/peers.json" : configured).ToRootedPath();
    }

    private T? LoadJson<T>(ResPath path) where T : class
    {
        path = path.ToRootedPath();
        var candidates = new[]
        {
            path,
            new ResPath(path + ".bak"),
            new ResPath(path + ".tmp"),
        };
        foreach (var candidate in candidates)
        {
            if (!_resources.UserData.Exists(candidate))
                continue;
            try
            {
                using var stream = _resources.UserData.OpenRead(candidate);
                var value = JsonSerializer.Deserialize<T>(stream, JsonOptions);
                if (value != null)
                {
                    if (candidate != path)
                        _log.Warning("Recovered {Path} from journal fallback {Fallback}", path, candidate);
                    return value;
                }
            }
            catch (Exception e)
            {
                _log.Error("Failed to load {Path}: {Error}", candidate, e);
            }
        }
        return null;
    }

    private void SaveJson<T>(ResPath path, T value)
    {
        path = path.ToRootedPath();
        var temporary = new ResPath(path + ".tmp");
        var backup = new ResPath(path + ".bak");
        _resources.UserData.CreateDir(path.Directory);
        using (var stream = _resources.UserData.OpenWrite(temporary))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush();
        }

        if (_resources.UserData.Exists(backup))
            _resources.UserData.Delete(backup);
        if (_resources.UserData.Exists(path))
            _resources.UserData.Rename(path, backup);
        try
        {
            _resources.UserData.Rename(temporary, path);
        }
        catch
        {
            if (!_resources.UserData.Exists(path) && _resources.UserData.Exists(backup))
                _resources.UserData.Rename(backup, path);
            throw;
        }
    }

    private void SaveRegistry()
    {
        lock (_registryLock)
            SaveJson(RegistryPath(), _registry);
    }

    private void SaveOutbox() => SaveJson(OutboxPath, _outbox);
    private void SaveInbox() => SaveJson(InboxPath, _inbox);

    private List<InterserverPeerRecord> GetApprovedPeers()
    {
        lock (_registryLock)
        {
            return _registry.Peers
                .Where(x => x.Approved && IsValidPeer(x))
                .Select(ClonePeer)
                .ToList();
        }
    }

    private bool TryGetPeer(string id, out InterserverPeerRecord peer, bool requireApproved = true)
    {
        lock (_registryLock)
        {
            var found = _registry.Peers.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (found == null || requireApproved && !found.Approved)
            {
                peer = new InterserverPeerRecord();
                return false;
            }

            peer = ClonePeer(found);
            return true;
        }
    }

    private static InterserverPeerRecord ClonePeer(InterserverPeerRecord peer) => new()
    {
        Id = peer.Id,
        DisplayName = peer.DisplayName,
        ApiUrl = peer.ApiUrl,
        PublicAddress = peer.PublicAddress,
        SharedSecret = peer.SharedSecret,
        Approved = peer.Approved,
        AllowedMapIds = peer.AllowedMapIds.ToList(),
    };

    internal static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    internal static bool IsValidPeer(InterserverPeerRecord peer)
    {
        return IsValidIdentifier(peer.Id) &&
               Uri.TryCreate(peer.ApiUrl, UriKind.Absolute, out var api) &&
               api.Scheme is "http" or "https" &&
               Uri.TryCreate(peer.PublicAddress, UriKind.Absolute, out var address) &&
               address.Scheme == "ss14" &&
               peer.SharedSecret.Length >= 16;
    }

    private static string SanitizePublicAddress(string? candidate, string fallback)
    {
        return Uri.TryCreate(candidate, UriKind.Absolute, out var address) && address.Scheme == "ss14"
            ? candidate!
            : fallback;
    }

    private static ResPath SnapshotPath(string transferId) =>
        new($"/interserver/snapshots/{transferId}.yml");

    private static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private async Task<TResponse?> SendPeerRequest<TRequest, TResponse>(
        InterserverPeerRecord peer,
        string path,
        TRequest request,
        TimeSpan timeout)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(peer.ApiUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        message.Headers.Add("X-Interserver-Server", _cfg.GetCVar(CLVars.InterserverServerId));
        message.Headers.Add("X-Interserver-Secret", peer.SharedSecret);
        message.Content = JsonContent.Create(request);
        using var cancellation = new CancellationTokenSource(timeout);
        using var response = await _http.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        var parsed = await response.Content.ReadFromJsonAsync<TResponse>(cancellation.Token);
        return parsed;
    }

    private void RunOnMainThread(Action action)
    {
        _tasks.RunOnMainThread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _log.Error("Interserver main-thread continuation failed: {Error}", e);
            }
        });
    }
}
