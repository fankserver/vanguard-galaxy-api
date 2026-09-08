using System;
using System.Collections.Generic;

// Synthetic metadata-shaped objects: no game implementation or Unity execution.
namespace UnityEngine
{
    public sealed class GameObject
    {
        public Dictionary<Type, object> Components = new();
    }
}
namespace Source.Item
{
    public enum RefinedMaterial { TestMetal, TestGas }
    public enum Rarity { Common, Rare }
    public enum ItemCategory { Ore, Material, Module }
}
namespace Behaviour.Item
{
    public sealed partial class InventoryItemType
    {
        public static IEnumerable<InventoryItemType> all { get; set; } = Array.Empty<InventoryItemType>();
        public string identifier { get; set; } = "item";
        public string displayName { get; set; } = "@item";
        public UnityEngine.GameObject gameObject { get; set; } = new();
    }
}
namespace Behaviour.Item.Builder
{
    public sealed class ItemBuilder { public string identifier { get; set; } = "builder"; public Behaviour.Item.InventoryItemType prefab { get; set; } = new(); }
}
namespace Behaviour.Equipment.Builder
{
    public sealed class EquipmentBuilder { public string identifier { get; set; } = "equipment"; public Behaviour.Item.InventoryItemType prefab { get; set; } = new(); }
}
namespace Behaviour.Mining
{
    public sealed class OreRefinementProduct
    {
        public Source.Item.RefinedMaterial product { get; set; }
        public float yield { get; set; } = .5f;
    }
    public sealed partial class OreItemData { public List<OreRefinementProduct> contents = new(); }
}
namespace Behaviour.Crafting
{
    public sealed partial class CraftingRecipe
    {
        public sealed class CraftingRecipeMaterialRow
        {
            public Source.Item.RefinedMaterial material { get; set; }
            public float amount { get; set; } = 1;
        }
        public sealed class CraftingRecipeItemRow
        {
            public UnityEngine.GameObject? item { get; set; }
            public int count { get; set; } = 1;
        }
        public List<CraftingRecipeMaterialRow> materials = new();
        public List<CraftingRecipeItemRow> itemMaterials = new();
        public List<CraftingRecipeItemRow> results = new();
        public bool levelingItem = false;
        public static IEnumerable<CraftingRecipe> all { get; set; } = Array.Empty<CraftingRecipe>();
        public string identifier { get; set; } = "recipe";
        public string displayName { get; set; } = "@recipe";
        public CraftingRecipe? parentRecipe { get; set; }
        public List<CraftingRecipe> subRecipes = new();
        public Source.Item.Rarity craftingRarity { get; set; }
    }
}
namespace Source.Mining
{
    public sealed partial class Forge
    {
        private static Forge? _current;
        public static Forge? current
        {
            get => _current;
            set { _current = value; if (value != null) value.spaceStation.forge = value; }
        }
        public IEnumerable<Behaviour.Crafting.CraftingRecipe> recipes { get; set; } = Array.Empty<Behaviour.Crafting.CraftingRecipe>();
    }
}
