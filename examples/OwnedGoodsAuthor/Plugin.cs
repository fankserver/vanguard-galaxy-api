using BepInEx;
using VGModAPI;

namespace OwnedGoodsAuthor;

[BepInPlugin("example.ownedgoods", "Owned Goods Example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.4")]
public sealed class Plugin : BaseUnityPlugin
{
    private IOwnedItemProvider? _provider;
    private IOwnedRecipeProvider? _recipes;
    private void Awake()
    {
        _recipes = ModApi.Services.RecipeRegistration.AcquireProvider(this);
        // Deliberately register the recipe first: owned item dependencies resolve automatically later.
        _recipes?.Register(new OwnedRecipeDefinition("container-recipe", 1, "Silo Container", 100, 30,
            new[] { new OwnedRecipeIngredient(RecipeItemReference.Vanilla("Carbon"), 2) },
            new OwnedRecipeIngredient(RecipeItemReference.FromOwned(new OwnedItemReference("example.ownedgoods", "silo-container")), 1)));
        _provider = ModApi.Services.Items.AcquireProvider(this);
        if (_provider == null) { Logger.LogWarning("Item provider unavailable."); return; }
        var result = _provider.Register(new OwnedItemDefinition("silo-container", 1, "Silo Container",
            "A manufactured container. Plain trade goods, not an installable silo.", "Carbon", 15, 100, OwnedItemStorage.Armory));
        Logger.LogInfo("Item declaration: " + result);
    }
    // _recipes.Find("container-recipe") supplies a RecipeId to the catalog/quote/crafting-command APIs.
    private void OnDestroy() { _recipes?.Dispose(); _provider?.Dispose(); }
}
