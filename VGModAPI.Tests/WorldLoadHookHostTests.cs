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
    public void ProviderCallbackAdvancingSameSessionCannotAdmitFactory()
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
            store.Publish(path, GenerationStore.Hash(bytes), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = envelope });
            var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected observer failure", error));
            using var persistence = new PersistenceService(hub, store, Path.GetFullPath, p => GenerationStore.Hash(File.ReadAllBytes(p)));
            Guid session = Guid.Empty; bool advance = false;
            long Revision()
            {
                if (advance) hub.PlayerReady(session);
                return 1;
            }
            using var host = new WorldLoadHookHost(typeof(JsonObject).Assembly, hub, persistence, store, Path.GetFullPath, _ => true, Revision);
            session = hub.Begin(SessionOrigin.SaveLoad, path);
            Assert.True(host.TryRecall(new Source.Util.SaveGameFile(path), out var loaded)); Assert.Same(root, loaded);
            host.RequireFactory(new JsonValue(poi));
            advance = true;
            Assert.Throws<InvalidDataException>(() => host.RequireFactory(new JsonValue(poi)));
            Assert.Equal(SessionPhase.PlayerReady, hub.CurrentSession!.Phase);
        }
        finally { JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }
}
