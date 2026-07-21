using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Text.Json;
using Content.IntegrationTests.Pair;
using Content.Server.Persistence.Components;
using Content.Server._Lua.Interserver.Components;
using Content.Server._Lua.Interserver;
using Content.Shared._Lua.Interserver;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests;

[TestFixture]
public sealed class InterserverTransferProtocolTest
{
    [Test]
    public async Task PersistenceCheckpointPreservesCompletedTransferIds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IServerEntityManager>();
        var loader = server.System<MapLoaderSystem>();
        var mapSystem = server.System<SharedMapSystem>();
        var transferId = Guid.NewGuid().ToString("D");

        await server.WaitAssertion(() =>
        {
            var metadata = entities.AddComponent<PersistenceSaveMetadataComponent>(map.MapUid);
            metadata.SaveUnixTime = 123456;
            metadata.CompletedInterserverTransfers.Add(transferId);

            using var writer = new StringWriter();
            Assert.That(loader.TrySaveMap(map.MapUid, writer), Is.True);
            using var reader = new StringReader(writer.ToString());
            Assert.That(loader.TryLoadGeneric(reader, "persistence-checkpoint-test", out var result), Is.True);

            var loadedMap = result!.Maps.Single();
            var loaded = entities.GetComponent<PersistenceSaveMetadataComponent>(loadedMap.Owner);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SaveUnixTime, Is.EqualTo(123456));
                Assert.That(loaded.CompletedInterserverTransfers, Does.Contain(transferId));
            });
            mapSystem.DeleteMap(loadedMap.Comp.MapId);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShuttleSnapshotPreservesIdentityAndPassengerMarker()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IServerEntityManager>();
        var loader = server.System<MapLoaderSystem>();
        var mapSystem = server.System<SharedMapSystem>();
        var userId = new NetUserId(Guid.NewGuid());
        var transferId = Guid.NewGuid().ToString("D");
        var shipId = Guid.NewGuid().ToString("D");

        await server.WaitAssertion(() =>
        {
            var identity = entities.AddComponent<InterserverShipComponent>(map.Grid.Owner);
            identity.TransferId = transferId;
            identity.ShipId = shipId;
            identity.OwnershipEpoch = 3;
            identity.Primary = true;
            var passenger = entities.SpawnEntity(null, new EntityCoordinates(map.Grid.Owner, Vector2.Zero));
            entities.AddComponent<PersistentPlayerCharacterComponent>(passenger).UserId = userId;

            using var writer = new StringWriter();
            var options = SerializationOptions.Default with
            {
                Category = FileCategory.Save,
                MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                ErrorOnOrphan = false,
            };
            Assert.That(loader.TrySaveGeneric(new HashSet<EntityUid> { map.Grid.Owner }, writer, out _, options), Is.True);

            var targetMapUid = mapSystem.CreateMap(out var targetMap, runMapInit: true);
            using var reader = new StringReader(writer.ToString());
            var loadOptions = MapLoadOptions.Default with { MergeMap = targetMap };
            Assert.That(loader.TryLoadGeneric(reader, "interserver-roundtrip-test", out var result, loadOptions), Is.True);

            var loaded = result!.Grids.Single(grid =>
                entities.TryGetComponent<InterserverShipComponent>(grid.Owner, out var loadedIdentity) &&
                loadedIdentity.ShipId == shipId);
            var loadedIdentity = entities.GetComponent<InterserverShipComponent>(loaded.Owner);
            var passengerFound = entities.EntityQuery<PersistentPlayerCharacterComponent, TransformComponent>()
                .Any(x => x.Item1.UserId == userId && x.Item2.GridUid == loaded.Owner);
            Assert.Multiple(() =>
            {
                Assert.That(loadedIdentity.TransferId, Is.EqualTo(transferId));
                Assert.That(loadedIdentity.OwnershipEpoch, Is.EqualTo(3));
                Assert.That(loadedIdentity.Primary, Is.True);
                Assert.That(passengerFound, Is.True);
            });

            mapSystem.DeleteMap(targetMap);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public void PlacementRejectsExistingGridAndReservation()
    {
        var occupied = new List<Box2>
        {
            InterserverPlacement.BoundsAt(new Vector2(100, 0), new Vector2(40, 40), 10),
            InterserverPlacement.BoundsAt(new Vector2(300, 0), new Vector2(80, 80), 20),
        };
        var candidates = new Queue<Vector2>(new[]
        {
            new Vector2(100, 0), // existing grid
            new Vector2(300, 0), // another transfer reservation
            new Vector2(500, 0), // free
        });

        var result = InterserverPlacement.FindFree(
            new Vector2(50, 50), 50, 1000, 10, 3,
            () => candidates.Dequeue(), occupied);

        Assert.That(result, Is.EqualTo(new Vector2(500, 0)));
    }

    [Test]
    public void PlacementHonorsRadiusAndFailsClosed()
    {
        var candidates = new Queue<Vector2>(new[]
        {
            new Vector2(10, 0),
            new Vector2(2000, 0),
            new Vector2(150, 0),
        });
        var occupied = new[]
        {
            InterserverPlacement.BoundsAt(new Vector2(150, 0), new Vector2(100, 100), 20),
        };

        var result = InterserverPlacement.FindFree(
            new Vector2(100, 100), 100, 1000, 20, 3,
            () => candidates.Dequeue(), occupied);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void RepeatedReservationIsIdempotentButCollisionIsRejected()
    {
        var transfer = Guid.NewGuid().ToString("D");
        var ship = Guid.NewGuid().ToString("D");
        var record = new InterserverTransferRecord
        {
            TransferId = transfer,
            ShipId = ship,
            SourceServerId = "alpha",
            DestinationMapId = "FrontierSector",
            OwnershipEpoch = 7,
        };
        var retry = new InterserverReserveRequest(InterserverTransferConstants.ProtocolVersion,
            transfer, ship, 7, "alpha", "FrontierSector", 20, 30);
        var collision = retry with { ShipId = Guid.NewGuid().ToString("D") };

        Assert.Multiple(() =>
        {
            Assert.That(InterserverTransferProtocol.MatchesReservation(record, retry, "alpha"), Is.True);
            Assert.That(InterserverTransferProtocol.MatchesReservation(record, collision, "alpha"), Is.False);
            Assert.That(InterserverTransferProtocol.MatchesReservation(record, retry, "forged-peer"), Is.False);
        });
    }

    [Test]
    public void OwnershipCommitCannotBeRolledBack()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InterserverTransferProtocol.CanTransition(InterserverJournalStage.Reserved,
                InterserverJournalStage.Uploaded), Is.True);
            Assert.That(InterserverTransferProtocol.CanTransition(InterserverJournalStage.Uploaded,
                InterserverJournalStage.Committing), Is.True);
            Assert.That(InterserverTransferProtocol.CanTransition(InterserverJournalStage.Committing,
                InterserverJournalStage.Committed), Is.True);
            Assert.That(InterserverTransferProtocol.CanTransition(InterserverJournalStage.Committing,
                InterserverJournalStage.Aborted), Is.False);
            Assert.That(InterserverTransferProtocol.CanTransition(InterserverJournalStage.Committed,
                InterserverJournalStage.Aborted), Is.False);
            Assert.That(InterserverTransferProtocol.CanTransition(InterserverJournalStage.Completed,
                InterserverJournalStage.Committed), Is.False);
            Assert.That(InterserverTransferProtocol.IsStaleOwnershipEpoch(4, new long[] { 2, 3 }), Is.False);
            Assert.That(InterserverTransferProtocol.IsStaleOwnershipEpoch(4, new long[] { 3, 5 }), Is.True);
        });
    }

    [Test]
    public void AmbiguousCommittingResponseNeverPermitsSourceRollback()
    {
        var committing = new InterserverTransferResponse(true, nameof(InterserverJournalStage.Committing));
        var missing = new InterserverTransferResponse(false, "Missing");
        var failed = new InterserverTransferResponse(true, nameof(InterserverJournalStage.Failed));
        var committed = new InterserverTransferResponse(true, nameof(InterserverJournalStage.Committed));

        Assert.Multiple(() =>
        {
            Assert.That(InterserverTransferProtocol.ProvesDestinationDidNotCommit(null), Is.False);
            Assert.That(InterserverTransferProtocol.ProvesDestinationDidNotCommit(committing), Is.False);
            Assert.That(InterserverTransferProtocol.ProvesDestinationDidNotCommit(missing), Is.True);
            Assert.That(InterserverTransferProtocol.ProvesDestinationDidNotCommit(failed), Is.True);
            Assert.That(InterserverTransferProtocol.ProvesDestinationDidNotCommit(committed), Is.False);
            Assert.That(InterserverTransferProtocol.IsDestinationCommitted(committed), Is.True);
        });
    }

    [Test]
    public void OnlyUnusedReservationExpires()
    {
        var record = new InterserverTransferRecord
        {
            Stage = InterserverJournalStage.Reserved,
            ExpiresUnixTime = 100,
        };

        Assert.That(InterserverTransferProtocol.ReservationExpired(record, 101), Is.True);
        record.Stage = InterserverJournalStage.Uploaded;
        Assert.That(InterserverTransferProtocol.ReservationExpired(record, 101), Is.False);
        record.Stage = InterserverJournalStage.Committed;
        Assert.That(InterserverTransferProtocol.ReservationExpired(record, 101), Is.False);
    }

    [Test]
    public void ReturningPassengerIsNotRedirectedByStaleOutboxRoute()
    {
        var user = Guid.NewGuid().ToString("D");
        var outgoing = new InterserverTransferRecord
        {
            Stage = InterserverJournalStage.Completed,
            DestinationAddress = "ss14://beta.example",
            UpdatedUnixTime = 100,
            PassengerUserIds = { user },
        };
        var incoming = new InterserverTransferRecord
        {
            Stage = InterserverJournalStage.Committed,
            UpdatedUnixTime = 200,
            PassengerUserIds = { user },
        };

        Assert.That(InterserverTransferProtocol.FindCurrentOutgoingRoute(user, new[] { outgoing },
            Array.Empty<InterserverTransferRecord>()), Is.SameAs(outgoing));
        Assert.That(InterserverTransferProtocol.FindCurrentOutgoingRoute(user, new[] { outgoing },
            new[] { incoming }), Is.Null);
    }

    [Test]
    public void DurableJournalRoundTripsAllRecoveryFields()
    {
        var original = new InterserverJournalFile
        {
            Transfers =
            {
                new InterserverTransferRecord
                {
                    TransferId = Guid.NewGuid().ToString("D"),
                    ShipId = Guid.NewGuid().ToString("D"),
                    OwnershipEpoch = 42,
                    SourceServerId = "alpha",
                    DestinationServerId = "beta",
                    DestinationMapId = "FrontierSector",
                    Stage = InterserverJournalStage.Committed,
                    SnapshotSha256 = new string('a', 64),
                    PassengerUserIds = { Guid.NewGuid().ToString("D") },
                    DestinationX = 1234.5f,
                    DestinationY = -4321.5f,
                },
            },
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<InterserverJournalFile>(json)!;
        var transfer = restored.Transfers.Single();
        Assert.Multiple(() =>
        {
            Assert.That(transfer.TransferId, Is.EqualTo(original.Transfers[0].TransferId));
            Assert.That(transfer.ShipId, Is.EqualTo(original.Transfers[0].ShipId));
            Assert.That(transfer.OwnershipEpoch, Is.EqualTo(42));
            Assert.That(transfer.Stage, Is.EqualTo(InterserverJournalStage.Committed));
            Assert.That(transfer.PassengerUserIds, Has.Count.EqualTo(1));
            Assert.That(transfer.DestinationX, Is.EqualTo(1234.5f));
        });
    }

    [TestCase("sector-one", true)]
    [TestCase("sector_one.2", true)]
    [TestCase("../sector", false)]
    [TestCase("sector/name", false)]
    [TestCase("", false)]
    public void LogicalIdsAreStrict(string id, bool valid)
    {
        Assert.That(InterserverTransferSystem.IsValidIdentifier(id), Is.EqualTo(valid));
    }
}
