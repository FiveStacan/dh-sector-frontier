namespace Content.Server._NF.SectorServices;

/// <summary>
/// A station with this component will host all sector-wide services.
/// </summary>
[RegisterComponent]
[Access(typeof(SectorServiceSystem))]
public sealed partial class StationSectorServiceHostComponent : Component
{
    // Dark Haven - persistence: [DataField] so the host->service link is saved. The service entity is a
    // parentless nullspace singleton, so serializing this reference makes the engine auto-include it (SectorBank
    // balances, ShuttleRecords, sector StationRecords, bounties, mail) into the map save and remap the link on
    // load — sector-wide economy/records survive a restart instead of resetting to prototype defaults.
    [DataField, ViewVariables(VVAccess.ReadOnly)]
    public EntityUid? SectorUid;
}
