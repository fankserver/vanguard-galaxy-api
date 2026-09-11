using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Source.Galaxy;
using Source.Player;
using Source.Util;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Collection("game-double")]
public sealed class WorldNativeReconstructionTests
{
    [Fact]
    public void RebindsAtPlayerReadyWithoutRecreatingOrResettingNativeState()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-world-rebind-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            byte[] bytes = { 1, 2, 3 }; string path = Path.Combine(dir, "native.save");
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            var row = new WorldSavedObject(identity, "system", GenerationStore.Hash(bytes), 1);
            store.Publish(path, GenerationStore.Hash(bytes), Guid.NewGuid(), new Dictionary<string, byte[]>
            {
                [WorldStateCodec.Owner] = new OwnerSchemaCodec(WorldStateCodec.Owner, 1, _ => true).Encode(WorldStateCodec.Encode(new[] { row })),
                [WorldDefinitionCodec.Owner] = WorldTestDefinitions.Envelope(identity)
            });
            var generation = new WorldGenerationReader(store).Read(path, bytes);
            var map = new GalaxyMapData(); var sector = new SectorMapData { guid = "sector" }; var system = new SystemMapData { guid = "system" };
            var poi = new Source.Galaxy.POI.Combat { guid = identity.NativeId, system = system, level = 9, name = "Native persisted state" };
            map.TestSectors.Add(sector); sector.TestSystems.Add(system); system.pointsOfInterest.Add(poi);
            var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected fault", error));
            var game = new GameAdapter(hub, new GameBindings(typeof(GamePlayer).Assembly), _ => { });
            var request = game.BeginLoad(new SaveGameFile(path));
            IEnumerator Load() { GamePlayer.current = new GamePlayer { map = map }; game.PlayerReconstructed(); yield break; }
            var routine = game.ObserveLoad(Load()); game.EndLoadRequest(request, null); while (routine.MoveNext()) { }
            var prepared = new WorldPreparedLoad(request.Id, new object(), generation, 1);
            var reconstruction = new WorldNativeReconstruction(game);
            var coordinator = new WorldCreationCoordinator(new WorldNativeAttachment(game), hub.CheckThread);
            coordinator.Reset(request.Id);
            Assert.True(coordinator.TryRestore(request.Id, () => reconstruction.Read(prepared, () => true, record => ReferenceEquals(record.Native, poi))));
            var restored = Assert.Single(coordinator.Snapshot());
            Assert.False(coordinator.TryRestore(request.Id, () => throw new Exception("Must not replay restoration")));
            Assert.Same(poi, restored.Native); Assert.Same(poi, Assert.Single(system.pointsOfInterest));
            Assert.Equal(9, poi.level); Assert.Equal(0, poi.NameReads); Assert.Equal("Native persisted state", poi.name);
            coordinator.Reset(request.Id);
            Assert.False(coordinator.TryRestore(request.Id, () =>
            {
                coordinator.Reset(Guid.NewGuid());
                return new[] { restored };
            }));
            Assert.Throws<InvalidDataException>(() => coordinator.Snapshot());
            poi.name = "Site";
            WorldRestorationPlan Migration() => reconstruction.Prepare(prepared, () => true, _ => true,
                _ => new WorldSavedDefinition("author.a", new WorldCombatDefinition("PoiX", 2, "Migrated", "player", 1)));
            var wrongLifetime = new WorldLifetimeGuard(); wrongLifetime.Start(Guid.NewGuid());
            var blocked = new WorldCreationCoordinator(new WorldNativeAttachment(game), hub.CheckThread, wrongLifetime); blocked.Reset(request.Id);
            Assert.Throws<InvalidDataException>(() => blocked.TryRestorePrepared(request.Id, Migration));
            Assert.Equal("Site", poi.name);
            coordinator.Reset(request.Id);
            Assert.False(coordinator.TryRestorePrepared(request.Id, () =>
            {
                var plan = Migration(); coordinator.Reset(Guid.NewGuid()); return plan;
            }));
            Assert.Equal("Site", poi.name);
            coordinator.Reset(request.Id);
            Assert.False(coordinator.TryRestorePrepared(request.Id, () =>
            {
                var plan = Migration();
                return new WorldRestorationPlan(plan.Occurrences, () => { plan.Apply(); coordinator.Reset(Guid.NewGuid()); }, plan.Rollback);
            }));
            Assert.Equal("Site", poi.name);
            Assert.Throws<InvalidDataException>(() => reconstruction.Read(prepared, () => false, _ => true));
            Assert.Throws<InvalidDataException>(() => reconstruction.Read(prepared, () => { GamePlayer.current = new GamePlayer { map = map }; return true; }, _ => true));
        }
        finally { GamePlayer.current = null; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
