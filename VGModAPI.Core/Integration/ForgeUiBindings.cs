using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class ForgeUiBindings
{
    private const string Ui = "Behaviour.UI.Forge.ForgeUI", Contents = "Behaviour.UI.Forge.ForgeTabContents";
    private const string Recipe = "Behaviour.Crafting.CraftingRecipe", Interior = "Behaviour.UI.Spacestation.SpaceStationInterior";
    private const string Recipes = "System.Collections.Generic.List`1<Behaviour.Crafting.CraftingRecipe>";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Ui, "current", Ui, true, false), (Ui, "tabContents", Contents, false, true), (Ui, "preselectRecipe", Recipe, true, true),
        (Contents, "parentRecipe", Recipe, false, false), (Contents, "subRecipe", Recipe, false, false),
        (Contents, "unlockedRecipes", Recipes, false, false), (Contents, "recipeIcon", "UnityEngine.UI.Image", false, true),
        (Contents, "countSlider", "UnityEngine.UI.Slider", false, true), (Contents, "costText", "TMPro.TMP_Text", false, true),
        (Interior, "spacestation", "Source.Galaxy.POI.SpaceStation", false, false),
        (Interior, "tabParent", "UnityEngine.RectTransform", false, true),
        (Interior, "currentTab", "Source.Galaxy.POI.SpaceStationFacility", false, false),
        (Interior, "tabActions", "System.Collections.Generic.Dictionary`2<Source.Galaxy.POI.SpaceStationFacility,System.Action>", false, true)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("forgeUiAwake", Ui, "Awake", false, "System.Void"),
        new("forgeUiSelect", Ui, "SelectRecipe", false, "System.Void", Recipe, Recipes, Recipe),
        new("forgeUiSelected", Contents, "SetSelectedRecipe", false, "System.Boolean", Recipe, Recipe, Recipes),
        new("forgeUiSlider", Contents, "UpdateSlider", false, "System.Void"),
        new("forgeUiText", Contents, "UpdateTextValue", false, "System.Void"),
        new("forgeUiLocation", Interior, "GoToLocation", false, "System.Void", "Source.Galaxy.POI.SpaceStationFacility", "System.Boolean")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeQuoteBindings.Validate(assembly);
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}
