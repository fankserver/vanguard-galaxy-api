using System;
using System.Collections.Generic;
using VGModAPI;
using Xunit;

namespace VGModAPI.Tests;

public sealed class RecipeCatalogTests
{
    private static readonly RecipeResourceId Item = new("vanilla", "Component", RecipeResourceKind.Item);
    private static RecipeSnapshot Recipe(string id, RecipeAvailability available = RecipeAvailability.Available, string name = "Same name") =>
        new(new RecipeId("vanilla", id), null, name, RecipeProcess.Forge, available, "", Array.Empty<RecipeResourceAmount>(),
            new[] { new RecipeResourceAmount(Item, 2) });

    [Fact]
    public void ProducerLookupReturnsAllVariantsWithoutUsingName()
    {
        var snapshot = new RecipeCatalogSnapshot(RecipeCatalogStatus.Available, Guid.NewGuid(), "",
            new[] { Recipe("a"), Recipe("b", name: "Different name"), Recipe("locked", RecipeAvailability.Locked) });
        Assert.Equal(2, snapshot.FindProducers(Item).Count);
        Assert.Equal(3, snapshot.FindProducers(Item, true).Count);
        Assert.Empty(snapshot.FindProducers(new RecipeResourceId("another-mod", "Component", RecipeResourceKind.Item)));
        Assert.Empty(snapshot.FindProducers(new RecipeResourceId("vanilla", "Component", RecipeResourceKind.ItemTemplate)));
    }

    [Fact]
    public void SnapshotCopiesCollectionsAndRejectsDuplicateIdentities()
    {
        var rows = new List<RecipeSnapshot> { Recipe("a") };
        var snapshot = new RecipeCatalogSnapshot(RecipeCatalogStatus.Available, null, "", rows);
        rows.Clear(); Assert.Single(snapshot.Recipes);
        Assert.Throws<ArgumentException>(() => new RecipeCatalogSnapshot(RecipeCatalogStatus.Available, null, "", new[] { Recipe("a"), Recipe("a") }));
        Assert.Throws<ArgumentException>(() => new RecipeCatalogSnapshot(RecipeCatalogStatus.NativeFailure, null, "", snapshot.Recipes));
    }

    [Fact]
    public void FractionalMaterialsAndMultiOutputArePreserved()
    {
        var outputs = new[] { new RecipeResourceAmount(Item, 5), new RecipeResourceAmount(new RecipeResourceId("vanilla", "Iron", RecipeResourceKind.RefinedMaterial), .25, conditional: true) };
        var recipe = new RecipeSnapshot(new RecipeId("test", "multi"), null, "Multi", RecipeProcess.Refining,
            RecipeAvailability.Available, "", Array.Empty<RecipeResourceAmount>(), outputs);
        Assert.Equal(2, recipe.Outputs.Count); Assert.Equal(.25, recipe.Outputs[1].Amount); Assert.True(recipe.Outputs[1].Conditional);
        Assert.Throws<ArgumentException>(() => new RecipeResourceAmount(Item, .5));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)] [InlineData(0)] [InlineData(2147483648d)]
    public void QuantitiesAreBounded(double amount) => Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeResourceAmount(Item, amount));

    [Fact]
    public void IdentityIsScopedAndOrdinal()
    {
        Assert.Equal(new RecipeId("a", "x"), new RecipeId("a", "x"));
        Assert.NotEqual(new RecipeId("a", "x"), new RecipeId("b", "x"));
        Assert.NotEqual(new RecipeId("a", "x"), new RecipeId("a", "X"));
        Assert.Throws<ArgumentException>(() => new RecipeId("a", "bad\nidentity"));
    }

    [Fact]
    public void CyclicRelationshipsDoNotRecursivelyExpand()
    {
        var a = new RecipeResourceId("vanilla", "a", RecipeResourceKind.Item);
        var b = new RecipeResourceId("vanilla", "b", RecipeResourceKind.Item);
        var recipes = new[] {
            new RecipeSnapshot(new RecipeId("vanilla", "a"), null, "A", RecipeProcess.Forge, RecipeAvailability.Available, "", new[] { new RecipeResourceAmount(b, 1) }, new[] { new RecipeResourceAmount(a, 1) }),
            new RecipeSnapshot(new RecipeId("vanilla", "b"), null, "B", RecipeProcess.Forge, RecipeAvailability.Available, "", new[] { new RecipeResourceAmount(a, 1) }, new[] { new RecipeResourceAmount(b, 1) }) };
        var snapshot = new RecipeCatalogSnapshot(RecipeCatalogStatus.Available, null, "", recipes);
        Assert.Single(snapshot.FindProducers(a)); Assert.Single(snapshot.FindProducers(b));
    }
}
