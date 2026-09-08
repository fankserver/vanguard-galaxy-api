using System;
using System.Collections;
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
public sealed class WorldNativeAttachmentTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AttachmentRequiresUnchangedObservedPlayerMapAndAdmission(int change)
    {
        var hub = new LifecycleHub((_, _) => { });
        var game = new GameAdapter(hub, new GameBindings(typeof(GamePlayer).Assembly), _ => { });
        var map = new GalaxyMapData(); var sector = new SectorMapData { guid = "sector" }; var system = new SystemMapData { guid = "system" };
        map.TestSectors.Add(sector); sector.TestSystems.Add(system);
        var neighbour = new MapPointOfInterest { guid = "neighbour", system = system }; system.pointsOfInterest.Add(neighbour);
        var request = game.BeginLoad(new SaveGameFile(Path.Combine(Path.GetTempPath(), "world-attach.save")));
        IEnumerator Load() { GamePlayer.current = new GamePlayer { map = map }; game.PlayerReconstructed(); yield break; }
        var routine = game.ObserveLoad(Load()); game.EndLoadRequest(request, null); while (routine.MoveNext()) { }
        game.GameplayCompleted(request.Id, new GameplayManager(true), null);
        const string faction = "world.attach.test"; Faction.allFactions.Add(faction, new Faction());
        try
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var definition = new WorldSavedDefinition("author.a", new WorldCombatDefinition("PoiX", 1, "世界", faction, 2));
            var attachment = new WorldNativeAttachment(game);
            var coordinator = new WorldCreationCoordinator(attachment, hub.CheckThread);
            coordinator.Reset(request.Id);
            using var definitions = new WorldDefinitionRegistry((_, caller) => new StoryHostPlugin("author.a", caller), hub.CheckThread);
            var provider = definitions.Acquire(new object(), typeof(WorldNativeAttachmentTests).Assembly)!;
            Assert.True(provider.Register(definition.Definition));
            var gate = new WorldAuthoringGate(definitions, coordinator, _ =>
            {
                if (change == 6)
                {
                    provider.Dispose();
                    var replacement = definitions.Acquire(new object(), typeof(WorldNativeAttachmentTests).Assembly)!;
                    Assert.True(replacement.Register(definition.Definition));
                }
                Assert.Throws<InvalidDataException>(() => coordinator.Snapshot());
                if (change == 5) coordinator.Reset(Guid.NewGuid());
                if (change == 1) GamePlayer.current = new GamePlayer { map = map };
                if (change == 2) GamePlayer.current!.map = new GalaxyMapData();
                if (change == 3) neighbour.position = new UnityEngine.Vector2 { x = 10, y = 20 };
                return change != 4;
            });
            var result = gate.TryCreate(provider, request.Id, "PoiX", identity.InstanceId, "system", 10, 20);
            if (change == 0)
            {
                Assert.NotNull(result); Assert.Equal(2, system.pointsOfInterest.Count);
                Assert.Same(result!.Native, system.pointsOfInterest[1]);
                Assert.Same(result, Assert.Single(coordinator.Snapshot()));
                Assert.Null(coordinator.TryCreate(request.Id, definition, identity, "system", 30, 40, () => true));
                Assert.Null(attachment.TryAppend(request.Id, definition, identity, "system", 30, 40, () => true));
            }
            else { Assert.Null(result); Assert.Same(neighbour, Assert.Single(system.pointsOfInterest)); Assert.Empty(coordinator.Snapshot()); }
            Assert.Equal(0, neighbour.NameReads);
        }
        finally { Faction.allFactions.Remove(faction); GamePlayer.current = null; }
    }
}
