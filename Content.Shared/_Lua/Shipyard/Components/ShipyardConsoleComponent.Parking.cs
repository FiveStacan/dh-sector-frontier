// LuaCorp - This file is licensed under AGPLv3
// Copyright (c) 2026 LuaCorp
// See AGPLv3.txt for details.

using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._NF.Shipyard.Components;

public sealed partial class ShipyardConsoleComponent
{
    [DataField]
    public bool ParkingConsole;

    [DataField]
    public TimeSpan ParkingActionCooldown = TimeSpan.FromSeconds(15);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextParkingActionTime = TimeSpan.Zero;
}
