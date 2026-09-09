using System;
using System.Collections.Generic;
using System.Reflection;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class OwnedItemServiceTests
{
    private static OwnedItemDefinition Item(string name = "Silo") => new("item", 1, name, "Plain goods", "Carbon", 15, 100, OwnedItemStorage.Materials);
    [Fact]
    public void ProvidersCannotAliasDuplicateOrRegisterAfterDisposal()
    {
        using var hub = new LifecycleHub((_, _) => { }); var assembly = Assembly.GetExecutingAssembly();
        using var service = new OwnedItemService(hub, (instance, caller) => new StoryHostPlugin((string)instance, caller));
        using var a = service.Acquire("author.a", assembly)!; using var b = service.Acquire("author.b", assembly)!;
        Assert.Equal(OwnedItemStatus.Succeeded, a.Register(Item())); Assert.Equal(OwnedItemStatus.Succeeded, b.Register(Item()));
        Assert.NotEqual(service.Find("author.a", "item")!.NativeId, service.Find("author.b", "item")!.NativeId);
        Assert.Equal(OwnedItemStatus.Duplicate, a.Register(Item("Renamed")));
        a.Dispose(); Assert.Equal(OwnedItemStatus.Rejected, a.Register(Item())); Assert.Null(service.Find("author.a", "item"));
        Assert.NotNull(service.Find("author.b", "item"));
    }
    [Fact]
    public void PublicationCallbackCannotResurrectDisposedProvider()
    {
        using var hub = new LifecycleHub((_, _) => { }); IOwnedItemProvider? provider = null;
        using var service = new OwnedItemService(hub, (_, caller) => new StoryHostPlugin("author.a", caller), _ => provider!.Dispose());
        provider = service.Acquire(new object(), Assembly.GetExecutingAssembly());
        Assert.Equal(OwnedItemStatus.Rejected, provider!.Register(Item())); Assert.Null(service.Find("author.a", "item"));
    }
    [Fact]
    public void LeveledOrLoreWrappersRefuseBeforeAnyReconstruction()
    {
        var id = new OwnedItemIdentity("author.a", Item()).NativeId;
        var wrapper = new JsonObject { ["itemTypeId"] = id, ["level"] = new JsonValue(4), ["loreKey"] = "Changed" };
        var reader = new WorldJsonInspection(typeof(JsonObject).Assembly); int created = 0;
        var root = new JsonObject { ["plain"] = id, ["wrapped"] = new JsonValue(wrapper) };
        Assert.Throws<System.IO.InvalidDataException>(() => reader.HasOwnedItems(root, _ => created++));
        Assert.Equal(0, created);
        Assert.Throws<System.IO.InvalidDataException>(() => reader.RequirePlainItemValue(new JsonValue(wrapper)));
        reader.RequirePlainItemValue(new JsonValue(id));
    }
    [Fact]
    public void BadQueuedDefinitionDoesNotBlockUnrelatedCatalogDeclarations()
    {
        var bad = new OwnedItemIdentity("author.bad", Item()); var good = new OwnedItemIdentity("author.good", Item());
        var loaded = new List<string>(); int reported = 0;
        OwnedItemDeclarationPublication.Publish(new[] { bad, good }, id =>
        { if (id == bad.NativeId) throw new InvalidOperationException("Missing icon"); loaded.Add(id); }, (_, _) => reported++);
        Assert.Equal(new[] { good.NativeId }, loaded); Assert.Equal(1, reported);
    }
    [Fact]
    public void ItemOnlySaveRequiresBarrierAndRestorationBeforeUnsealing()
    {
        string id = new OwnedItemIdentity("author.a", Item()).NativeId;
        var restored = new List<string>();
        var json = new WorldJsonInspection(typeof(JsonObject).Assembly, restoreItem: restored.Add);
        var root = new JsonObject { ["Version"] = "0.8.2.3", ["inventory"] = new JsonValue(new JsonObject { [id] = new JsonValue(7) }) };
        json.SealSnapshot(root, false); Assert.Equal(WorldSaveFormat.Marker, root["Version"].AsString);
        Assert.Throws<System.IO.InvalidDataException>(() => new WorldJsonInspection(typeof(JsonObject).Assembly).UnsealVerified(root, false));
        Assert.Equal(WorldSaveFormat.Marker, root["Version"].AsString);
        json.UnsealVerified(root, false); Assert.Equal("0.8.2.3", root["Version"].AsString);
        Assert.Equal(new[] { id, id }, restored); Assert.Equal(7, root["inventory"].AsJsonObject[id].AsNumber);
    }
}
