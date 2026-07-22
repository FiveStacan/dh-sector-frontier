// Dark Haven - inter-server shuttle transfers.

using Robust.Shared.Serialization;

namespace Content.Shared._Lua.Interserver;

public static class InterserverTransferConstants
{
    public const int ProtocolVersion = 1;
}

[Serializable, NetSerializable]
public sealed class InterserverMapInfo
{
    public string Id { get; }
    public string Name { get; }

    public InterserverMapInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

[Serializable, NetSerializable]
public sealed class InterserverServerInfo
{
    public string Id { get; }
    public string Name { get; }
    public string PublicAddress { get; }
    public List<InterserverMapInfo> Maps { get; }
    public bool Online { get; }
    public string Error { get; }

    public InterserverServerInfo(
        string id,
        string name,
        string publicAddress,
        List<InterserverMapInfo> maps,
        bool online,
        string error = "")
    {
        Id = id;
        Name = name;
        PublicAddress = publicAddress;
        Maps = maps;
        Online = online;
        Error = error;
    }
}

[Serializable, NetSerializable]
public sealed class InterserverConsoleState
{
    public List<InterserverServerInfo> Servers { get; }
    public InterserverTransferStage Stage { get; }
    public string Status { get; }
    public string TransferId { get; }

    public InterserverConsoleState(
        List<InterserverServerInfo>? servers = null,
        InterserverTransferStage stage = InterserverTransferStage.Idle,
        string status = "",
        string transferId = "")
    {
        Servers = servers ?? new List<InterserverServerInfo>();
        Stage = stage;
        Status = status;
        TransferId = transferId;
    }
}

[Serializable, NetSerializable]
public enum InterserverTransferStage : byte
{
    Idle,
    Reserving,
    Charging,
    Uploading,
    Committing,
    Redirecting,
    Arriving,
    Completed,
    Failed,
}

[Serializable, NetSerializable]
public sealed class InterserverRefreshDestinationsMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class InterserverStartTransferMessage : BoundUserInterfaceMessage
{
    public string ServerId { get; }
    public string MapId { get; }

    public InterserverStartTransferMessage(string serverId, string mapId)
    {
        ServerId = serverId;
        MapId = mapId;
    }
}

/// <summary>
/// Sent only after the destination durably committed the shuttle. Clients whose launcher does not support
/// redial retain the address in a manual reconnect window.
/// </summary>
[Serializable, NetSerializable]
public sealed class InterserverRedirectEvent(
    string address,
    string serverName,
    string transferId) : EntityEventArgs
{
    public readonly string Address = address;
    public readonly string ServerName = serverName;
    public readonly string TransferId = transferId;
}
