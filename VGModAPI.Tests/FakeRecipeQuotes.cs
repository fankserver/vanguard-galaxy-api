using System;
using System.Collections.Generic;
using System.Linq;

namespace Source.Item
{
    public sealed class Inventory
    {
        public sealed class InventoryItem
        {
            public Behaviour.Item.InventoryItemType item = new();
            public Inventory? inventory;
            public int count { get; set; }
            public bool favourite;
        }
        public IEnumerable<InventoryItem> items { get; set; } = Array.Empty<InventoryItem>();
        public float Capacity = 1000;
        public int GetCount(Behaviour.Item.InventoryItemType item) => items.Where(stack => ReferenceEquals(stack.item, item)).Sum(stack => stack.count);
        public bool IsFull(float space) => space > Capacity;
    }
}
namespace Source.Player
{
    public sealed partial class GamePlayer
    {
        public Source.Item.Inventory globalInventory { get; set; } = new();
        public Source.Item.Inventory dataInventory { get; set; } = new();
        public bool forgeDepositInCargo = true;
        public int Reserved;
        public float Materials = 100;
        public bool CanAfford(float amount) => credits >= (long)amount;
        public int RequiredItemCountForMissions(Behaviour.Item.InventoryItemType item) => Reserved;
        public float CountRefinedMaterial(Source.Item.RefinedMaterial material) => Materials;
    }
}
namespace Source.SpaceShip
{
    public sealed partial class SpaceShipData { public Source.Item.Inventory cargo = new(); }
}
namespace Source.Galaxy
{
    public partial class MapPointOfInterest { public static MapPointOfInterest? current => Source.Player.GamePlayer.current?.currentPointOfInterest; }
}
namespace Source.Galaxy.POI
{
    public partial class SpaceStation
    {
        public static SpaceStation? TestCurrent;
        public new static SpaceStation? current => TestCurrent ?? Source.Mining.Forge.current?.spaceStation;
        public Source.Mining.Forge? forge;
        public Source.Mining.Refinery refinery = new();
        public Source.Item.Inventory materialStorage = new();
    }
    public sealed class IndustryStation : SpaceStation { }
}
namespace Source.Mining
{
    public sealed partial class Forge
    {
        public Source.Galaxy.POI.SpaceStation spaceStation = new();
        public List<object> jobs = new();
        public int maxJobs { get; set; } = 2;
        public float craftingSpeed { get; set; } = 1;
    }
    public sealed class Refinery
    {
        public Source.Galaxy.POI.SpaceStation spaceStation = null!;
        public List<object> jobs = new();
        public int maxJobs { get; set; } = 2;
        public static int GetExtractCost(Source.Item.RefinedMaterial material, int count) => count * 2;
    }
}
namespace Behaviour.Crafting
{
    public sealed partial class CraftingRecipe
    {
        public int TestLevel = 10;
        public float TestScale = 1;
        public int customCost = 10;
        public int dynamicCost = -1;
        public int craftingCost
        {
            get
            {
                if (customCost > 0) return customCost;
                if (dynamicCost < 0)
                    dynamicCost = itemMaterials.Sum(row => ((Behaviour.Item.InventoryItemType)row.item!.Components[typeof(Behaviour.Item.InventoryItemType)]).cost);
                return dynamicCost;
            }
            set => customCost = value;
        }
        public float craftingTime { get; set; } = 5;
        public int GetAdjustedOutputLevel() => TestLevel;
        public IEnumerable<(Source.Item.RefinedMaterial, float)> GetIngredientMaterials(int level) => materials.Select(row => (row.material, row.amount * TestScale));
        public IEnumerable<(Behaviour.Item.InventoryItemType, int)> GetIngredientItems(int level) => itemMaterials.Select(row =>
            ((Behaviour.Item.InventoryItemType)row.item!.Components[typeof(Behaviour.Item.InventoryItemType)], (int)Math.Round(row.count * TestScale)));
    }
}
namespace Behaviour.Item
{
    public sealed partial class InventoryItemType
    {
        public Source.Item.ItemCategory itemCategory { get; set; } = Source.Item.ItemCategory.Material;
        public float m3 { get; set; } = 1;
        public int itemLevel { get; set; } = 1;
        public Source.Item.Rarity rarity { get; set; }
        public float calcCost = 100;
        public int PreviewBuilderCalls { get; private set; }
        public int cost
        {
            get
            {
                if (calcCost < 0) { PreviewBuilderCalls++; calcCost = 77; }
                return (int)calcCost;
            }
        }
        public bool CanGoInArmory() => itemCategory == Source.Item.ItemCategory.Module;
        public bool CanGoInMaterials() => itemCategory != Source.Item.ItemCategory.Module;
    }
}
namespace Behaviour.Mining
{
    public sealed partial class OreItemData
    {
        public Behaviour.Item.InventoryItemType item { get; set; } = new();
        public Behaviour.Item.InventoryItemType? PricingItem;
        private int _cost = 5;
        public int refinementCost
        {
            get { if (PricingItem != null) _ = PricingItem.cost; return _cost; }
            set => _cost = value;
        }
        public float refinementTime { get; set; } = 3;
        public bool ignoreExtraRewards { get; set; }
        public bool disableAutoRefine { get; set; }
    }
}
namespace Behaviour.Crew
{
    public sealed class SkilltreeNode
    {
        public static SkilltreeNode industrialForgeBonusCraft { get; set; } = new();
        public static SkilltreeNode industrialRefBonusCraft1 { get; set; } = new();
        public static SkilltreeNode industrialCrystalRefineChance { get; set; } = new();
        public float currentIncrease { get; set; }
        public bool isActive { get; set; }
    }
}
