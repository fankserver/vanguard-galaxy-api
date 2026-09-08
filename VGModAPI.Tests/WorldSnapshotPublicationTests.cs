using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSnapshotPublicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vg-world-publication-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void ScopedPayloadsPublishTogetherOnlyAfterSuccessfulNativeOutcomeAndSupportSaveAs()
    {
        Directory.CreateDirectory(_directory);
        var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected persistence fault", error));
        var store = new GenerationStore(Path.Combine(_directory, "generations"));
        using var coordinator = new PersistenceCoordinator(hub, store, Path.GetFullPath,
            path => GenerationStore.Hash(File.ReadAllBytes(path)));
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var instance = new WorldSnapshotInstance(new object(), identity, "system-a",
            new WorldSavedDefinition("author.a", new WorldCombatDefinition("PoiX", 1, "世界", "player", 2)));
        using var host = new WorldSnapshotHookHost(hub,
            new WorldSnapshotRecorder(new WorldJsonInspection(typeof(JsonObject).Assembly)), () => new[] { instance }, () => 1);
        coordinator.Register(new OwnerSchemaCodec(WorldStateCodec.Owner, 1, bytes => { WorldStateCodec.Decode(bytes); return true; }),
            () => host.CaptureOwner(WorldStateCodec.Owner), _ => { });
        coordinator.Register(new OwnerSchemaCodec(WorldDefinitionCodec.Owner, 1, bytes => { WorldDefinitionCodec.Decode(bytes); return true; }),
            () => host.CaptureOwner(WorldDefinitionCodec.Owner), _ => { });
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var poi = new JsonObject { Text = "poi-state", ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system-a") };
        var system = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
        var root = new JsonObject { Text = "snapshot-a", ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) }) }) };
        host.CompleteSnapshot(host.BeginSnapshot(), root);
        string slot = Path.Combine(_directory, "a.save"), saveAs = Path.Combine(_directory, "b.save");
        Save(slot, LifecycleEventKind.SaveSucceeded);
        var reader = new WorldGenerationReader(store);
        var original = reader.Read(slot, File.ReadAllBytes(slot));
        var row = Assert.Single(original.Rows);
        Assert.Equal(identity.NativeId, row.Identity.NativeId);
        Assert.Equal("世界", original.DefinitionFor(row).Definition.Name);
        foreach (var outcome in new[] { LifecycleEventKind.SaveFailed, LifecycleEventKind.SaveSkipped })
        {
            Save(slot, outcome);
            Assert.Equal(original.Association.Snapshot, reader.Read(slot, File.ReadAllBytes(slot)).Association.Snapshot);
        }
        Save(saveAs, LifecycleEventKind.SaveSucceeded);
        var copied = reader.Read(saveAs, File.ReadAllBytes(saveAs));
        Assert.Equal(identity.NativeId, Assert.Single(copied.Rows).Identity.NativeId);
        Assert.NotEqual(original.Association.Snapshot, copied.Association.Snapshot);
        Assert.Equal(original.Association.Snapshot, reader.Read(slot, File.ReadAllBytes(slot)).Association.Snapshot);

        void Save(string path, LifecycleEventKind outcome)
        {
            using var scope = host.BeginStore(root);
            var operation = Guid.NewGuid();
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, path));
            if (outcome == LifecycleEventKind.SaveSucceeded) File.WriteAllBytes(path, Encoding.UTF8.GetBytes(root.Text));
            hub.Publish(new LifecycleEvent(outcome, hub.CurrentSession, operation, path));
        }
    }
}
