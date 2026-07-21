namespace Content.Server.Persistence.Components;

/// <summary>
/// Records the generation of a successfully prepared whole-world persistence snapshot. Interserver recovery uses
/// this to distinguish a transfer missing from an old save from a shuttle that was destroyed after a newer save.
/// </summary>
[RegisterComponent]
public sealed partial class PersistenceSaveMetadataComponent : Component
{
    [DataField]
    public long SaveUnixTime;

    [DataField]
    public HashSet<string> CompletedInterserverTransfers = new(StringComparer.OrdinalIgnoreCase);
}
