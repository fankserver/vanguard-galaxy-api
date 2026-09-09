using System;
using System.Collections.Generic;
using System.Reflection;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class OwnedRecipeServiceTests
{
    private static OwnedRecipeDefinition Recipe(int revision = 1) => new("recipe", revision, "Container", 100, 30,
        new[] { new OwnedRecipeIngredient(RecipeItemReference.Vanilla("Carbon"), 2) },
        new OwnedRecipeIngredient(RecipeItemReference.FromOwned(new OwnedItemReference("items", "container")), 1));
    [Fact]
    public void RecipeFirstRegistrationWaitsForItemsThenProducesUsableHandleAndRetires()
    {
        using var hub = new LifecycleHub((_, _) => { }); string? output = null;
        var published = new List<OwnedRecipeIdentity>(); var retired = new List<string>();
        using var service = new OwnedRecipeService(hub, (instance, caller) => new StoryHostPlugin((string)instance, caller),
            reference => reference.VanillaId ?? output, published.Add, retired.Add);
        using var a = service.Acquire("recipes.a", Assembly.GetExecutingAssembly())!;
        using var b = service.Acquire("recipes.b", Assembly.GetExecutingAssembly())!;
        Assert.Equal(OwnedRecipeStatus.PendingDependencies, a.Register(Recipe())); Assert.Null(a.Find("recipe"));
        Assert.Equal(OwnedRecipeStatus.Duplicate, a.Register(Recipe()));
        output = new OwnedItemIdentity("items", new OwnedItemDefinition("container", 1, "Container", "", "Carbon", 15, 5, OwnedItemStorage.Materials)).NativeId;
        service.Refresh(); Assert.NotNull(a.Find("recipe"));
        Assert.Equal(OwnedRecipeStatus.Succeeded, b.Register(Recipe()));
        Assert.NotEqual(a.Find("recipe"), b.Find("recipe"));
        a.Dispose(); Assert.Null(a.Find("recipe")); Assert.Single(retired);
        Assert.NotNull(b.Find("recipe")); Assert.Equal(output, published[0].Output.Id);
    }
    [Fact]
    public void SavedRecipeRetainsDependenciesAndJobProgressWithoutLiveAuthor()
    {
        var output = new OwnedItemIdentity("items", new OwnedItemDefinition("container", 1, "Container", "", "Carbon", 15, 5, OwnedItemStorage.Materials));
        var identity = new OwnedRecipeIdentity("recipes", "container", 1, "Container", 100, 30, new[] { ("Carbon", 2) }, (output.NativeId, 1));
        var decoded = OwnedRecipeIdentity.Read(identity.NativeId);
        Assert.Equal(output.NativeId, decoded.Output.Id); Assert.Equal(30, decoded.Seconds);
        Assert.Equal(1, OwnedItemIdentity.Read(decoded.Output.Id).Definition.Revision);
        Assert.DoesNotContain("_SubRecipe_", identity.NativeId);
        var job = new JsonObject { ["recipe"] = identity.NativeId, ["remainingAmount"] = new JsonValue(3), ["progress"] = new JsonValue(12) };
        var root = new JsonObject { ["Version"] = "0.8.2.3", ["job"] = new JsonValue(job) }; int restored = 0;
        var json = new WorldJsonInspection(typeof(JsonObject).Assembly, restoreRecipe: id => { Assert.Equal(identity.NativeId, id); restored++; });
        json.SealSnapshot(root, false);
        Assert.Throws<System.IO.InvalidDataException>(() => new WorldJsonInspection(typeof(JsonObject).Assembly).UnsealVerified(root, false));
        json.UnsealVerified(root, false);
        Assert.Equal(2, restored); Assert.Equal(3, job["remainingAmount"].AsNumber); Assert.Equal(12, job["progress"].AsNumber);
        Assert.Equal("0.8.2.3", root["Version"].AsString);
    }
}
