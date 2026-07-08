// LuaCorp - This file is licensed under AGPLv3
// Copyright (c) 2026 LuaCorp
// See AGPLv3.txt for details.

using Robust.Shared.Serialization;

namespace Content.Shared._Lua.Shipyard.BUI;

[NetSerializable, Serializable]
public sealed class ParkingConsoleInterfaceState : BoundUserInterfaceState
{
    public readonly string? ShipDeedTitle;
    public readonly bool IsTargetIdPresent;
    public readonly bool IsShuttleParked;
    public readonly int Balance;
    public readonly int ParkingPrice;
    public readonly TimeSpan ParkingRemaining;
    public readonly TimeSpan ActionCooldownRemaining;
    public readonly bool CanExtendParking;

    public ParkingConsoleInterfaceState(
        string? shipDeedTitle,
        bool isTargetIdPresent,
        bool isShuttleParked,
        int balance = 0,
        int parkingPrice = 0,
        TimeSpan parkingRemaining = default,
        TimeSpan actionCooldownRemaining = default,
        bool canExtendParking = false)
    {
        ShipDeedTitle = shipDeedTitle;
        IsTargetIdPresent = isTargetIdPresent;
        IsShuttleParked = isShuttleParked;
        Balance = balance;
        ParkingPrice = parkingPrice;
        ParkingRemaining = parkingRemaining;
        ActionCooldownRemaining = actionCooldownRemaining;
        CanExtendParking = canExtendParking;
    }
}
