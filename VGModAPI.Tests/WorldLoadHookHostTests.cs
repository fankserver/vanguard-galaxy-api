using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

[Collection("World native assets")]
public sealed class WorldLoadHookHostTests
{
    [Fact]
    public void CaughtNestedRecallStillRejectsTheOuterOptionalLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-world-reentrant-" + Guid.NewGuid().ToString("N"));
        string text = "fixture-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(dir);
        try
        {
            var root = new JsonObject { Text = text, ["Version"] = new("0.8.2.3"), ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue>()) }) }) };
            JsonValue.ParseFixtures[text] = root;
            string path = Path.Combine(dir, "native.save"); File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected observer failure", error));
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            using var persistence = new PersistenceService(hub, store, Path.GetFullPath, p => GenerationStore.Hash(File.ReadAllBytes(p)));
            WorldLoadHookHost? host = null;
            int nested = 0;
            long Revision()
            {
                nested++;
                Assert.Throws<InvalidDataException>(() => host!.TryRecall(new Source.Util.SaveGameFile(path), out _));
                return 1;
            }
            using (host = new WorldLoadHookHost(typeof(JsonObject).Assembly, hub, persistence, persistence.CreateWorldReader(), Path.GetFullPath, _ => true, Revision))
            {
                var session = hub.Begin(SessionOrigin.SaveLoad, path);
                Assert.Throws<InvalidDataException>(() => host.TryRecall(new Source.Util.SaveGameFile(path), out _));
                Assert.Equal(1, nested);
                Assert.Null(host.PreparedFor(session));
            }
        }
        finally { JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 0)]
    [InlineData(false, true, false, 0)]
    [InlineData(false, true, true, 0)]
    [InlineData(false, false, false, 1)]
    [InlineData(false, false, false, 2)]
    [InlineData(false, false, false, 3)]
    [InlineData(false, false, false, 4)]
    [InlineData(false, false, false, 5)]
    [InlineData(false, false, false, 6)]
    public void PreparedLoadAndFactoryRemainAttemptScoped(bool repeatRecall, bool replaceAsset, bool replaceSession, int constructionFault)
    {
        string assetId = "host-" + Guid.NewGuid().ToString("N");
        var asset = new Behaviour.Unit.SpaceShip();
        Behaviour.Unit.SpaceShip.allShips.Add(assetId, asset);
        string dir = Path.Combine(Path.GetTempPath(), "vg-world-host-" + Guid.NewGuid().ToString("N"));
        string text = "fixture-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(dir);
        try
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.one", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var poi = new JsonObject { Text = "poi", ["guid"] = new(identity.NativeId), ["type"] = new(WorldSaveFormat.OwnedCombatType), ["systemName"] = new("system-a"),
                ["guardDescriptors"] = new(new List<JsonValue> { new(new JsonObject { ["type"] = new("FixedPayloadDescriptor"), ["fixedUnit"] = new(assetId), ["unitCount"] = new(1) }) }) };
            var system = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
            var map = new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) };
            var root = new JsonObject { Text = text, ["Version"] = new(WorldSaveFormat.Marker), [WorldSaveFormat.OriginalVersion] = new("0.8.2.3"), ["Player"] = new(new JsonObject { ["map"] = new(map) }) };
            JsonValue.ParseFixtures[text] = root;
            string path = Path.Combine(dir, "native.save"); var bytes = Encoding.UTF8.GetBytes(text); File.WriteAllBytes(path, bytes);
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            var row = new WorldSavedObject(identity, "system-a", WorldJsonInspection.Digest(poi), 1);
            var envelope = new OwnerSchemaCodec(WorldStateCodec.Owner, 1, _ => true).Encode(WorldStateCodec.Encode(new[] { row }));
            store.Publish(path, GenerationStore.Hash(bytes), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = envelope, [WorldDefinitionCodec.Owner] = WorldTestDefinitions.Envelope(identity) });
            var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected observer failure", error));
            using var persistence = new PersistenceService(hub, store, Path.GetFullPath, p => GenerationStore.Hash(File.ReadAllBytes(p)));
            Guid session = Guid.Empty; bool advance = false; bool swapSession = false;
            bool contextCurrent = true; bool replaceDuringInspection = false; int constructorCalls = 0;
            WorldLoadHookHost? host = null;
            long Revision()
            {
                if (swapSession)
                {
                    swapSession = false;
                    session = hub.Begin(SessionOrigin.SaveLoad, path);
                    root["Version"] = new(WorldSaveFormat.Marker); root[WorldSaveFormat.OriginalVersion] = new("0.8.2.3");
                    Assert.True(host!.TryRecall(new Source.Util.SaveGameFile(path), out _));
                }
                if (advance) hub.PlayerReady(session);
                return 1;
            }
            var native = new Source.Galaxy.POI.Combat { guid = identity.NativeId };
            using var ownedHost = host = new WorldLoadHookHost(typeof(JsonObject).Assembly, hub, persistence, persistence.CreateWorldReader(), Path.GetFullPath, _ => contextCurrent, Revision,
                (_, require) =>
                {
                    constructorCalls++;
                    require();
                    if (constructionFault == 5) contextCurrent = false;
                    if (constructionFault == 3) Assert.Throws<InvalidDataException>(() => host!.BeginFactory(new JsonValue(poi)));
                    return native;
                }, requireContext: () =>
                {
                    if (replaceDuringInspection)
                    {
                        replaceDuringInspection = false;
                        session = hub.Begin(SessionOrigin.SaveLoad, path);
                        root["Version"] = new(WorldSaveFormat.Marker); root[WorldSaveFormat.OriginalVersion] = new("0.8.2.3");
                        Assert.True(host!.TryRecall(new Source.Util.SaveGameFile(path), out _));
                        throw new InvalidDataException("Old inspector failed after replacement");
                    }
                    if (!contextCurrent) throw new InvalidDataException("Qualification context lost");
                });
            session = hub.Begin(SessionOrigin.SaveLoad, path);
            Assert.True(host.TryRecall(new Source.Util.SaveGameFile(path), out var loaded)); Assert.Same(root, loaded);
            var prepared = host.PreparedFor(session);
            Assert.NotNull(prepared); Assert.Same(root, prepared!.Root);
            Assert.Equal(identity.NativeId, Assert.Single(prepared.Generation!.Rows).Identity.NativeId);
            Assert.Null(host.PreparedFor(Guid.NewGuid()));
            var factoryToken = host.BeginFactory(new JsonValue(poi));
            Assert.NotNull(factoryToken);
            if (constructionFault >= 4)
            {
                if (constructionFault == 4) contextCurrent = false;
                var priorSession = session;
                replaceDuringInspection = constructionFault == 6;
                Assert.Throws<InvalidDataException>(() => host.ConstructFactory(factoryToken!));
                Assert.Equal(constructionFault == 5 ? 1 : 0, constructorCalls);
                if (constructionFault == 6)
                {
                    Assert.NotEqual(priorSession, session);
                    Assert.NotNull(host.PreparedFor(session));
                    var replacementToken = host.BeginFactory(new JsonValue(poi));
                    Assert.NotNull(replacementToken);
                    Assert.Same(native, host.ConstructFactory(replacementToken!));
                    host.CompleteFactory(replacementToken!, native);
                }
                else Assert.Null(host.PreparedFor(session));
                Assert.Equal(bytes, File.ReadAllBytes(path));
                return;
            }
            if (constructionFault == 3)
            {
                Assert.Throws<InvalidDataException>(() => host.ConstructFactory(factoryToken!));
                Assert.Null(host.PreparedFor(session)); return;
            }
            Assert.Same(native, host.ConstructFactory(factoryToken!));
            if (constructionFault != 0)
            {
                if (constructionFault == 1) Assert.Throws<InvalidDataException>(() => host.CompleteFactory(factoryToken!, new Source.Galaxy.POI.Combat { guid = identity.NativeId }));
                else Assert.Throws<InvalidDataException>(() => host.ConstructFactory(factoryToken!));
                Assert.Null(host.PreparedFor(session)); return;
            }
            host.CompleteFactory(factoryToken!, native);
            var definition = prepared.Generation.DefinitionFor(Assert.Single(prepared.Generation.Rows));
            Assert.True(host.ConstructedBy(prepared, new WorldSnapshotInstance(native, identity, "system-a", definition)));
            Assert.False(host.ConstructedBy(prepared, new WorldSnapshotInstance(new Source.Galaxy.POI.Combat { guid = identity.NativeId }, identity, "system-a", definition)));
            if (replaceAsset)
            {
                Behaviour.Unit.SpaceShip.allShips[assetId] = new Behaviour.Unit.SpaceShip();
                if (replaceSession)
                {
                    var oldSession = session; swapSession = true;
                    Assert.Null(host.PreparedFor(oldSession));
                    Assert.NotEqual(oldSession, session);
                    Assert.NotNull(host.PreparedFor(session));
                    Assert.NotNull(host.BeginFactory(new JsonValue(poi)));
                    return;
                }
                Assert.Throws<InvalidDataException>(() => host.PreparedFor(session));
                Behaviour.Unit.SpaceShip.allShips[assetId] = asset;
                Assert.Null(host.PreparedFor(session));
                Assert.Throws<InvalidDataException>(() => host.RequireFactory(new JsonValue(poi)));
                return;
            }
            if (repeatRecall)
            {
                Assert.Throws<InvalidDataException>(() => host.BeginFactory(new JsonValue(poi)));
                Assert.Null(host.PreparedFor(session));
                Assert.Throws<InvalidDataException>(() => host.TryRecall(new Source.Util.SaveGameFile(path), out _));
                Assert.Null(host.PreparedFor(session));
                Assert.Throws<InvalidDataException>(() => host.RequireFactory(new JsonValue(poi)));
                return;
            }
            advance = true;
            Assert.Throws<InvalidDataException>(() => host.RequireFactory(new JsonValue(poi)));
            Assert.Equal(SessionPhase.PlayerReady, hub.CurrentSession!.Phase);
            host.Dispose(); Assert.Null(host.PreparedFor(session));
        }
        finally { Behaviour.Unit.SpaceShip.allShips.Remove(assetId); JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }
}
