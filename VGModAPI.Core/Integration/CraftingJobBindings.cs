using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class CraftingJobBindings
{
    private const string Forge = "Source.Mining.Forge", Refinery = "Source.Mining.Refinery";
    private const string ForgeJob = "Source.Mining.ForgeJob", RefineryJob = "Source.Mining.RefineryJob";
    private const string Item = "Behaviour.Item.InventoryItemType", Ore = "Behaviour.Mining.OreItemData", Recipe = "Behaviour.Crafting.CraftingRecipe";
    private const string Inventory = "Source.Item.Inventory", Player = "Source.Player.GamePlayer";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Refinery, "spaceStation", "Source.Galaxy.POI.SpaceStation", false, true),
        (ForgeJob, "parent", Forge, false, true), (RefineryJob, "parent", Refinery, false, true),
        (ForgeJob, "recipe", Recipe, false, false), (RefineryJob, "ore", Ore, false, false),
        (ForgeJob, "initialAmount", "System.Int32", false, false), (ForgeJob, "remainingAmount", "System.Int32", false, false),
        (RefineryJob, "initialAmount", "System.Int32", false, false), (RefineryJob, "remainingAmount", "System.Int32", false, false),
        (ForgeJob, "craftedLevel", "System.Int32", false, true),
        (ForgeJob, "craftingTime", "System.Single", false, false), (RefineryJob, "refineTime", "System.Single", false, false),
        (ForgeJob, "jobProgress", "System.Single", false, false), (RefineryJob, "jobProgress", "System.Single", false, false),
        ("Behaviour.Item.InventoryItemPart", "item", Item, false, false),
        (Inventory + "/InventoryItem", "inventory", Inventory, false, true),
        (Item, "itemLevel", "System.Int32", false, false), (Item, "rarity", "Source.Item.Rarity", false, false)
    };
    internal static readonly MethodBinding[] Hooks =
    {
        new("jobQueueForge", Forge, "StartJob", false, "System.Boolean", Recipe, "System.Int32"),
        new("jobQueueRefinery", Refinery, "StartJob", false, "System.Boolean", Ore, "System.Int32", "System.Boolean"),
        new("jobCancelForge", Forge, "CancelJob", false, "System.Void", ForgeJob),
        new("jobCancelRefinery", Refinery, "CancelJob", false, "System.Void", RefineryJob),
        new("jobProgressForge", Forge, "ProgressJobs", false, "System.Void", "System.Single"),
        new("jobProgressRefinery", Refinery, "ProgressJobs", false, "System.Void", "System.Single"),
        new("jobBatchForge", ForgeJob, "CompleteOneItem", false, "System.Void"),
        new("jobBatchRefinery", RefineryJob, "CompleteOneItem", false, "System.Void"),
        new("jobRouteForge", ForgeJob, "AddForgeItemToCargo", false, "System.Void", Item, "System.Int32"),
        new("jobRouteRefinery", RefineryJob, "DepositCrystalReward", false, "System.Void", Item),
        new("jobInventoryAdd", Inventory, "Add", false, Inventory + "/InventoryItem", Item, "System.Int32", "System.Boolean", "System.Boolean"),
        new("jobMaterialAdd", Player, "AddRefinedMaterial", false, "System.Void", "Source.Item.RefinedMaterial", "System.Single")
    };
    internal static readonly MethodBinding[] Persistence =
    {
        new("jobForgeSave", ForgeJob, "ToJson", false, "LightJson.JsonValue"),
        new("jobForgeRestore", ForgeJob, "FromJson", true, ForgeJob, Forge, "LightJson.JsonValue"),
        new("jobRefinerySave", RefineryJob, "ToJson", false, "LightJson.JsonValue"),
        new("jobRefineryRestore", RefineryJob, "FromJson", true, RefineryJob, Refinery, "LightJson.JsonValue")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeQuoteBindings.Validate(assembly);
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Hooks.Concat(Persistence));
    }
}
