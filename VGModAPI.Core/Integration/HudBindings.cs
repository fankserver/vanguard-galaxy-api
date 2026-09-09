using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class HudBindings
{
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        ("Behaviour.UI.Side_Menu.SidePanel", "instance", "Behaviour.UI.Side_Menu.SidePanel", true, true),
        ("Behaviour.Item.InventoryItemType", "icon", "UnityEngine.Sprite", false, false),
        ("Behaviour.Item.InventoryItemType", "displayName", "System.String", false, false),
        ("Behaviour.Item.InventoryItemType", "rarity", "Source.Item.Rarity", false, false),
        ("Behaviour.Crafting.CraftingRecipe", "icon", "UnityEngine.Sprite", false, false),
        ("Behaviour.UI.Tooltip.UITooltipParent", "ItemTooltipPrefab", "Behaviour.UI.UITooltip", true, false)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("hudMaterialIcon", "Source.Item.RefinedMaterialExtensions", "GetIcon", true, "UnityEngine.Sprite", "Source.Item.RefinedMaterial"),
        new("hudMaterialName", "Source.Item.RefinedMaterialExtensions", "GetDisplayName", true, "System.String", "Source.Item.RefinedMaterial"),
        new("hudRarityColor", "Source.Util.RarityExtensions", "GetColor", true, "UnityEngine.Color", "Source.Item.Rarity"),
        new("hudTranslate", "Source.Util.Translation", "Translate", true, "System.String", "System.String", "System.Object[]"),
        new("hudItemTooltip", "Behaviour.UI.Tooltip.ItemTooltipSource", "SetItem", false, "System.Void", "Behaviour.Item.InventoryItemType", "System.Int32", "System.Boolean", "Behaviour.UI.Tooltip.ItemTooltipContext", "System.Boolean", "Source.Item.Inventory/InventoryItem")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly); RecipeCatalogBindings.Validate(assembly, Members);
        _ = assembly.GetType("Behaviour.UI.Side_Menu.SideTabs.CargoIndicator", true);
        return new GameBindings(assembly).Resolve(Methods);
    }
}
