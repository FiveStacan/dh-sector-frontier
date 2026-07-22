using Robust.Shared.Serialization;

namespace Content.Shared.Administration.Events;

[Serializable, NetSerializable]
public sealed class InterserverAdminRequestStateEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class InterserverAdminApplyLocalEvent(
    bool enabled,
    string serverId,
    string displayName,
    string publicAddress) : EntityEventArgs
{
    public readonly bool Enabled = enabled;
    public readonly string ServerId = serverId;
    public readonly string DisplayName = displayName;
    public readonly string PublicAddress = publicAddress;
}

[Serializable, NetSerializable]
public sealed class InterserverAdminUpsertPeerEvent(
    string id,
    string displayName,
    string apiUrl,
    string publicAddress,
    string sharedSecret,
    bool approved,
    string allowedMaps) : EntityEventArgs
{
    public readonly string Id = id;
    public readonly string DisplayName = displayName;
    public readonly string ApiUrl = apiUrl;
    public readonly string PublicAddress = publicAddress;
    public readonly string SharedSecret = sharedSecret;
    public readonly bool Approved = approved;
    public readonly string AllowedMaps = allowedMaps;
}

[Serializable, NetSerializable]
public sealed class InterserverAdminRemovePeerEvent(string id) : EntityEventArgs
{
    public readonly string Id = id;
}

[Serializable, NetSerializable]
public sealed class InterserverAdminTestPeerEvent(string id) : EntityEventArgs
{
    public readonly string Id = id;
}

[Serializable, NetSerializable]
public sealed class InterserverAdminPeerInfo
{
    public string Id { get; }
    public string DisplayName { get; }
    public string ApiUrl { get; }
    public string PublicAddress { get; }
    public bool SecretConfigured { get; }
    public bool Approved { get; }
    public string AllowedMaps { get; }

    public InterserverAdminPeerInfo(
        string id,
        string displayName,
        string apiUrl,
        string publicAddress,
        bool secretConfigured,
        bool approved,
        string allowedMaps)
    {
        Id = id;
        DisplayName = displayName;
        ApiUrl = apiUrl;
        PublicAddress = publicAddress;
        SecretConfigured = secretConfigured;
        Approved = approved;
        AllowedMaps = allowedMaps;
    }
}

[Serializable, NetSerializable]
public sealed class InterserverAdminStateEvent(
    bool enabled,
    string serverId,
    string displayName,
    string publicAddress,
    List<InterserverAdminPeerInfo> peers,
    string localMaps,
    string feedback) : EntityEventArgs
{
    public readonly bool Enabled = enabled;
    public readonly string ServerId = serverId;
    public readonly string DisplayName = displayName;
    public readonly string PublicAddress = publicAddress;
    public readonly List<InterserverAdminPeerInfo> Peers = peers;
    public readonly string LocalMaps = localMaps;
    public readonly string Feedback = feedback;
}
