// LuaCorp - This file is licensed under AGPLv3
// Copyright (c) 2026 LuaCorp
// See AGPLv3.txt for details.

using Content.Server._Lua.Shipyard.Systems;
using Content.Server._Lua.Frontier.Parking;
using Content.Shared._Lua.Shipyard.Events;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Shared._Lua.Shipyard.BUI;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private readonly ShuttleParkingSystem _parking = default!;
    [Dependency] private readonly FrontierParkingSystem _frontierParking = default!;

    private const float ParkingBasePrice = 2000f;
    private const float ParkingBaseMass = 100f;
    private const float ParkingFrontierTaxRate = 0.6f;

    private bool HandleParkingPurchase(EntityUid consoleUid, ShipyardConsoleComponent component, EntityUid player, EntityUid targetId)
    {
        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) || deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        if (component.SelectedDockPort is not { } netDock)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-parking-no-dock-selected"));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        var dockUid = GetEntity(netDock);
        if (IsParkingActionCoolingDown(component))
        {
            PlayDenySound(player, consoleUid, component);
            RefreshParkingState(consoleUid, player, GetFullName(deed), targetId);
            return true;
        }

        var result = _parking.TryRecallShuttle(consoleUid, shuttleUid, dockUid);
        if (result.Error != ShuttleParkingSystem.ShuttleParkingError.Success)
        {
            ConsolePopup(player, Loc.GetString(GetParkingErrorLoc(result)));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        StartParkingActionCooldown(consoleUid, component);
        _frontierParking.StartTrackingFromConsoleRecall(shuttleUid, deed);
        PlayConfirmSound(player, consoleUid, component);
        RefreshParkingState(consoleUid, player, GetFullName(deed), targetId);
        return true;
    }

    private bool HandleParkingSell(EntityUid consoleUid, ShipyardConsoleComponent component, EntityUid player, EntityUid targetId)
    {
        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) || deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        if (IsParkingActionCoolingDown(component))
        {
            PlayDenySound(player, consoleUid, component);
            RefreshParkingState(consoleUid, player, GetFullName(deed), targetId);
            return true;
        }
        if (!TryComp<BankAccountComponent>(player, out var bank))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        var parkingPrice = CalculateParkingPrice(shuttleUid);
        if (bank.Balance < parkingPrice)
        {
            ConsolePopup(player, Loc.GetString("cargo-console-insufficient-funds", ("cost", parkingPrice)));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        if (parkingPrice > 0 && !_bank.TryBankWithdraw(player, parkingPrice))
        {
            ConsolePopup(player, Loc.GetString("cargo-console-insufficient-funds", ("cost", parkingPrice)));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        var result = _parking.TryParkShuttle(consoleUid, shuttleUid);
        if (result.Error != ShuttleParkingSystem.ShuttleParkingError.Success)
        {
            if (parkingPrice > 0)
                _bank.TryBankDeposit(player, parkingPrice);

            var errorLoc = GetParkingErrorLoc(result);
            if (result.Error == ShuttleParkingSystem.ShuttleParkingError.OrganicsAboard) ConsolePopup(player, Loc.GetString(errorLoc, ("name", result.OrganicName ?? "Somebody")));
            else ConsolePopup(player, Loc.GetString(errorLoc));
            PlayDenySound(player, consoleUid, component);
            return true;
        }
        DepositParkingFee(parkingPrice);
        StartParkingActionCooldown(consoleUid, component);
        PlayConfirmSound(player, consoleUid, component);
        RefreshParkingState(consoleUid, player, GetFullName(deed), targetId);
        return true;
    }

    private void OnExtendParkingTime(EntityUid uid, ShipyardConsoleComponent component, ExtendParkingTimeMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId ||
            !TryComp<ShuttleDeedComponent>(targetId, out var deed) ||
            deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!_frontierParking.AddFiveMinutesFromConsole(shuttleUid))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-parking-extend-unavailable"));
            PlayDenySound(player, uid, component);
            RefreshParkingState(uid, player, GetFullName(deed), targetId);
            return;
        }

        PlayConfirmSound(player, uid, component);
        RefreshParkingState(uid, player, GetFullName(deed), targetId);
    }

    private static string GetParkingErrorLoc(ShuttleParkingSystem.ShuttleParkingResult result)
    {
        return result.Error switch
        {
            ShuttleParkingSystem.ShuttleParkingError.AlreadyParked => "shipyard-console-parking-already-parked",
            ShuttleParkingSystem.ShuttleParkingError.ShuttleNotParked => "shipyard-console-parking-not-parked",
            ShuttleParkingSystem.ShuttleParkingError.NotDocked => "shipyard-console-sale-not-docked",
            ShuttleParkingSystem.ShuttleParkingError.OrganicsAboard => "shipyard-console-sale-organic-aboard",
            ShuttleParkingSystem.ShuttleParkingError.CryoPodAboard => "shipyard-console-parking-cryo-pod-aboard",
            ShuttleParkingSystem.ShuttleParkingError.InvalidDock => "shipyard-console-parking-invalid-dock",
            ShuttleParkingSystem.ShuttleParkingError.NoDockingPath => "shipyard-console-parking-no-docking-path",
            ShuttleParkingSystem.ShuttleParkingError.InvalidConsole => "shipyard-console-invalid-station",
            _ => "shipyard-console-sale-invalid-ship",
        };
    }

    private bool TryGetAvailableParkingShuttles(EntityUid uid, EntityUid? targetId, out List<string> available, out List<string> unavailable)
    {
        available = new List<string>();
        unavailable = new List<string>();
        if (TryComp<ShipyardConsoleComponent>(uid, out var console) && console.ParkingConsole)
        {
            if (targetId is { Valid: true } insertedId && TryComp<ShuttleDeedComponent>(insertedId, out var deed) && deed.ShuttleUid is { Valid: true } shuttleUid && _parking.IsParked(shuttleUid) && TryComp<VesselComponent>(shuttleUid, out var vessel))
            { available.Add(vessel.VesselId.ToString()); }
            return true;
        }
        return false;
    }

    private int CalculateParkingPrice(EntityUid shuttleUid)
    {
        if (!TryComp<PhysicsComponent>(shuttleUid, out var physics))
            return 0;

        return Math.Max(0, (int)Math.Round(ParkingBasePrice * physics.FixturesMass / ParkingBaseMass));
    }

    private void DepositParkingFee(int price)
    {
        var frontierShare = (int)Math.Round(price * ParkingFrontierTaxRate);
        var invalidShare = price - frontierShare;

        if (frontierShare > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Frontier, frontierShare, LedgerEntryType.StationDepositAssetsSold);

        if (invalidShare > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Invalid, invalidShare, LedgerEntryType.StationDepositAssetsSold);
    }

    private bool IsParkingActionCoolingDown(ShipyardConsoleComponent component)
    {
        return _timing.CurTime < component.NextParkingActionTime;
    }

    private void StartParkingActionCooldown(EntityUid uid, ShipyardConsoleComponent component)
    {
        component.NextParkingActionTime = _timing.CurTime + component.ParkingActionCooldown;
        Dirty(uid, component);
    }

    private TimeSpan GetParkingActionCooldownRemaining(ShipyardConsoleComponent component)
    {
        var remaining = component.NextParkingActionTime - _timing.CurTime;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void RefreshParkingState(EntityUid uid, EntityUid? player, string? shipDeed, EntityUid? targetId)
    {
        var parked = false;
        var balance = 0;
        var parkingPrice = 0;
        var parkingRemaining = TimeSpan.Zero;
        var canExtendParking = false;

        if (player is { Valid: true } playerUid && TryComp<BankAccountComponent>(playerUid, out var bank))
            balance = bank.Balance;

        if (targetId is { Valid: true } insertedId && TryComp<ShuttleDeedComponent>(insertedId, out var deed) && deed.ShuttleUid is { Valid: true } shuttleUid)
        {
            parked = _parking.IsParked(shuttleUid);
            parkingPrice = CalculateParkingPrice(shuttleUid);
            _frontierParking.TryGetRemainingTime(shuttleUid, out parkingRemaining, out canExtendParking);
        }

        var cooldownRemaining = TryComp<ShipyardConsoleComponent>(uid, out var console)
            ? GetParkingActionCooldownRemaining(console)
            : TimeSpan.Zero;

        BoundUserInterfaceState state = new ParkingConsoleInterfaceState(
            shipDeed,
            targetId.HasValue,
            parked,
            balance,
            parkingPrice,
            parkingRemaining,
            cooldownRemaining,
            canExtendParking);
        ExtendUiStateLua(uid, ref state);
        _ui.SetUiState(uid, ShipyardConsoleUiKey.Parking, state);
    }
}
