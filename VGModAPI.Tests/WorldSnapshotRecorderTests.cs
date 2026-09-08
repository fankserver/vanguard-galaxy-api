using System;
using System.Collections.Generic;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSnapshotRecorderTests
{
    private static (WorldSnapshotInstance instance, JsonObject root, JsonObject poi) Fixture()
    {
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var instance = new WorldSnapshotInstance(new object(), identity, "system-a", new WorldSavedDefinition("author.a", new WorldCombatDefinition("PoiX", 1, "世界", "player", 2)));
        var poi = new JsonObject { Text = "native-poi", ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system-a") };
        var system = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
        var map = new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) };
        var root = new JsonObject { Text = "native-root", ["Version"] = new("0.8.2.3"), ["Player"] = new(new JsonObject { ["map"] = new(map) }) };
        return (instance, root, poi);
    }
    private static WorldSnapshotRecorder Recorder() => new(new WorldJsonInspection(typeof(JsonObject).Assembly));

    [Fact]
    public void BindsBothOwnerPayloadsToTheActualSerializedSnapshot()
    {
        var (instance, root, poi) = Fixture(); var recorder = Recorder(); var instances = new[] { instance };
        var token = recorder.Begin(1, instances);
        poi.Text = "actual-serialized-state";
        Assert.True(recorder.Complete(token, 1, instances, root));
        Assert.Equal(WorldSaveFormat.Marker, root["Version"].AsString);
        Assert.Equal("0.8.2.3", root[WorldSaveFormat.OriginalVersion].AsString);
        var payload = recorder.ForStore(root);
        Assert.Equal(WorldJsonInspection.Digest(poi), Assert.Single(WorldStateCodec.Decode(payload[WorldStateCodec.Owner])).NativeDigest);
        Assert.Equal("世界", Assert.Single(WorldDefinitionCodec.Decode(payload[WorldDefinitionCodec.Owner])).Definition.Name);
        payload[WorldStateCodec.Owner][0] = 0;
        Assert.Single(WorldStateCodec.Decode(recorder.ForStore(root)[WorldStateCodec.Owner]));
        Assert.Throws<InvalidDataException>(() => recorder.ForStore(new JsonObject { Text = "native-root" }));
        root.Text = "changed";
        Assert.Throws<InvalidDataException>(() => recorder.ForStore(root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedRecaptureRevokesBothOwners(int failure)
    {
        var (instance, root, _) = Fixture(); var recorder = Recorder(); var instances = new[] { instance };
        Assert.True(recorder.Complete(recorder.Begin(1, instances), 1, instances, root));
        var token = recorder.Begin(1, instances);
        if (failure == 0) recorder.Reset();
        Assert.False(recorder.Complete(token, failure == 1 ? 2 : 1, failure == 2 ? Array.Empty<WorldSnapshotInstance>() : instances, root));
        Assert.Throws<InvalidDataException>(() => recorder.ForStore(root));
    }

    [Fact]
    public void StaleAndForeignCompletionsCannotRevokeFreshBindings()
    {
        var (instance, root, _) = Fixture(); var recorder = Recorder(); var instances = new[] { instance };
        var old = recorder.Begin(1, instances);
        recorder.Reset();
        var fresh = recorder.Begin(2, instances);
        Assert.True(recorder.Complete(fresh, 2, instances, root));
        var expected = recorder.ForStore(root);
        foreach (var token in new[] { old, new object(), fresh })
        {
            Assert.False(recorder.Complete(token, 1, instances, root));
            var actual = recorder.ForStore(root);
            Assert.Equal(expected[WorldStateCodec.Owner], actual[WorldStateCodec.Owner]);
            Assert.Equal(expected[WorldDefinitionCodec.Owner], actual[WorldDefinitionCodec.Owner]);
        }
    }

    [Fact]
    public void FailedOuterValidationDoesNotEraseAReentrantNewBinding()
    {
        var (instance, root, _) = Fixture(); var recorder = Recorder(); var instances = new[] { instance };
        var token = recorder.Begin(1, instances);
        Assert.Throws<InvalidDataException>(() => recorder.Complete(token, 1, instances, root, () =>
        {
            var fresh = recorder.Begin(2, instances);
            Assert.True(recorder.Complete(fresh, 2, instances, root));
            throw new InvalidDataException("Outer validation failed");
        }));
        Assert.Single(WorldStateCodec.Decode(recorder.ForStore(root)[WorldStateCodec.Owner]));
        Assert.Single(WorldDefinitionCodec.Decode(recorder.ForStore(root)[WorldDefinitionCodec.Owner]));
    }

    [Fact]
    public void TokensAreSingleUseAndMismatchedParentsRefuse()
    {
        var (instance, root, poi) = Fixture(); var recorder = Recorder(); var instances = new[] { instance };
        var token = recorder.Begin(1, instances);
        Assert.True(recorder.Complete(token, 1, instances, root));
        Assert.False(recorder.Complete(token, 1, instances, root));
        Assert.Single(WorldStateCodec.Decode(recorder.ForStore(root)[WorldStateCodec.Owner]));
        token = recorder.Begin(1, instances); poi["systemName"] = new("other");
        Assert.Throws<InvalidDataException>(() => recorder.Complete(token, 1, instances, root));
        Assert.Throws<InvalidDataException>(() => recorder.ForStore(root));
    }
}
