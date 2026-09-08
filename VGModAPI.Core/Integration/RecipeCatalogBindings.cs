using System;
using System.Reflection;

namespace VGModAPI.Runtime;

internal static class RecipeCatalogBindings
{
    private const string Recipe = "Behaviour.Crafting.CraftingRecipe";
    private const string Item = "Behaviour.Item.InventoryItemType";
    private const string MaterialRow = Recipe + "/CraftingRecipeMaterialRow";
    private const string ItemRow = Recipe + "/CraftingRecipeItemRow";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Recipe, "materials", "System.Collections.Generic.List`1<" + MaterialRow + ">", false, true),
        (Recipe, "itemMaterials", "System.Collections.Generic.List`1<" + ItemRow + ">", false, true),
        (Recipe, "results", "System.Collections.Generic.List`1<" + ItemRow + ">", false, true),
        (Recipe, "levelingItem", "System.Boolean", false, true),
        (Recipe, "all", "System.Collections.Generic.IEnumerable`1<" + Recipe + ">", true, false),
        (Recipe, "identifier", "System.String", false, false),
        (Recipe, "displayName", "System.String", false, false),
        (Recipe, "parentRecipe", Recipe, false, false),
        (Recipe, "subRecipes", "System.Collections.Generic.List`1<" + Recipe + ">", false, true),
        (Recipe, "craftingRarity", "Source.Item.Rarity", false, false),
        (MaterialRow, "material", "Source.Item.RefinedMaterial", false, false),
        (MaterialRow, "amount", "System.Single", false, false),
        (ItemRow, "item", "UnityEngine.GameObject", false, false),
        (ItemRow, "count", "System.Int32", false, false),
        (Item, "all", "System.Collections.Generic.IEnumerable`1<" + Item + ">", true, false),
        (Item, "identifier", "System.String", false, false),
        (Item, "displayName", "System.String", false, false),
        ("Source.Mining.Forge", "current", "Source.Mining.Forge", true, false),
        ("Source.Mining.Forge", "recipes", "System.Collections.Generic.IEnumerable`1<" + Recipe + ">", false, false),
        ("Behaviour.Mining.OreItemData", "contents", "System.Collections.Generic.List`1<Behaviour.Mining.OreRefinementProduct>", false, true),
        ("Behaviour.Mining.OreRefinementProduct", "product", "Source.Item.RefinedMaterial", false, false),
        ("Behaviour.Mining.OreRefinementProduct", "yield", "System.Single", false, false),
        ("Behaviour.Equipment.Builder.EquipmentBuilder", "identifier", "System.String", false, false),
        ("Behaviour.Item.Builder.ItemBuilder", "identifier", "System.String", false, false)
    };
    internal static void Validate(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var spec in Members)
        {
            var type = assembly.GetType(spec.Type.Replace('/', '+'), true)!;
            if (spec.Field)
            {
                var field = type.GetField(spec.Member, flags);
                if (field == null || field.IsStatic != spec.Static || !NativeTypeName.Matches(field.FieldType, spec.Shape))
                    throw new MissingFieldException(spec.Type, spec.Member);
            }
            else
            {
                var property = type.GetProperty(spec.Member, flags);
                if (property?.GetMethod == null || property.GetMethod.IsStatic != spec.Static || property.GetIndexParameters().Length != 0 || !NativeTypeName.Matches(property.PropertyType, spec.Shape))
                    throw new MissingMemberException(spec.Type, spec.Member);
            }
        }
    }
}
