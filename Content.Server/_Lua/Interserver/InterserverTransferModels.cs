using System.Text.Json.Serialization;

namespace Content.Server._Lua.Interserver;

/// <summary>Persistent stages of an incoming or outgoing interserver transfer.</summary>
public enum InterserverJournalStage
{
    Reserved,
    Charging,
    Uploaded,
    Committing,
    Committed,
    Completed,
    Aborted,
    Failed,
}

/// <summary>Persistent registry of configured interserver peers.</summary>
public sealed class InterserverRegistryFile
{
    public int Version { get; set; } = 1;
    public List<InterserverPeerRecord> Peers { get; set; } = new();
}

/// <summary>Configuration and authentication data for a trusted interserver peer.</summary>
public sealed class InterserverPeerRecord
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string PublicAddress { get; set; } = string.Empty;
    public string SharedSecret { get; set; } = string.Empty;
    public bool Approved { get; set; }
    /// <summary>Logical IDs of this server's maps which the peer may target.</summary>
    public List<string> AllowedMapIds { get; set; } = new();
}

/// <summary>Persistent journal containing recoverable interserver transfer records.</summary>
public sealed class InterserverJournalFile
{
    public int Version { get; set; } = 1;
    public List<InterserverTransferRecord> Transfers { get; set; } = new();
}

/// <summary>Durable ownership, routing, snapshot, and reservation data for one transfer.</summary>
public sealed class InterserverTransferRecord
{
    public string TransferId { get; set; } = string.Empty;
    public string ShipId { get; set; } = string.Empty;
    public long OwnershipEpoch { get; set; }
    public string SourceServerId { get; set; } = string.Empty;
    public string DestinationServerId { get; set; } = string.Empty;
    public string DestinationMapId { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public string DestinationName { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InterserverJournalStage Stage { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float DestinationX { get; set; }
    public float DestinationY { get; set; }
    public string SnapshotSha256 { get; set; } = string.Empty;
    public string SnapshotFile { get; set; } = string.Empty;
    public List<string> PassengerUserIds { get; set; } = new();
    public long UpdatedUnixTime { get; set; }
    public long ExpiresUnixTime { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>Requests the destination catalog exposed to an authenticated source server.</summary>
public sealed record InterserverCatalogRequest(int ProtocolVersion, string SourceServerId);

/// <summary>Returns the destination server identity, address, and allowed map catalog.</summary>
public sealed record InterserverCatalogResponse(
    bool Success,
    string ServerId,
    string DisplayName,
    string PublicAddress,
    List<InterserverCatalogMap> Maps,
    string? Error = null);

/// <summary>Identifies a logical destination map published in an interserver catalog.</summary>
public sealed record InterserverCatalogMap(string Id, string Name);

/// <summary>Requests collision-free arrival space for a shuttle ownership epoch.</summary>
public sealed record InterserverReserveRequest(
    int ProtocolVersion,
    string TransferId,
    string ShipId,
    long OwnershipEpoch,
    string SourceServerId,
    string DestinationMapId,
    float Width,
    float Height);

/// <summary>Uploads a verified shuttle snapshot and its passenger identities.</summary>
public sealed record InterserverUploadRequest(
    int ProtocolVersion,
    string TransferId,
    string ShipId,
    long OwnershipEpoch,
    string SourceServerId,
    string Sha256,
    string SnapshotBase64,
    List<string> PassengerUserIds);

/// <summary>Identifies a transfer for commit, status, or abort operations.</summary>
public sealed record InterserverTransferRequest(
    int ProtocolVersion,
    string TransferId,
    string SourceServerId);

/// <summary>Reports the durable destination stage and optional arrival or routing information.</summary>
public sealed record InterserverTransferResponse(
    bool Success,
    string Stage,
    string? Error = null,
    float DestinationX = 0,
    float DestinationY = 0,
    string? PublicAddress = null,
    string? DisplayName = null);

internal sealed class ActiveInterserverTransfer
{
    public required InterserverTransferRecord Record;
    public required InterserverPeerRecord Peer;
    public required EntityUid PrimaryGrid;
    public required HashSet<EntityUid> Grids;
    public bool OperationInFlight;
    public TimeSpan NextRetry;
}
