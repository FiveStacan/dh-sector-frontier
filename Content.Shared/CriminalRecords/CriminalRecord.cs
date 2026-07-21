using Content.Shared.Security;
using Robust.Shared.Serialization;

namespace Content.Shared.CriminalRecords;

/// <summary>
/// Criminal record for a crewmember.
/// Can be viewed and edited in a criminal records console by security.
/// </summary>
[Serializable, NetSerializable, DataRecord, DataDefinition]
public sealed partial record CriminalRecord
{
    /// <summary>
    /// Status of the person (None, Wanted, Detained).
    /// </summary>
    [DataField]
    public SecurityStatus Status = SecurityStatus.None;

    /// <summary>
    /// When Status is Wanted, the reason for it.
    /// Should never be set otherwise.
    /// </summary>
    [DataField]
    public string? Reason;

    /// <summary>
    /// The name of the person who changed the status.
    /// </summary>
    [DataField]
    public string? InitiatorName;

    /// <summary>
    /// Criminal history of the person.
    /// This should have charges and time served added after someone is detained.
    /// </summary>
    [DataField]
    public List<CrimeHistory> History = new();
}

/// <summary>
/// A line of criminal activity and the time it was added at.
/// </summary>
[Serializable, NetSerializable, DataDefinition]
public partial record struct CrimeHistory
{
    [DataField]
    public TimeSpan AddTime;

    [DataField]
    public string Crime;

    [DataField]
    public string? InitiatorName;

    public CrimeHistory(TimeSpan addTime, string crime, string? initiatorName)
    {
        AddTime = addTime;
        Crime = crime;
        InitiatorName = initiatorName;
    }
}
