namespace Content.Server._Lua.Interserver.Components;

/// <summary>
/// Stable identity and ownership epoch for a shuttle transferred between servers. This intentionally survives
/// ordinary world persistence so recovery can fence an old copy after a crash.
/// </summary>
[RegisterComponent]
public sealed partial class InterserverShipComponent : Component
{
    [DataField]
    public string ShipId = string.Empty;

    [DataField]
    public string TransferId = string.Empty;

    [DataField]
    public long OwnershipEpoch;

    [DataField]
    public bool Primary;
}

