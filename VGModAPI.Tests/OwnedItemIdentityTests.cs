using System;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class OwnedItemIdentityTests
{
    private static OwnedItemDefinition Definition() => new("silo", 1, "小型 Silo", "Stackable manufactured goods", "Carbon", 15, 100, OwnedItemStorage.Armory);
    [Fact]
    public void NativeIdentityRetainsEverySupportedFieldWithoutProviderSaveCallbacks()
    {
        var saved = new OwnedItemIdentity("author.a", Definition());
        var restored = OwnedItemIdentity.Read(saved.NativeId);
        Assert.Equal("author.a", restored.Owner); Assert.Equal("silo", restored.Definition.LocalId);
        Assert.Equal("小型 Silo", restored.Definition.Name); Assert.Equal(15, restored.Definition.Volume);
        Assert.Equal(100, restored.Definition.BaseCost); Assert.Equal(OwnedItemStorage.Armory, restored.Definition.Storage);
        Assert.NotEqual(saved.NativeId, new OwnedItemIdentity("author.b", Definition()).NativeId);
        Assert.Throws<InvalidDataException>(() => OwnedItemIdentity.Read(saved.NativeId + "x"));
    }
    [Fact]
    public void ReferenceInspectionRecognizesBothDictionaryKeysAndItemValues()
    {
        var id = new OwnedItemIdentity("author.a", Definition()).NativeId;
        var reader = new WorldJsonInspection(typeof(JsonObject).Assembly);
        Assert.True(reader.HasOwnedItems(new JsonObject { [id] = new JsonValue(12) }));
        Assert.True(reader.HasOwnedItems(new JsonObject { ["item"] = new JsonValue(id) }));
        Assert.False(reader.HasOwnedItems(new JsonObject { ["item"] = new JsonValue("Carbon") }));
        Assert.Throws<InvalidDataException>(() => reader.HasOwnedItems(new JsonObject { ["item"] = new JsonValue("vgmodapi.item.v99.invalid") }));
    }
}
