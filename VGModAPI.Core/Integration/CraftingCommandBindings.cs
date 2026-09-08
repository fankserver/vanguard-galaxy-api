using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class CraftingCommandBindings
{
    private const string Item = "Behaviour.Item.InventoryItemType", Forge = "Source.Mining.Forge", Refinery = "Source.Mining.Refinery";
    private const string Player = "Source.Player.GamePlayer", Recipe = "Behaviour.Crafting.CraftingRecipe", Ore = "Behaviour.Mining.OreItemData";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Refinery, "cargoAccessible", "System.Boolean", false, false), (Refinery, "autoRefine", "System.Boolean", false, true),
        (Refinery, "autoSell", "System.Boolean", true, true),
        (Item, "itemBuilder", "Behaviour.Item.Builder.ItemBuilder", false, false),
        (Item, "equipmentBuilder", "Behaviour.Equipment.Builder.EquipmentBuilder", false, false),
        ("Behaviour.UI.Forge.ForgeUI", "current", "Behaviour.UI.Forge.ForgeUI", true, false),
        ("Behaviour.UI.Forge.ForgeUI", "cargoToggle", "UnityEngine.UI.Toggle", false, true),
        ("Behaviour.UI.Refinery.RefineryUI", "current", "Behaviour.UI.Refinery.RefineryUI", true, false),
        ("Behaviour.UI.Refinery.RefineryUI", "contents", "Behaviour.UI.Refinery.RefineryJobTabContents", false, true),
        ("Behaviour.UI.Refinery.RefineryUI", "settings", "Behaviour.UI.Refinery.RefinerySettingsTab", false, true),
        ("Behaviour.UI.Refinery.RefinerySettingsTab", "autoRefine", "UnityEngine.UI.Toggle", false, true),
        ("Behaviour.UI.Refinery.RefinerySettingsTab", "autoRefineOptionShipCargo", "UnityEngine.UI.Toggle", false, true),
        ("Behaviour.UI.Refinery.RefinerySettingsTab", "autoSell", "UnityEngine.UI.Toggle", false, true)
    };
    internal static readonly MethodBinding[] Actions =
    {
        new("commandForgeQueue", Forge, "TryStartJob", false, "System.Boolean", Recipe, "System.Int32"),
        new("commandRefineryQueue", Refinery, "TryStartJob", false, "System.Boolean", Ore, "System.Int32"),
        new("commandExtract", Refinery, "ExtractMaterial", false, "System.Void", "Source.Item.RefinedMaterial", "System.Int32"),
        new("commandCanStack", Item, "CanStackWith", false, "System.Boolean", Item),
        new("commandDataItem", Item, "CanGoInDataInventory", false, "System.Boolean"),
        new("commandReadFlag", "Source.Player.Register", "HasFlag", true, "System.Boolean", "System.String", "System.Boolean"),
        new("commandWriteFlag", "Source.Player.Register", "SetFlag", true, "System.Void", "System.String", "System.Boolean"),
        new("commandRefreshJobs", "Behaviour.UI.Spacestation.SpaceStationInterior", "UpdateJobs", false, "System.Void"),
        new("commandRefreshForge", "Behaviour.UI.Forge.ForgeUI", "UpdateContent", false, "System.Void"),
        new("commandRefreshRefinery", "Behaviour.UI.Refinery.RefineryUI", "UpdateContent", false, "System.Void")
    };
    internal static readonly MethodBinding[] Serialization =
    {
        new("commandPlayerSerialize", Player, "ToJson", false, "LightJson.JsonValue"),
        new("commandForgeSerialize", Forge, "ToJson", false, "LightJson.JsonValue"),
        new("commandRefinerySerialize", Refinery, "ToJson", false, "LightJson.JsonValue"),
        new("commandForgeJobSerialize", "Source.Mining.ForgeJob", "ToJson", false, "LightJson.JsonValue"),
        new("commandRefineryJobSerialize", "Source.Mining.RefineryJob", "ToJson", false, "LightJson.JsonValue"),
        new("commandForgeRestore", Forge, "FromJson", true, Forge, "Source.Galaxy.POI.SpaceStation", "LightJson.JsonValue"),
        new("commandRefineryRestore", Refinery, "FromJson", true, Refinery, "Source.Galaxy.POI.SpaceStation", "LightJson.JsonValue"),
        new("commandForgeJobRestore", "Source.Mining.ForgeJob", "FromJson", true, "Source.Mining.ForgeJob", Forge, "LightJson.JsonValue"),
        new("commandRefineryJobRestore", "Source.Mining.RefineryJob", "FromJson", true, "Source.Mining.RefineryJob", Refinery, "LightJson.JsonValue")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        _ = CraftingJobBindings.Validate(assembly);
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Actions.Concat(Serialization));
    }
}
