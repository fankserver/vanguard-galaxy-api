using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadPreparationTests
{
    [Fact]
    public void OptionalGenerationStillRejectsRootChangesFromValidationCallbacks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-prep-empty-" + Guid.NewGuid().ToString("N"));
        string text = "fixture-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(dir);
        try
        {
            var map = new JsonObject { ["systems"] = new(new List<JsonValue>()) };
            var root = new JsonObject { Text = text, ["Player"] = new(new JsonObject { ["map"] = new(map) }) };
            JsonValue.ParseFixtures[text] = root;
            var bytes = Encoding.UTF8.GetBytes(text); string path = Path.Combine(dir, "native.save"); File.WriteAllBytes(path, bytes);
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
            var prep = new WorldLoadPreparation(new WorldGenerationReader(store), new WorldJsonInspection(typeof(JsonObject).Assembly), gate);
            int calls = 0;
            bool Starting()
            {
                if (++calls == 2) root.Text = "mutated-without-owned-pois";
                return true;
            }
            Assert.Throws<InvalidDataException>(() => prep.Read(session, path, GenerationStore.Hash(bytes), Starting, _ => true, () => 1));
        }
        finally { JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EarlyPreparationAdmitsOnlyUnchangedCurrentDefinitions(int fault)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vg-prep-" + Guid.NewGuid().ToString("N"));
        string text = "fixture-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(dir);
        try
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.one", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var poi = new JsonObject { Text = "poi", ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system-a") };
            var system = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
            var map = new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) };
            var root = new JsonObject { Text = text, ["Player"] = new(new JsonObject { ["map"] = new(map) }) };
            JsonValue.ParseFixtures[text] = root;
            var bytes = Encoding.UTF8.GetBytes(text); string path = Path.Combine(dir, "native.save"); File.WriteAllBytes(path, bytes);
            var row = new WorldSavedObject(identity, "system-a", WorldJsonInspection.Digest(poi), 1);
            var store = new GenerationStore(Path.Combine(dir, "generations"));
            var envelope = new OwnerSchemaCodec(WorldStateCodec.Owner, 1, _ => true).Encode(WorldStateCodec.Encode(new[] { row }));
            store.Publish(path, GenerationStore.Hash(bytes), Guid.NewGuid(), new Dictionary<string, byte[]> { [WorldStateCodec.Owner] = envelope, [WorldDefinitionCodec.Owner] = WorldTestDefinitions.Envelope(identity) });
            var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
            var prep = new WorldLoadPreparation(new WorldGenerationReader(store), new WorldJsonInspection(typeof(JsonObject).Assembly), gate);
            long revision = 1;
            bool Definition(WorldSavedDefinition saved)
            {
                if (fault == 1) return false;
                if (fault == 2) revision++;
                if (fault == 3) poi.Text = "changed-after-metadata";
                if (fault == 4) root.Text = "changed-player-or-vanilla-state";
                return saved.Definition.Revision == 1;
            }
            if (fault == 0)
            {
                Assert.Same(root, prep.Read(session, path, GenerationStore.Hash(bytes), () => true, Definition, () => revision));
                gate.RequireFactory(session, poi, identity.NativeId, row.NativeDigest, revision);
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => prep.Read(session, path, GenerationStore.Hash(bytes), () => true, Definition, () => revision));
                Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, poi, identity.NativeId, row.NativeDigest, revision));
            }
        }
        finally { JsonValue.ParseFixtures.TryRemove(text, out _); Directory.Delete(dir, true); }
    }
}
