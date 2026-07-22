using System.Linq;
using System.Threading.Tasks;
using Content.Shared.Lua.CLVar;
using Content.Shared._Lua.Interserver;
using Robust.Shared.Map;

namespace Content.Server._Lua.Interserver;

public sealed partial class InterserverTransferSystem
{
    private async Task RefreshCatalogAsync(InterserverPeerRecord peer)
    {
        try
        {
            var request = new InterserverCatalogRequest(
                InterserverTransferConstants.ProtocolVersion,
                _cfg.GetCVar(CLVars.InterserverServerId));
            var response = await SendPeerRequest<InterserverCatalogRequest, InterserverCatalogResponse>(
                peer, "/api/interserver/catalog", request, TimeSpan.FromSeconds(8));
            RunOnMainThread(() =>
            {
                if (response is not { Success: true } || response.ServerId != peer.Id)
                {
                    _catalog[peer.Id] = new InterserverServerInfo(peer.Id, peer.DisplayName,
                        peer.PublicAddress, new List<InterserverMapInfo>(), false,
                        response?.Error ?? Loc.GetString("interserver-catalog-unavailable"));
                }
                else
                {
                    var displayName = string.IsNullOrWhiteSpace(response.DisplayName) || response.DisplayName.Length > 128
                        ? peer.DisplayName
                        : response.DisplayName.Trim();
                    var publicAddress = SanitizePublicAddress(response.PublicAddress, peer.PublicAddress);
                    var maps = (response.Maps ?? new List<InterserverCatalogMap>())
                        .Where(x => IsValidIdentifier(x.Id) && !string.IsNullOrWhiteSpace(x.Name) && x.Name.Length <= 128)
                        .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                        .Take(128)
                        .Select(x => new InterserverMapInfo(x.Id, x.Name.Trim()))
                        .ToList();
                    _catalog[peer.Id] = new InterserverServerInfo(
                        peer.Id,
                        displayName,
                        publicAddress,
                        maps,
                        true);
                }
                _shuttleConsole.RefreshShuttleConsoles();
            });
        }
        catch (Exception e)
        {
            RunOnMainThread(() =>
            {
                _catalog[peer.Id] = new InterserverServerInfo(peer.Id, peer.DisplayName,
                    peer.PublicAddress, new List<InterserverMapInfo>(), false, e.Message);
                _shuttleConsole.RefreshShuttleConsoles();
            });
        }
    }

    private List<InterserverCatalogMap> GetLocalCatalog(InterserverPeerRecord peer)
    {
        var maps = new List<InterserverCatalogMap>();
        foreach (var logicalId in peer.AllowedMapIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveMap(logicalId, out var mapId, out var name))
                continue;
            maps.Add(new InterserverCatalogMap(logicalId, name));
        }
        return maps;
    }

    private bool TryResolveMap(string logicalId, out MapId mapId, out string name)
    {
        mapId = MapId.Nullspace;
        name = logicalId;
        if (string.Equals(logicalId, "FrontierSector", StringComparison.OrdinalIgnoreCase))
        {
            mapId = _ticker.DefaultMap;
            if (!_maps.MapExists(mapId))
                return false;
            var mapUid = _maps.GetMapEntityId(mapId);
            name = Exists(mapUid) ? Name(mapUid) : logicalId;
            return true;
        }

        if (!_sectors.TryGetMapId(logicalId, out mapId) || !_maps.MapExists(mapId))
            return false;
        if (_sectors.TryGetSectorConfig(mapId, out var config))
            name = config.Name;
        return true;
    }

    private IReadOnlyList<string> GetLocalMapIds()
    {
        var result = new List<string>();
        if (_maps.MapExists(_ticker.DefaultMap))
            result.Add("FrontierSector");
        result.AddRange(_sectors.GetSectorMapIds());
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
    }
}
