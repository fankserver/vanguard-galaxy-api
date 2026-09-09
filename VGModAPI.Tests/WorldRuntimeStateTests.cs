using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightJson;
using Source.Galaxy;
using Source.Player;
using Source.Util;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Collection("game-double")]
public sealed class WorldRuntimeStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedLoadFactoryAndPlayerReadyPublishBeforeDependentSubscribers(bool disposePersistence)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-world-runtime-" + Guid.NewGuid().ToString("N"));
        string text = "world-runtime-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(dir);
        try
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var poiJson = new JsonObject { Text = "native-poi", ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system") };
            var systemJson = new JsonObject { ["guid"] = new("system"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poiJson) }) };
            var root = new JsonObject { Text = text, ["Version"] = new(WorldSaveFormat.Marker), [WorldSaveFormat.OriginalVersion] = new("0.8.2.3"), ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue> { new(systemJson) }) }) }) };
            JsonValue.ParseFixtures[text] = root;
            byte[] bytes = Encoding.UTF8.GetBytes(text); string path = Path.Combine(dir, "native.save"); File.WriteAllBytes(path, bytes);
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            store.Publish(path, GenerationStore.Hash(bytes), Guid.NewGuid(), new Dictionary<string, byte[]>
            {
                [WorldStateCodec.Owner] = new OwnerSchemaCodec(WorldStateCodec.Owner, 1, _ => true).Encode(WorldStateCodec.Encode(new[] { new WorldSavedObject(identity, "system", WorldJsonInspection.Digest(poiJson), 1) })),
                [WorldDefinitionCodec.Owner] = WorldTestDefinitions.Envelope(identity)
            });
            var hub = new LifecycleHub((_, _) => { });
            var game = new GameAdapter(hub, new GameBindings(typeof(GamePlayer).Assembly), _ => { });
            using var persistence = new PersistenceService(hub, store, Path.GetFullPath, p => GenerationStore.Hash(File.ReadAllBytes(p)));
            using var definitions = new WorldDefinitionRegistry((_, caller) => new StoryHostPlugin("author.a", caller), hub.CheckThread);
            var provider = definitions.Acquire(new object(), typeof(GamePlayer).Assembly)!;
            Assert.True(provider.Register(new WorldCombatDefinition("PoiX", 1, "Site", "player", 1)));
            using var loads = new WorldLoadHookHost(typeof(GamePlayer).Assembly, hub, persistence, persistence.CreateWorldReader(), Path.GetFullPath, definitions.MatchesRetained, () => definitions.Revision);
            var lifetime = new WorldLifetimeGuard();
            using var lifetimeHost = new WorldLifetimeHookHost(typeof(GamePlayer).Assembly, hub, lifetime);
            var creation = new WorldCreationCoordinator(new WorldNativeAttachment(game), hub.CheckThread, lifetime);
            using var snapshots = new WorldSnapshotHookHost(hub, new WorldSnapshotRecorder(new WorldJsonInspection(typeof(GamePlayer).Assembly)), creation.Snapshot, () => creation.Revision);
            using var bindings = new WorldPersistenceBindings(persistence, hub, loads, snapshots, creation);
            Action? runtimeChange = null;
            using var runtime = new WorldRuntimeState(game, loads, definitions, creation, lifetime, bindings.StateReady, () => { runtimeChange?.Invoke(); return true; });
            bool dependentSawRestored = false;
            using var dependent = hub.Subscribe("dependent-content", e =>
            { if (e.Kind == LifecycleEventKind.PlayerReady) dependentSawRestored = creation.Restored(e.Session!.Id) && lifetimeHost.AllowUse(Assert.Single(creation.Snapshot()).Native); });
            var map = new GalaxyMapData(); var sector = new SectorMapData { guid = "sector" }; var system = new SystemMapData { guid = "system" };
            map.TestSectors.Add(sector); sector.TestSystems.Add(system);
            var poi = new Source.Galaxy.POI.Combat { guid = identity.NativeId, system = system, level = 9 };
            var file = new SaveGameFile(path); var request = game.BeginLoad(file);
            IEnumerator Load()
            {
                Assert.True(loads.TryRecall(file, out _));
                var token = loads.BeginFactory(new JsonValue(poiJson)); loads.CompleteFactory(token!, poi);
                system.pointsOfInterest.Add(poi);
                GamePlayer.current = new GamePlayer { map = map }; game.PlayerReconstructed(); yield break;
            }
            var routine = game.ObserveLoad(Load()); game.EndLoadRequest(request, null); while (routine.MoveNext()) { }
            Assert.True(dependentSawRestored); Assert.Same(poi, Assert.Single(creation.Snapshot()).Native); Assert.Equal(9, poi.level);
            Assert.False(bindings.CanMutate(request.Id));
            game.GameplayCompleted(request.Id, new GameplayManager(true), null);
            Assert.True(bindings.CanMutate(request.Id));
            snapshots.CompleteSnapshot(snapshots.BeginSnapshot(), root);
            string saveAs = Path.Combine(dir, "save-as.save");
            using (snapshots.BeginStore(root))
            {
                var operation = Guid.NewGuid();
                hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, saveAs));
                Assert.False(bindings.CanMutate(request.Id));
                Assert.True(bindings.StateReady(request.Id)); Assert.True(lifetimeHost.AllowUse(poi));
                File.WriteAllBytes(saveAs, bytes);
                hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, operation, saveAs));
            }
            var saved = new WorldGenerationReader(store).Read(saveAs, bytes);
            Assert.Equal(identity.NativeId, Assert.Single(saved.Rows).Identity.NativeId);
            Assert.Equal("Site", saved.DefinitionFor(Assert.Single(saved.Rows)).Definition.Name);
            bool hadFaction = Faction.allFactions.TryGetValue("player", out var oldFaction);
            try
            {
                if (!hadFaction) Faction.allFactions.Add("player", new Faction());
                var author = new WorldAuthoringGate(definitions, creation, bindings.CanMutate);
                Assert.NotNull(author.TryCreate(provider, request.Id, "PoiX", Guid.NewGuid(), "system", 20, 20));
                Assert.Equal(2, creation.Snapshot().Length);
            }
            finally { if (!hadFaction) Faction.allFactions.Remove("player"); else Faction.allFactions["player"] = oldFaction!; }
            Assert.True(lifetimeHost.AllowUse(poi));
            runtimeChange = () => { if (disposePersistence) bindings.Dispose(); else creation.Reset(request.Id); };
            Assert.False(lifetimeHost.AllowUse(poi));
            runtimeChange = null;
            provider.Dispose(); Assert.False(lifetimeHost.AllowUse(poi));
            hub.Invalidate("leave"); Assert.False(creation.Restored(request.Id));
            Assert.False(bindings.CanMutate(request.Id));
            Assert.Throws<InvalidDataException>(() => creation.Snapshot());
        }
        finally { GamePlayer.current = null; JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }
}
