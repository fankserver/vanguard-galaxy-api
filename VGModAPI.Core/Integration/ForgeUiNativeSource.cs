using System;
using System.Collections;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource : IForgeUiSource
{
    private const string ForgeUiType = "Behaviour.UI.Forge.ForgeUI";
    private const string InteriorType = "Behaviour.UI.Spacestation.SpaceStationInterior";
    private object? _forgeViewObject;
    private ForgeViewHandle? _forgeViewHandle;
    private RecipeStationHandle? _forgeViewStation;
    internal Func<Guid?>? UiSession { get; set; }
    internal Func<bool>? UiDispatching { get; set; }
    internal Func<object, bool>? UiActive { get; set; }
    internal Func<object, object, bool>? UiBelongsTo { get; set; }
    internal Func<object, bool>? UiAssetAlive { get; set; }
    internal object? ForgeViewObject => _forgeViewObject;
    internal object? ForgeTabAnchor => _forgeViewHandle != null && UiStation(_forgeViewHandle.SessionId, out var interior) != null
        ? Get(interior!, "tabParent") : null;
    public void ClearUi() { _forgeViewObject = null; _forgeViewHandle = null; _forgeViewStation = null; }
    private object? UiStation(Guid session, out object? interior)
    {
        interior = null;
        if (UiSession?.Invoke() != session) return null;
        interior = GetStatic(InteriorType, "instance");
        var station = GetStatic("Source.Galaxy.POI.SpaceStation", "current");
        if (interior == null || UiActive?.Invoke(interior) != true || station == null || !ReferenceEquals(Get(interior, "spacestation"), station) || Get(station, "forge") == null)
            return null;
        return station;
    }
    public ForgeSelectionSnapshot? ReadUi(Guid session)
    {
        var station = UiStation(session, out var interior);
        var ui = GetStatic(ForgeUiType, "current");
        if (station == null || ui == null || UiActive?.Invoke(ui) != true || UiBelongsTo?.Invoke(ui, interior!) != true || Get(interior!, "currentTab")?.ToString() != "Forge")
        { ClearUi(); return null; }
        var contents = Get(ui, "tabContents")!;
        var parent = Get(contents, "parentRecipe"); var selected = Get(contents, "subRecipe");
        if (parent == null || selected == null) { ClearUi(); return null; }
        var available = UiRecipes(Get(Get(station, "forge")!, "recipes"));
        var group = available.Where(recipe => ReferenceEquals(Get(recipe, "parentRecipe"), parent)).ToArray();
        var shown = UiRecipes(Get(contents, "unlockedRecipes"));
        if (!group.Any(recipe => ReferenceEquals(recipe, selected)) || group.Length != shown.Length || group.Distinct(NativeObjectIdentity.Instance).Count() != group.Length ||
            !shown.All(recipe => group.Contains(recipe, NativeObjectIdentity.Instance))) { ClearUi(); return null; }
        var batches = Convert.ToDouble(Get(Get(contents, "countSlider")!, "value"));
        if (!Finite(batches) || batches < 0 || batches > int.MaxValue) throw new InvalidOperationException("Invalid selected Forge count.");
        var handle = IssueStation(session, station);
        if (handle == null) { ClearUi(); return null; }
        if (!ReferenceEquals(ui, _forgeViewObject) || _forgeViewStation?.Equals(handle) != true || _forgeViewHandle?.SessionId != session)
        { _forgeViewObject = ui; _forgeViewStation = handle; _forgeViewHandle = new(session, Guid.NewGuid()); }
        var sprite = Get(Get(contents, "recipeIcon")!, "sprite");
        var snapshot = new ForgeSelectionSnapshot(_forgeViewHandle!, handle, ForgeId(parent), ForgeId(selected), shown.Select(ForgeId),
            checked((int)Math.Round(batches, MidpointRounding.ToEven)), 0, new(_translate(Text(selected, "displayName")), sprite != null && UiAssetAlive?.Invoke(sprite) == true));
        if (UiSession?.Invoke() != session || !ReferenceEquals(GetStatic(ForgeUiType, "current"), ui)) { ClearUi(); return null; }
        return snapshot;
    }
    public ForgeNavigationStatus OpenUi(Guid session, RecipeId recipe)
    {
        var station = UiStation(session, out var interior);
        if (station == null) return ForgeNavigationStatus.NotAtStation;
        var facility = Enum.Parse(_assembly.GetType("Source.Galaxy.POI.SpaceStationFacility", true)!, "Forge");
        if (Get(interior!, "tabActions") is not IDictionary actions || !actions.Contains(facility)) return ForgeNavigationStatus.NotAtStation;
        if (recipe.ProviderId != "vanilla" || !recipe.LocalId.StartsWith("forge/", StringComparison.Ordinal)) return ForgeNavigationStatus.RecipeUnavailable;
        var available = UiRecipes(Get(Get(station, "forge")!, "recipes"));
        var matches = available.Where(item => ForgeId(item).Equals(recipe)).ToArray();
        if (matches.Length != 1) return ForgeNavigationStatus.RecipeUnavailable;
        var selected = matches[0]; var parent = Get(selected, "parentRecipe")!;
        var group = available.Where(item => ReferenceEquals(Get(item, "parentRecipe"), parent)).ToArray();
        if (group.Length == 0 || group.Length > 256 || group.Select(ForgeId).Distinct().Count() != group.Length) return ForgeNavigationStatus.RecipeUnavailable;
        var ui = GetStatic(ForgeUiType, "current");
        if (ui == null || UiActive?.Invoke(ui) != true || UiBelongsTo?.Invoke(ui, interior!) != true || Get(interior!, "currentTab")?.ToString() != "Forge")
        {
            // Awake receives a validated selection whose real parent group is present in the station list.
            var field = _assembly.GetType(ForgeUiType, true)!.GetField("preselectRecipe", Flags)!;
            var previous = field.GetValue(null);
            try
            {
                field.SetValue(null, selected);
                Call(interior!, "GoToLocation", facility, true);
            }
            finally { field.SetValue(null, previous); }
            if (UiSession?.Invoke() != session) return ForgeNavigationStatus.Uncertain;
            ui = GetStatic(ForgeUiType, "current");
        }
        if (ui == null || UiActive?.Invoke(ui) != true || UiBelongsTo?.Invoke(ui, interior!) != true || !ReferenceEquals(UiStation(session, out _), station))
            return ForgeNavigationStatus.Uncertain;
        // Revalidate after opening; other native hooks may have changed availability.
        var current = UiRecipes(Get(Get(station, "forge")!, "recipes"));
        var currentGroup = current.Where(item => ReferenceEquals(Get(item, "parentRecipe"), parent)).ToArray();
        if (!currentGroup.Contains(selected, NativeObjectIdentity.Instance) || currentGroup.Length != group.Length || !group.All(item => currentGroup.Contains(item, NativeObjectIdentity.Instance)))
            return ForgeNavigationStatus.Uncertain;
        var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(_assembly.GetType(RecipeType, true)!))!;
        foreach (var item in group) list.Add(item);
        Call(ui, "SelectRecipe", parent, list, selected);
        var observed = ReadUi(session);
        return observed?.SelectedRecipe.Equals(recipe) == true ? ForgeNavigationStatus.Selected : ForgeNavigationStatus.Uncertain;
    }
    // Native sources can repeat the same recipe object. Never merge distinct objects by identifier.
    private static object[] UiRecipes(object? values) => Enumerate(values).Distinct(NativeObjectIdentity.Instance).ToArray();
    private static RecipeId ForgeId(object recipe) => new("vanilla", "forge/" + Text(recipe, "identifier"));
}
