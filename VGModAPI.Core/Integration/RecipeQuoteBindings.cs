using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class RecipeQuoteBindings
{
    private const string Recipe = "Behaviour.Crafting.CraftingRecipe";
    private const string Forge = "Source.Mining.Forge";
    private const string Refinery = "Source.Mining.Refinery";
    private const string Player = "Source.Player.GamePlayer";
    private const string Item = "Behaviour.Item.InventoryItemType";
    private const string Inventory = "Source.Item.Inventory";
    private const string Station = "Source.Galaxy.POI.SpaceStation";
    private const string Ore = "Behaviour.Mining.OreItemData";
    private const string Skill = "Behaviour.Crew.SkilltreeNode";
    private const string Material = "Source.Item.RefinedMaterial";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Forge, "spaceStation", Station, false, true),
        (Forge, "jobs", "System.Collections.Generic.List`1<Source.Mining.ForgeJob>", false, true),
        (Forge, "maxJobs", "System.Int32", false, false), (Forge, "craftingSpeed", "System.Single", false, false),
        (Station, "forge", Forge, false, true), (Station, "refinery", Refinery, false, true),
        (Station, "materialStorage", Inventory, false, true),
        ("Source.Galaxy.MapPointOfInterest", "current", "Source.Galaxy.MapPointOfInterest", true, false),
        ("Source.Galaxy.MapElement", "name", "System.String", false, false),
        ("Source.Galaxy.MapElement", "guid", "System.String", false, false),
        ("Source.Galaxy.GalaxyMapData", "current", "Source.Galaxy.GalaxyMapData", true, false),
        (Player, "current", Player, true, true), (Player, "credits", "System.Int64", false, false),
        (Player, "currentSpaceShip", "Source.SpaceShip.SpaceShipData", false, false),
        (Player, "forgeDepositInCargo", "System.Boolean", false, true),
        ("Behaviour.Equipment.Builder.EquipmentBuilder", "prefab", Item, false, false),
        ("Behaviour.Item.Builder.ItemBuilder", "prefab", Item, false, false),
        (Item, "m3", "System.Single", false, false),
        (Player, "globalInventory", Inventory, false, false), (Player, "dataInventory", Inventory, false, false),
        ("Source.Data.AbstractUnitData", "cargo", Inventory, false, true),
        ("Behaviour.UI.Spacestation.SpaceStationInterior", "instance", "Behaviour.UI.Spacestation.SpaceStationInterior", true, true),
        (Recipe, "customCost", "System.Int32", false, true), (Recipe, "dynamicCost", "System.Int32", false, true),
        (Item, "calcCost", "System.Single", false, true),
        (Recipe, "craftingCost", "System.Int32", false, false), (Recipe, "craftingTime", "System.Single", false, false),
        (Refinery, "jobs", "System.Collections.Generic.List`1<Source.Mining.RefineryJob>", false, true),
        (Refinery, "maxJobs", "System.Int32", false, false),
        (Ore, "refinementCost", "System.Int32", false, false), (Ore, "refinementTime", "System.Single", false, false),
        (Ore, "ignoreExtraRewards", "System.Boolean", false, false), (Ore, "disableAutoRefine", "System.Boolean", false, false),
        (Item, "itemCategory", "Source.Item.ItemCategory", false, false),
        (Inventory, "items", "System.Collections.Generic.IEnumerable`1<Source.Item.Inventory/InventoryItem>", false, false),
        (Inventory + "/InventoryItem", "item", Item, false, true), (Inventory + "/InventoryItem", "count", "System.Int32", false, false),
        (Inventory + "/InventoryItem", "favourite", "System.Boolean", false, true),
        (Skill, "industrialForgeBonusCraft", Skill, true, false), (Skill, "industrialRefBonusCraft1", Skill, true, false),
        (Skill, "industrialCrystalRefineChance", Skill, true, false),
        (Skill, "currentIncrease", "System.Single", false, false), (Skill, "isActive", "System.Boolean", false, false)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("quoteStation", "Source.Galaxy.GalaxyMapData", "GetPointOfInterest", false, "Source.Galaxy.MapPointOfInterest", "System.String"),
        new("quoteLevel", Recipe, "GetAdjustedOutputLevel", false, "System.Int32"),
        new("quoteMaterials", Recipe, "GetIngredientMaterials", false, "System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<Source.Item.RefinedMaterial,System.Single>>", "System.Int32"),
        new("quoteItems", Recipe, "GetIngredientItems", false, "System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<Behaviour.Item.InventoryItemType,System.Int32>>", "System.Int32"),
        new("quoteCredits", Player, "CanAfford", false, "System.Boolean", "System.Single"),
        new("quoteReserved", Player, "RequiredItemCountForMissions", false, "System.Int32", Item),
        new("quoteMaterialBalance", Player, "CountRefinedMaterial", false, "System.Single", Material),
        new("quoteItemBalance", Inventory, "GetCount", false, "System.Int32", Item),
        new("quoteArmory", Item, "CanGoInArmory", false, "System.Boolean"),
        new("quoteMaterialsRoute", Item, "CanGoInMaterials", false, "System.Boolean"),
        new("quoteCargoSpace", Inventory, "IsFull", false, "System.Boolean", "System.Single"),
        new("quoteExtractionCost", Refinery, "GetExtractCost", true, "System.Int32", Material, "System.Int32")
    };
    internal static void Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        _ = new GameBindings(assembly).Resolve(Methods);
    }
}
