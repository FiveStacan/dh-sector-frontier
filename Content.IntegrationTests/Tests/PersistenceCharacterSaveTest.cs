using System.IO;
using System.Linq;
using Content.IntegrationTests.Pair;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server.Persistence.Components;
using Content.Server.StationRecords;
using Content.Server.StationRecords.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.CCVar;
using Content.Shared.CriminalRecords;
using Content.Shared.Mind;
using Content.Shared.Security;
using Content.Shared.Silicons.StationAi;
using Content.Shared.StationRecords;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests;

[TestFixture]
public sealed class PersistenceCharacterSaveTest
{
    [Test]
    public async Task LegacyEmptyBootstrapUsesFrontierMap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var maps = server.ResolveDependency<IGameMapManager>();
        var missingSave = $"/missing-persistence-{Guid.NewGuid():N}.yml";

        await server.WaitAssertion(() =>
        {
            config.SetCVar(CCVars.PersistenceMap, "Empty");
            config.SetCVar(CCVars.UsePersistence, true);
            config.SetCVar(CCVars.GameMap, missingSave);

            var selected = maps.GetSelectedMap();
            Assert.That(selected, Is.Not.Null);
            Assert.That(selected!.ID, Is.EqualTo("Frontier"));
            Assert.That(selected.MapPath, Is.EqualTo(new ResPath("/Maps/_Lua/Outpost/frontier.yml")));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingPrimaryPersistenceSaveSelectsRollback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var maps = server.ResolveDependency<IGameMapManager>();
        var resources = server.ResolveDependency<IResourceManager>();
        var primary = new ResPath($"/missing-primary-{Guid.NewGuid():N}.yml");
        var rollback = new ResPath(primary + GameMapManager.PersistenceBackupSuffix);

        await server.WaitAssertion(() =>
        {
            using (resources.UserData.OpenWriteText(rollback))
            {
            }

            config.SetCVar(CCVars.PersistenceMap, "Frontier");
            config.SetCVar(CCVars.UsePersistence, true);
            config.SetCVar(CCVars.GameMap, primary.ToString());

            var selected = maps.GetSelectedMap();
            Assert.That(selected, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(selected!.ID, Is.EqualTo("Frontier"));
                Assert.That(selected.MapPath, Is.EqualTo(rollback));
                Assert.That(selected.IsPersistence, Is.True);
                Assert.That(selected.RandomRotation, Is.False);
                Assert.That(selected.MaxRandomOffset, Is.Zero);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FrontierPersistenceBootstrapCompletesMapInitAndSaves()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var ticker = server.System<GameTicker>();
        var loader = server.System<MapLoaderSystem>();
        var savePath = new ResPath("/frontier-persistence-round-trip-test.yml");

        await server.WaitAssertion(() =>
        {
            config.SetCVar(CCVars.UsePersistence, true);
            var frontier = prototypes.Index<GameMapPrototype>("Frontier");
            var options = DeserializationOptions.Default with { InitializeMaps = true };
            MapId mapId = default;
            Assert.DoesNotThrow(() => ticker.LoadGameMap(frontier, out mapId, options));
            Assert.That(loader.TrySaveMap(mapId, savePath), Is.True);
            Assert.That(loader.TryLoadMap(savePath, out var restoredMap, out _, options), Is.True);
            Assert.That(restoredMap, Is.Not.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StartRoundAcceptsAlreadyInitializedPersistenceMap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = false,
            InLobby = true,
            Dirty = true,
        });

        var server = pair.Server;
        var config = server.ResolveDependency<IConfigurationManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var ticker = server.System<GameTicker>();
        var loader = server.System<MapLoaderSystem>();
        var mapSystem = server.System<SharedMapSystem>();
        var stationSystem = server.System<StationSystem>();
        var entities = server.ResolveDependency<IServerEntityManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var savePath = new ResPath("/frontier-persistence-start-round-test.yml");

        await server.WaitAssertion(() =>
        {
            config.SetCVar(CCVars.UsePersistence, true);
            var frontier = prototypes.Index<GameMapPrototype>("Frontier");
            var options = DeserializationOptions.Default with { InitializeMaps = true };
            ticker.LoadGameMap(frontier, out var sourceMap, options);
            Assert.That(loader.TrySaveMap(sourceMap, savePath), Is.True);
            foreach (var sourceStation in stationSystem.GetStations())
                entities.DeleteEntity(sourceStation);
            mapSystem.DeleteMap(sourceMap);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            config.SetCVar(CCVars.PersistenceMap, "Frontier");
            config.SetCVar(CCVars.GameMap, savePath.ToString());
            Assert.DoesNotThrow(() => ticker.StartRound(force: true));
            Assert.Multiple(() =>
            {
                Assert.That(ticker.LoadedPersistenceSave, Is.True);
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(mapSystem.IsInitialized(ticker.DefaultMap), Is.True);
                Assert.That(mapSystem.IsPaused(ticker.DefaultMap), Is.False);
            });

            Assert.That(stationSystem.GetStations().Count(uid =>
                entities.HasComponent<StationJobsComponent>(uid) &&
                entities.HasComponent<StationSpawningComponent>(uid)), Is.EqualTo(1));
            var session = players.Sessions.Single();
            Assert.DoesNotThrow(() => ticker.MakeJoinGame(session, EntityUid.Invalid));
            Assert.That(session.AttachedEntity, Is.Not.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StationRecordsRoundTripThroughMapSave()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IServerEntityManager>();
        var loader = server.System<MapLoaderSystem>();
        var recordsSystem = server.System<StationRecordsSystem>();
        var savePath = new ResPath("/persistence-station-records-test.yml");

        await server.WaitAssertion(() =>
        {
            var component = entities.AddComponent<StationRecordsComponent>(testMap.MapUid);
            var key = recordsSystem.AddRecordEntry(testMap.MapUid, new GeneralStationRecord
            {
                Name = "Persistence Test",
                JobTitle = "Archivist",
            }, component);
            Assert.That(key.IsValid(), Is.True);
            recordsSystem.AddRecordEntry(key, new CriminalRecord
            {
                Status = SecurityStatus.Wanted,
                Reason = "Persistence testing",
            }, component);

            Assert.That(loader.TrySaveMap(testMap.MapId, savePath), Is.True);
            Assert.That(loader.TryLoadMap(savePath, out var loadedMap, out _), Is.True);
            Assert.That(loadedMap, Is.Not.Null);

            var restored = entities.GetComponent<StationRecordsComponent>(loadedMap!.Value.Owner);
            var restoredKey = new StationRecordKey(key.Id, loadedMap.Value.Owner);
            Assert.Multiple(() =>
            {
                Assert.That(recordsSystem.TryGetRecord(restoredKey, out GeneralStationRecord general, restored), Is.True);
                Assert.That(general!.Name, Is.EqualTo("Persistence Test"));
                Assert.That(recordsSystem.TryGetRecord(restoredKey, out CriminalRecord criminal, restored), Is.True);
                Assert.That(criminal!.Status, Is.EqualTo(SecurityStatus.Wanted));
                Assert.That(criminal.Reason, Is.EqualTo("Persistence testing"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TransientStationAiEyeTargetIsNotSerializedAndIsRestored()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
        });

        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IServerEntityManager>();
        var config = server.ResolveDependency<IConfigurationManager>();
        var loader = server.System<MapLoaderSystem>();
        var eyeSystem = server.System<SharedEyeSystem>();
        var savePath = new ResPath("/persistence-station-ai-eye-test.yml");
        EntityUid brain = default;
        EntityUid target = default;

        await server.WaitAssertion(() =>
        {
            config.SetCVar(CCVars.UsePersistence, true);
            brain = entities.SpawnEntity(null, testMap.GridCoords);
            entities.AddComponent<StationAiHeldComponent>(brain);
            var eye = entities.AddComponent<EyeComponent>(brain);
            target = entities.SpawnEntity("StationAiHolo", testMap.GridCoords);
            eyeSystem.SetTarget(brain, target, eye);

            Assert.That(loader.TrySaveMap(testMap.MapId, savePath), Is.True);
            Assert.That(eye.Target, Is.Null, "the transient target must be suppressed during serialization");

            Assert.That(loader.TryLoadMap(savePath, out var loadedMap, out _), Is.True);
            Assert.That(loadedMap, Is.Not.Null);

            var foundLoadedBrain = false;
            var query = entities.EntityQueryEnumerator<StationAiHeldComponent, EyeComponent, TransformComponent>();
            while (query.MoveNext(out _, out _, out var loadedEye, out var xform))
            {
                if (xform.MapID != loadedMap!.Value.Comp.MapId)
                    continue;

                foundLoadedBrain = true;
                Assert.That(loadedEye.Target, Is.Null);
            }

            Assert.That(foundLoadedBrain, Is.True);
        });

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<EyeComponent>(brain).Target, Is.EqualTo(target));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlayerCharacterIsMarkedAndSerialized()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            DummyTicker = false,
            Dirty = true,
        });

        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IServerEntityManager>();
        var players = server.ResolveDependency<IPlayerManager>();
        var config = server.ResolveDependency<IConfigurationManager>();
        var loader = server.System<MapLoaderSystem>();
        var minds = server.System<SharedMindSystem>();
        var session = players.Sessions.Single();
        var savePath = new ResPath("/persistence-character-save-test.yml");

        EntityUid character = default;
        await server.WaitAssertion(() =>
        {
            config.SetCVar(CCVars.UsePersistence, true);
            character = entities.SpawnEntity(null, testMap.GridCoords);
            var mind = minds.CreateMind(session.UserId, "Persistent Test Character");
            minds.TransferTo(mind, character);

            Assert.That(loader.TrySaveMap(testMap.MapId, savePath), Is.True);
            Assert.That(entities.TryGetComponent(character, out PersistentPlayerCharacterComponent marker), Is.True);
            Assert.That(marker!.UserId, Is.EqualTo(session.UserId));
        });

        await server.WaitIdleAsync();
        var userData = server.ResolveDependency<IResourceManager>().UserData;
        string yaml;
        await using (var stream = userData.Open(savePath, FileMode.Open))
        using (var reader = new StreamReader(stream))
        {
            yaml = await reader.ReadToEndAsync();
        }

        Assert.That(yaml, Does.Contain("PersistentPlayerCharacter"));

        await pair.CleanReturnAsync();
    }
}
