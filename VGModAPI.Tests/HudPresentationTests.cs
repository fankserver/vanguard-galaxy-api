using System;
using System.Collections.Generic;
using System.Reflection;
using Behaviour.Crafting;
using Behaviour.Item;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests
{
    public sealed class HudPresentationTests
    {
        private readonly HudPresentationSource _source = new(typeof(CraftingRecipe).Assembly, new Dictionary<string, MethodInfo>
        { ["hudTranslate"] = typeof(HudPresentationTests).GetMethod(nameof(Translate))! });
        public static string Translate(string text, object[] args) => "localized " + text;
        [Fact]
        public void ItemPresentationCopiesNameAndRetainsTooltipItemOnlyInternally()
        {
            var item = new InventoryItemType { identifier = "item", displayName = "Item" }; InventoryItemType.all = new[] { item, item };
            var value = _source.Resolve(new("vanilla", "item", HudPresentationKind.Item));
            Assert.Equal("localized Item", value.Name); Assert.Same(item.icon, value.Icon); Assert.Same(item, value.TooltipItem);
        }
        [Fact]
        public void RecipeIconIsAnExplicitPreviewBearingPresentationOperation()
        {
            var recipe = new CraftingRecipe { identifier = "recipe" }; recipe.subRecipes.Add(recipe); CraftingRecipe.all = new[] { recipe };
            Assert.Equal(0, recipe.IconReads);
            var value = _source.Resolve(new("vanilla", "forge/recipe", HudPresentationKind.ForgeRecipe));
            Assert.Equal(1, recipe.IconReads); Assert.NotNull(value.Icon); Assert.Null(value.TooltipItem);
        }
        [Fact]
        public void MissingOrConflictingIdentityHasNoAssetOrTooltip()
        {
            InventoryItemType.all = new[] { new InventoryItemType { identifier = "same" }, new InventoryItemType { identifier = "same" } };
            var value = _source.Resolve(new("vanilla", "same", HudPresentationKind.Item));
            Assert.Null(value.Icon); Assert.Null(value.TooltipItem); Assert.Contains("unavailable", value.Name);
            Assert.Null(_source.Resolve(new("other", "same", HudPresentationKind.Item)).Icon);
        }
        [Fact]
        public void NumericMaterialAliasesAreNotCanonicalPresentationIdentities()
        {
            var value = _source.Resolve(new("vanilla", "0", HudPresentationKind.RefinedMaterial));
            Assert.Null(value.Icon); Assert.Contains("unavailable", value.Name);
        }
    }
}
namespace Behaviour.Item
{
    public sealed partial class InventoryItemType { public object icon { get; } = new(); }
}
namespace Behaviour.Crafting
{
    public sealed partial class CraftingRecipe
    {
        public int IconReads;
        public object icon { get { IconReads++; return this; } }
    }
}
