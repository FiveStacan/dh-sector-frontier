using Content.Server.Administration;
using Content.Server.Administration.Components;
using Content.Server.Atmos.Monitor.Components;
using Content.Shared._Mono.ShipGuns;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Administration;
using Content.Shared.Cargo.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Warps;
using Robust.Server.GameObjects;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Server._Lua.Shipyard.Commands;

/// <summary>
/// Loads one grid YAML into a temporary map and prints its shipyard-rule violations.
/// It intentionally does not enumerate or validate the complete vessel prototype catalog.
/// </summary>
[AdminCommand(AdminFlags.Mapping | AdminFlags.Server)]
public sealed class ValidateShuttleMapCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IResourceManager _resources = default!;

    public string Command => "validate_shuttle_map";
    public string Description => "Checks one shuttle YAML and prints shipyard warnings.";
    public string Help => $"Usage: {Command} /Maps/path/to/shuttle.yml";

    private static readonly Dictionary<ShipGunClass, int> GunPoints = new()
    {
        [ShipGunClass.Superlight] = 1,
        [ShipGunClass.Light] = 2,
        [ShipGunClass.Medium] = 4,
        [ShipGunClass.Heavy] = 8,
        [ShipGunClass.Superheavy] = 16,
    };

    private static readonly Dictionary<VesselSize, int> PointLimits = new()
    {
        [VesselSize.Micro] = 4,
        [VesselSize.Small] = 8,
        [VesselSize.Medium] = 24,
        [VesselSize.Large] = 56,
    };

    private static readonly Dictionary<VesselSize, Dictionary<ShipGunClass, int>> GunLimits = new()
    {
        [VesselSize.Micro] = new() { [ShipGunClass.Superlight] = 4, [ShipGunClass.Light] = 1, [ShipGunClass.Medium] = 0, [ShipGunClass.Heavy] = 0, [ShipGunClass.Superheavy] = 0 },
        [VesselSize.Small] = new() { [ShipGunClass.Superlight] = 6, [ShipGunClass.Light] = 2, [ShipGunClass.Medium] = 2, [ShipGunClass.Heavy] = 0, [ShipGunClass.Superheavy] = 0 },
        [VesselSize.Medium] = new() { [ShipGunClass.Superlight] = 24, [ShipGunClass.Light] = 12, [ShipGunClass.Medium] = 6, [ShipGunClass.Heavy] = 3, [ShipGunClass.Superheavy] = 0 },
        [VesselSize.Large] = new() { [ShipGunClass.Superlight] = 56, [ShipGunClass.Light] = 28, [ShipGunClass.Medium] = 14, [ShipGunClass.Heavy] = 7, [ShipGunClass.Superheavy] = 3 },
    };

    private static readonly HashSet<string> ForbiddenFtlAll = ["MachineFTLDrive", "MachineFTLDrive50", "MachineFTLDrive25S"];
    private static readonly HashSet<string> ForbiddenFtlCivilian = ["MachineFTLDrive600", "MachineFTLDrive", "MachineFTLDrive50", "MachineFTLDrive25S", "MachineWarpDrive"];
    private static readonly HashSet<string> ForbiddenIffAll = ["ComputerIFFSyndicateTypan", "ComputerIFFPOI", "ComputerTabletopIFFPOI", "ComputerIFFSyndicate", "ComputerTabletopIFFSyndicate"];
    private static readonly HashSet<string> ForbiddenIffCivilian = ["ComputerIFF", "ComputerTabletopIFF"];
    private static readonly HashSet<string> ForbiddenGenerators = ["GeneratorWallmountAPU", "GeneratorWallmountBasic", "GeneratorRTG", "GeneratorRTGDamaged", "GeneratorBasic15kW", "DebugGenerator", "GeneratorBasic"];
    private static readonly HashSet<string> ForbiddenPower = ["SMESBig", "ADTSMESIndustrial", "ADTSMESIndustrialEmpty", "DebugSMES", "DebugSubstation"];

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        return CompletionResult.FromHintOptions(
            CompletionHelper.ContentFilePath(args[0], _resources),
            "/Maps/.../shuttle.yml");
    }

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var rawPath = args[0].Replace('\\', '/');
        if (!rawPath.StartsWith('/'))
            rawPath = "/" + rawPath;

        if (!rawPath.StartsWith("/Maps/", StringComparison.OrdinalIgnoreCase) ||
            !rawPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            rawPath.Contains("..", StringComparison.Ordinal))
        {
            shell.WriteError("Only absolute YAML paths inside /Maps/ are allowed.");
            return;
        }

        var path = new ResPath(rawPath);
        if (!_resources.TryContentFileRead(path, out var stream))
        {
            shell.WriteError($"Map file not found: {path}");
            return;
        }
        stream.Dispose();

        var mapSystem = _entities.System<MapSystem>();
        var loader = _entities.System<MapLoaderSystem>();
        mapSystem.CreateMap(out var mapId);

        try
        {
            if (!loader.TryLoadGrid(mapId, path, out var loaded) || loaded == null)
            {
                shell.WriteError($"Failed to load grid: {path}");
                return;
            }

            var vessel = _prototypes.EnumeratePrototypes<VesselPrototype>()
                .FirstOrDefault(proto => proto.ShuttlePath == path);
            var grid = loaded.Value.Owner;
            var findings = new List<(bool Dangerous, string Message)>();

            void Report(string category, string message, bool dangerous = false)
                => findings.Add((dangerous, $"[{category}] {message}"));

            void ReportLimit(string category, int actual, int limit, string subject)
            {
                if (actual <= limit)
                    return;
                Report(category, $"{subject}: {actual}, limit {limit}.", limit > 0 && actual > limit * 2);
            }

            if (vessel == null)
                Report("Metadata", "No VesselPrototype references this path; size and faction checks were skipped.");
            else
            {
                shell.WriteLine($"Vessel: {vessel.ID}; size: {vessel.Category}; classes: {string.Join(", ", vessel.Classes ?? [])}");
                if (vessel.Classes == null || vessel.Classes.Count == 0)
                    Report("Class", "The vessel has no class values.");
                if (vessel.Engines == null || vessel.Engines.Count == 0)
                    Report("Engine", "The vessel has no engine values.");
            }

            var gunCounts = GunPoints.Keys.ToDictionary(key => key, _ => 0);
            var points = 0;
            var guns = _entities.EntityQueryEnumerator<ShipGunClassComponent, TransformComponent>();
            while (guns.MoveNext(out _, out var gun, out var transform))
            {
                if (transform.GridUid != grid)
                    continue;
                gunCounts[gun.Class]++;
                points += GunPoints[gun.Class];
            }

            if (vessel != null && PointLimits.TryGetValue(vessel.Category, out var pointLimit))
            {
                ReportLimit("Weapons", points, pointLimit, "weapon points");
                foreach (var (gunClass, count) in gunCounts)
                {
                    var limit = GunLimits[vessel.Category][gunClass];
                    if (count > limit)
                        Report("Weapons", $"{gunClass}: {count}, limit {limit}.", limit > 0 && count > limit * 2);
                }
            }

            var airAlarms = 0;
            var alarmQuery = _entities.EntityQueryEnumerator<AirAlarmComponent, TransformComponent>();
            while (alarmQuery.MoveNext(out _, out var transform))
                if (transform.GridUid == grid) airAlarms++;
            ReportLimit("Atmos", airAlarms, 5, "AirAlarm count");

            var wallSubstations = 0;
            var floorSubstations = 0;
            var basicSmes = 0;
            var advancedSmes = 0;
            var civilian = vessel?.Classes?.Any(c => c is VesselClass.Civilian or VesselClass.Expedition) == true;
            var metadata = _entities.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (metadata.MoveNext(out _, out var meta, out var transform))
            {
                if (transform.GridUid != grid || meta.EntityPrototype?.ID is not { } id)
                    continue;

                if (id == "SubstationWallBasic") wallSubstations++;
                if (id is "SubstationBasic" or "SubstationBasicEmpty") floorSubstations++;
                if (id is "SMESBasic" or "SMESBasicEmpty") basicSmes++;
                if (id is "SMESAdvanced" or "SMESAdvancedEmpty") advancedSmes++;

                if (id.Contains("Debug", StringComparison.Ordinal))
                    Report("Debug", $"forbidden prototype {id}.", true);
                if (ForbiddenPower.Contains(id))
                    Report("Power", $"forbidden prototype {id}.", id.Contains("Debug", StringComparison.Ordinal));
                if (ForbiddenGenerators.Contains(id))
                    Report("Generator", $"forbidden generator {id}.", id.Contains("Debug", StringComparison.Ordinal));
                if (ForbiddenFtlAll.Contains(id) || civilian && ForbiddenFtlCivilian.Contains(id))
                    Report("FTL", $"forbidden prototype {id}.", true);
                if (ForbiddenIffAll.Contains(id) || civilian && ForbiddenIffCivilian.Contains(id))
                    Report("IFF", $"forbidden prototype {id}.", true);
                if (id.Contains("GasMiner", StringComparison.Ordinal))
                    Report("Atmos", $"GasMiner {id} is forbidden.");
            }

            if (vessel != null)
            {
                var size = vessel.Category;
                var wallLimit = size switch { VesselSize.Micro => 1, VesselSize.Small => 2, VesselSize.Medium => 2, _ => 3 };
                var smesLimit = size switch { VesselSize.Micro => 1, VesselSize.Small => 1, VesselSize.Medium => 2, _ => 4 };
                ReportLimit("Power", wallSubstations, wallLimit, "wall substations");
                ReportLimit("Power", basicSmes, smesLimit, "basic SMES");
                if (size == VesselSize.Large)
                {
                    ReportLimit("Power", floorSubstations, 2, "floor substations");
                    ReportLimit("Power", advancedSmes, 4, "advanced SMES");
                    if (basicSmes > 0 && advancedSmes > 0)
                        Report("Power", "basic and advanced SMES are mixed.");
                }
                else if (size is VesselSize.Micro or VesselSize.Small && floorSubstations > 0)
                    Report("Power", $"floor substations are forbidden for {size}.");
                if (size != VesselSize.Large && advancedSmes > 0)
                    Report("Power", $"advanced SMES are only allowed on Large vessels.");
            }

            var godmode = CountOnGrid<GodmodeComponent>(grid);
            var miniguns = CountOnGrid<AdminMinigunComponent>(grid);
            var cash = CountOnGrid<CashComponent>(grid);
            if (godmode > 0) Report("Admin", $"GodmodeComponent count: {godmode}.", true);
            if (miniguns > 0) Report("Admin", $"AdminMinigunComponent count: {miniguns}.", true);
            if (cash > 0) Report("Economy", $"CashComponent count: {cash}.", true);

            if (CountOnGrid<WarpPointComponent>(grid) == 0)
                Report("Warp", "WarpPoint is missing.");

            shell.WriteLine($"Map check: {path}");
            if (findings.Count == 0)
            {
                shell.WriteLine("No warnings found.");
                return;
            }

            foreach (var finding in findings)
                shell.WriteLine($"[{(finding.Dangerous ? "ERROR" : "WARNING")}] {finding.Message}");
            shell.WriteLine($"Result: {findings.Count(f => !f.Dangerous)} warning(s), {findings.Count(f => f.Dangerous)} error(s).");
        }
        catch (Exception exception)
        {
            shell.WriteError($"Failed to validate {path}: {exception.Message}");
        }
        finally
        {
            if (mapSystem.MapExists(mapId))
                mapSystem.DeleteMap(mapId);
        }
    }

    private int CountOnGrid<T>(EntityUid grid) where T : IComponent
    {
        var count = 0;
        var query = _entities.EntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out _, out var transform))
            if (transform.GridUid == grid) count++;
        return count;
    }
}
