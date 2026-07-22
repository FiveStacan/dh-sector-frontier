using Robust.Shared.Network;

namespace Content.Server.Persistence.Components;

/// <summary>
/// Links a map-saved character to the account that owned its mind when the save was made.
/// Entity UIDs are remapped when a save is loaded, so the stable account ID has to ride on the
/// character itself.
/// </summary>
[RegisterComponent]
public sealed partial class PersistentPlayerCharacterComponent : Component
{
    [DataField]
    public NetUserId UserId;
}
