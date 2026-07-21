using System.Linq;
using System.Threading.Tasks;
using Content.Shared.Administration;
using Content.Shared.Administration.Events;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Lua.CLVar;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._Lua.Interserver;

public sealed partial class InterserverTransferSystem
{
    private void InitializeAdmin()
    {
        SubscribeNetworkEvent<InterserverAdminRequestStateEvent>(OnAdminRequestState);
        SubscribeNetworkEvent<InterserverAdminApplyLocalEvent>(OnAdminApplyLocal);
        SubscribeNetworkEvent<InterserverAdminUpsertPeerEvent>(OnAdminUpsertPeer);
        SubscribeNetworkEvent<InterserverAdminRemovePeerEvent>(OnAdminRemovePeer);
        SubscribeNetworkEvent<InterserverAdminTestPeerEvent>(OnAdminTestPeer);
    }

    private bool CanManage(ICommonSession session) => _admins.HasAdminFlag(session, AdminFlags.Host);

    private void OnAdminRequestState(InterserverAdminRequestStateEvent ev, EntitySessionEventArgs args)
    {
        if (CanManage(args.SenderSession))
            SendAdminState(args.SenderSession);
    }

    private void OnAdminApplyLocal(InterserverAdminApplyLocalEvent ev, EntitySessionEventArgs args)
    {
        if (!CanManage(args.SenderSession))
            return;
        if (HasTransfersInFlight())
        {
            SendAdminState(args.SenderSession, "Нельзя менять межсерверную конфигурацию во время активного перелёта");
            return;
        }
        if (!IsValidIdentifier(ev.ServerId) || ev.DisplayName.Length is < 1 or > 128 ||
            !Uri.TryCreate(ev.PublicAddress, UriKind.Absolute, out var address) || address.Scheme != "ss14")
        {
            SendAdminState(args.SenderSession, "Некорректные параметры локального сервера");
            return;
        }

        _cfg.SetCVar(CLVars.InterserverEnabled, ev.Enabled);
        _cfg.SetCVar(CLVars.InterserverServerId, ev.ServerId.Trim());
        _cfg.SetCVar(CLVars.InterserverDisplayName, ev.DisplayName.Trim());
        _cfg.SetCVar(CLVars.InterserverPublicAddress, ev.PublicAddress.Trim());
        _cfg.SaveToFile();
        _adminLog.Add(LogType.AdminCommands, LogImpact.Extreme,
            $"Host {args.SenderSession.Name} changed interserver identity: enabled={ev.Enabled}, id={ev.ServerId}, address={ev.PublicAddress}");
        SendAdminState(args.SenderSession, "Настройки сохранены");
        RefreshCatalogs();
    }

    private void OnAdminUpsertPeer(InterserverAdminUpsertPeerEvent ev, EntitySessionEventArgs args)
    {
        if (!CanManage(args.SenderSession))
            return;
        if (HasTransfersInFlight())
        {
            SendAdminState(args.SenderSession, "Нельзя менять пиры во время активного перелёта");
            return;
        }
        var allowed = ev.AllowedMaps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var knownMaps = GetLocalMapIds();
        if (allowed.Any(x => !knownMaps.Contains(x, StringComparer.OrdinalIgnoreCase)))
        {
            SendAdminState(args.SenderSession, "Список содержит неизвестную локальную карту");
            return;
        }
        var peer = new InterserverPeerRecord
        {
            Id = ev.Id.Trim(),
            DisplayName = ev.DisplayName.Trim(),
            ApiUrl = ev.ApiUrl.Trim().TrimEnd('/'),
            PublicAddress = ev.PublicAddress.Trim(),
            SharedSecret = ev.SharedSecret.Trim(),
            Approved = ev.Approved,
            AllowedMapIds = allowed,
        };
        if (!IsValidPeer(peer) || peer.DisplayName.Length is < 1 or > 128)
        {
            SendAdminState(args.SenderSession,
                "Некорректный пир: secret минимум 16 символов, API http(s), адрес ss14://");
            return;
        }

        lock (_registryLock)
        {
            _registry.Peers.RemoveAll(x => string.Equals(x.Id, peer.Id, StringComparison.OrdinalIgnoreCase));
            _registry.Peers.Add(peer);
        }
        SaveRegistry();
        _adminLog.Add(LogType.AdminCommands, LogImpact.Extreme,
            $"Host {args.SenderSession.Name} upserted interserver peer {peer.Id}, approved={peer.Approved}, maps=[{string.Join(',', allowed)}]");
        SendAdminState(args.SenderSession, "Пир сохранён");
        RefreshCatalogs();
    }

    private void OnAdminRemovePeer(InterserverAdminRemovePeerEvent ev, EntitySessionEventArgs args)
    {
        if (!CanManage(args.SenderSession))
            return;
        if (HasTransfersInFlight())
        {
            SendAdminState(args.SenderSession, "Нельзя удалять пир во время активного перелёта");
            return;
        }
        lock (_registryLock)
            _registry.Peers.RemoveAll(x => string.Equals(x.Id, ev.Id, StringComparison.OrdinalIgnoreCase));
        SaveRegistry();
        _catalog.Remove(ev.Id);
        _shuttleConsole.RefreshShuttleConsoles();
        _adminLog.Add(LogType.AdminCommands, LogImpact.Extreme,
            $"Host {args.SenderSession.Name} removed interserver peer {ev.Id}");
        SendAdminState(args.SenderSession, "Пир удалён");
    }

    private void OnAdminTestPeer(InterserverAdminTestPeerEvent ev, EntitySessionEventArgs args)
    {
        if (!CanManage(args.SenderSession))
            return;
        if (!TryGetPeer(ev.Id, out var peer))
        {
            SendAdminState(args.SenderSession, "Пир не найден или не одобрен");
            return;
        }
        _ = TestPeerForAdminAsync(peer, args.SenderSession.UserId);
    }

    private async Task TestPeerForAdminAsync(InterserverPeerRecord peer, NetUserId requester)
    {
        await RefreshCatalogAsync(peer);
        RunOnMainThread(() =>
        {
            if (!_players.TryGetSessionById(requester, out var session) || !CanManage(session))
                return;
            var ok = _catalog.TryGetValue(peer.Id, out var info) && info.Online;
            SendAdminState(session, ok ? $"Связь с {peer.Id} установлена" : $"{peer.Id} недоступен");
        });
    }

    private void SendAdminState(ICommonSession session, string feedback = "")
    {
        List<InterserverAdminPeerInfo> peers;
        lock (_registryLock)
        {
            peers = _registry.Peers.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x =>
                new InterserverAdminPeerInfo(x.Id, x.DisplayName, x.ApiUrl, x.PublicAddress,
                    x.SharedSecret, x.Approved, string.Join(',', x.AllowedMapIds))).ToList();
        }
        RaiseNetworkEvent(new InterserverAdminStateEvent(
            _cfg.GetCVar(CLVars.InterserverEnabled),
            _cfg.GetCVar(CLVars.InterserverServerId),
            _cfg.GetCVar(CLVars.InterserverDisplayName),
            _cfg.GetCVar(CLVars.InterserverPublicAddress),
            peers,
            string.Join(", ", GetLocalMapIds()),
            feedback), session.Channel);
    }
}
