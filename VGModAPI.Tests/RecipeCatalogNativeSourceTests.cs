using System;
using System.Linq;
using Behaviour.Crafting;
using Behaviour.Item;
using Behaviour.Mining;
using Source.Mining;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class RecipeCatalogNativeSourceTests : IDisposable
{
    private static RecipeCatalogNativeSource Source() => new(typeof(CraftingRecipe).Assembly,
        (prefab, type) => ((UnityEngine.GameObject)prefab).Components.TryGetValue(type, out var component) ? component : null,
        text => text.TrimStart('@'));
    private static CraftingRecipe Recipe(string id)
    {
        var item = new InventoryItemType { identifier = "output" };
        var prefab = new UnityEngine.GameObject(); prefab.Components[typeof(InventoryItemType)] = item;
        var recipe = new CraftingRecipe { identifier = id };
        recipe.results.Add(new() { item = prefab, count = 3 }); recipe.subRecipes.Add(recipe);
        return recipe;
    }
    public void Dispose() { Forge.current = null; CraftingRecipe.all = Array.Empty<CraftingRecipe>(); InventoryItemType.all = Array.Empty<InventoryItemType>(); }

    [Fact]
    public void UsesStationRecipesAndReReadsRegistryWithoutStaleCache()
    {
        var a = Recipe("a"); var b = Recipe("b");
        Forge.current = new Forge { recipes = new[] { b } }; CraftingRecipe.all = new[] { a, b };
        var source = Source();
        Assert.Equal("forge/b", Assert.Single(source.Read(Guid.NewGuid(), false).Recipes).Id.LocalId);
        var all = source.Read(Guid.NewGuid(), true);
        Assert.Equal(RecipeAvailability.Locked, all.Recipes.Single(row => row.Id.LocalId == "forge/a").Availability);
        Assert.Equal(2, all.FindProducers(new RecipeResourceId("vanilla", "output", RecipeResourceKind.Item), true).Count);
        Forge.current.recipes = new[] { a }; CraftingRecipe.all = new[] { a };
        Assert.Equal("forge/a", Assert.Single(source.Read(Guid.NewGuid(), true).Recipes).Id.LocalId);
        Forge.current = null; Assert.Equal(RecipeCatalogStatus.StationUnavailable, source.Read(Guid.NewGuid(), true).Status);
    }
    [Fact]
    public void ReadsBuilderIdentityWithoutCreatingPreviewOrResult()
    {
        var recipe = Recipe("a"); recipe.levelingItem = true;
        recipe.materials.Add(new() { amount = 2.5f });
        var prefab = new UnityEngine.GameObject();
        prefab.Components[typeof(Behaviour.Equipment.Builder.EquipmentBuilder)] = new Behaviour.Equipment.Builder.EquipmentBuilder { identifier = "template" };
        recipe.results.Add(new() { item = prefab }); Forge.current = new Forge { recipes = new[] { recipe } };
        var row = Assert.Single(Source().Read(Guid.NewGuid(), false).Recipes);
        Assert.Equal(2, row.Outputs.Count); Assert.True(row.OutputLevelDependsOnPlayer);
        Assert.True(Assert.Single(row.Inputs).LevelScaled);
        Assert.Equal(RecipeResourceKind.EquipmentTemplate, row.Outputs[1].Resource.Kind);
        Assert.Equal("recipe", row.DisplayName);
    }
    [Fact]
    public void IncludesRefinableItemsAndFractionalMultipleOutputs()
    {
        Forge.current = new Forge(); var item = new InventoryItemType();
        item.gameObject.Components[typeof(OreItemData)] = new OreItemData { contents = new() { new() { yield = .25f }, new() { product = global::Source.Item.RefinedMaterial.TestGas, yield = .5f } } };
        InventoryItemType.all = new[] { item };
        var row = Assert.Single(Source().Read(Guid.NewGuid(), false).Recipes);
        Assert.Equal(RecipeProcess.Refining, row.Process); Assert.Equal(2, row.Outputs.Count); Assert.Equal(.25, row.Outputs[0].Amount);
    }
    [Fact]
    public void RejectsConflictingNativeIdentityButCoalescesRepeatedSameObject()
    {
        var a = Recipe("a"); Forge.current = new Forge { recipes = new[] { a, a } };
        Assert.Single(Source().Read(Guid.NewGuid(), false).Recipes);
        Forge.current.recipes = new[] { a, Recipe("a") };
        Assert.Throws<InvalidOperationException>(() => Source().Read(Guid.NewGuid(), false));
    }
    [Theory]
    [InlineData("", false)] [InlineData(" ", false)] [InlineData("", true)] [InlineData(" \t", true)]
    public void MissingNativeRecipeOrParentIdentityCannotBecomePrefixedIdentity(string identity, bool parent)
    {
        var recipe = Recipe(parent ? "valid" : identity);
        if (parent) recipe.parentRecipe = Recipe(identity);
        Forge.current = new Forge { recipes = new[] { recipe } };
        using var hub = new VGModAPI.Core.LifecycleHub((_, _) => { });
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        using var service = new VGModAPI.Core.RecipeCatalogService(hub, Source(), _ => { });
        var result = service.Read();
        Assert.Equal(RecipeCatalogStatus.NativeFailure, result.Status); Assert.Empty(result.Recipes);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ResourceRowOverflowReportsLimitExceededThroughService(bool inputs)
    {
        var recipe = Recipe("large");
        if (inputs)
            for (var i = 0; i < 257; i++) recipe.materials.Add(new() { amount = 1 });
        else
            for (var i = 0; i < 256; i++) recipe.results.Add(recipe.results[0]);
        Forge.current = new Forge { recipes = new[] { recipe } };
        using var hub = new VGModAPI.Core.LifecycleHub((_, _) => { });
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        using var service = new VGModAPI.Core.RecipeCatalogService(hub, Source(), _ => { });
        var result = service.Read();
        Assert.Equal(RecipeCatalogStatus.LimitExceeded, result.Status); Assert.Empty(result.Recipes);
    }
    [Fact]
    public void MissingOutputComponentIsExplicitUnsupported()
    {
        var recipe = Recipe("bad"); recipe.results[0].item = new UnityEngine.GameObject(); Forge.current = new Forge { recipes = new[] { recipe } };
        var row = Assert.Single(Source().Read(Guid.NewGuid(), false).Recipes);
        Assert.Equal(RecipeAvailability.Unsupported, row.Availability); Assert.Empty(row.Outputs);
    }
}
