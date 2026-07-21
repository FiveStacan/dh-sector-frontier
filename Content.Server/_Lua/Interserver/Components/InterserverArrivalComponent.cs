using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._Lua.Interserver.Components;

/// <summary>Tracks a committed incoming shuttle until its destination-side FTL arrival completes.</summary>
[RegisterComponent]
public sealed partial class InterserverArrivalComponent : Component
{
    [DataField]
    public string TransferId = string.Empty;

    [DataField]
    public string DestinationMapId = string.Empty;

    [DataField]
    public Vector2 DestinationPosition;

    [DataField]
    public MapId StagingMap;
}
