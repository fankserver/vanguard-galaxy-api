using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadHookHostTests
{
    [Fact]
    public void CaughtNestedRecallStillRejectsTheOuterOptionalLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-world-reentrant-" + Guid.NewGuid().ToString("N"));
        string text = "fixture-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(dir);
        try
        {
            var root = new JsonObject { Text = text, ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue>()) }) }) };
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
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedLoadAndFactoryRemainAttemptScoped(bool repeatRecall)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-world-host-" + Guid.NewGuid().ToString("N"));
        string text = "fixture-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(dir);
        try
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.one", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var poi = new JsonObject { Text = "poi", ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system-a") };
            var system = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
            var map = new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) };
            var root = new JsonObject { Text = text, ["Player"] = new(new JsonObject { ["map"] = new(map) }) };
            JsonValue.ParseFixtures[text] = root;
            string path = Path.Combine(dir, "native.save"); var bytes = Encoding.UTF8.GetBytes(text); File.WriteAllBytes(path, bytes);
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            var row = new WorldSavedObject(identity, "system-a", WorldJsonInspection.Digest(poi), 1);
            var envelope = new OwnerSchemaCodec(WorldStateCodec.Owner, 1, _ => true).Encode(WorldStateCodec.Encode(new[] { row }));
            store.Publish(path, GenerationStore.Hash(bytes), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = envelope, [WorldDefinitionCodec.Owner] = WorldTestDefinitions.Envelope(identity) });
            var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected observer failure", error));
            using var persistence = new PersistenceService(hub, store, Path.GetFullPath, p => GenerationStore.Hash(File.ReadAllBytes(p)));
            Guid session = Guid.Empty; bool advance = false;
            long Revision()
            {
                if (advance) hub.PlayerReady(session);
                return 1;
            }
            using var host = new WorldLoadHookHost(typeof(JsonObject).Assembly, hub, persistence, persistence.CreateWorldReader(), Path.GetFullPath, _ => true, Revision);
            session = hub.Begin(SessionOrigin.SaveLoad, path);
            Assert.True(host.TryRecall(new Source.Util.SaveGameFile(path), out var loaded)); Assert.Same(root, loaded);
            var prepared = host.PreparedFor(session);
            Assert.NotNull(prepared); Assert.Same(root, prepared!.Root);
            Assert.Equal(identity.NativeId, Assert.Single(prepared.Generation!.Rows).Identity.NativeId);
            Assert.Null(host.PreparedFor(Guid.NewGuid()));
            var factoryToken = host.BeginFactory(new JsonValue(poi));
            Assert.NotNull(factoryToken);
            var native = new Source.Galaxy.POI.Combat { guid = identity.NativeId };
            host.CompleteFactory(factoryToken!, native);
            var definition = prepared.Generation.DefinitionFor(Assert.Single(prepared.Generation.Rows));
            Assert.True(host.ConstructedBy(prepared, new WorldSnapshotInstance(native, identity, "system-a", definition)));
            Assert.False(host.ConstructedBy(prepared, new WorldSnapshotInstance(new Source.Galaxy.POI.Combat { guid = identity.NativeId }, identity, "system-a", definition)));
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
        finally { JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }
}
