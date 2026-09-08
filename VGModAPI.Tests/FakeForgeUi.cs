using System;
using System.Collections.Generic;
using Behaviour.Crafting;

namespace Source.Galaxy.POI
{
    public enum SpaceStationFacility { Airlock, Forge }
}
namespace Behaviour.UI.Spacestation
{
    public sealed partial class SpaceStationInterior
    {
        public Source.Galaxy.POI.SpaceStationFacility currentTab;
        public Dictionary<Source.Galaxy.POI.SpaceStationFacility, Action> tabActions = new();
        public void GoToLocation(Source.Galaxy.POI.SpaceStationFacility facility, bool automaticallyOpenTab)
        { tabActions[facility](); currentTab = facility; }
    }
}
namespace Behaviour.UI.Forge
{
    public sealed class ForgeUI
    {
        public static ForgeUI? current;
        public static CraftingRecipe? preselectRecipe;
        public ForgeTabContents tabContents = new();
        public int SelectCalls;
        public void SelectRecipe(CraftingRecipe parent, List<CraftingRecipe> group, CraftingRecipe selected)
        { SelectCalls++; tabContents.parentRecipe = parent; tabContents.subRecipe = selected; tabContents.unlockedRecipes = group; }
    }
    public sealed class ForgeTabContents
    {
        public CraftingRecipe? parentRecipe, subRecipe;
        public List<CraftingRecipe> unlockedRecipes = new();
        public Slider countSlider = new();
        public Icon recipeIcon = new();
        public sealed class Slider { public float value = 2; }
        public sealed class Icon { public object sprite = new(); }
    }
}
