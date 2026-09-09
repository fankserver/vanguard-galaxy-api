using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;

namespace VGModAPI;
public sealed partial class Plugin
{
    private OwnedRecipeService? _ownedRecipes;
    private OwnedRecipeNativeCatalog? _ownedRecipeCatalog;
    private bool _ownedRecipesStopped;
    private OwnedRecipeService CreateOwnedRecipes() => new(_hub!, StoryHostAuthentication.Resolve,
        reference => reference.Owned is { } owned ? _ownedItems?.Find(owned.ProviderId, owned.LocalId)?.NativeId : reference.VanillaId,
        identity =>
        {
            if (_ownedRecipesStopped || _ownedItemsStopped || !_worldAvailable || _ownedRecipeCatalog == null) throw new InvalidOperationException("Recipe construction unavailable.");
            _ownedRecipeCatalog.Activate(identity);
        }, id => _ownedRecipeCatalog?.Retire(id));
    private void RestoreOwnedRecipe(string id)
    {
        _hub!.CheckThread();
        if (_ownedRecipesStopped || _ownedItemsStopped || !_worldAvailable || _ownedRecipeCatalog == null) throw new InvalidOperationException("Owned recipe restoration unavailable.");
        _ownedRecipeCatalog.Ensure(id);
    }
    private void InitializeOwnedRecipes()
    {
        _ownedRecipes ??= CreateOwnedRecipes();
        _hub!.SetCapability("owned-recipes", false, "Owned recipe bindings initializing.");
        var harmony = new Harmony(ModApi.PluginId + ".owned-recipes");
        try
        {
            _ownedRecipeCatalog = new OwnedRecipeNativeCatalog(id => _ownedItemCatalog!.ResolvePlain(id));
            var type = Assembly.Load("Assembly-CSharp").GetType("Behaviour.Crafting.CraftingRecipe", true)!;
            Patches.OwnedRecipePatches.Resolve = RestoreOwnedRecipe;
            Patches.OwnedRecipePatches.Reload = () => { _ownedRecipeCatalog.Reinsert(); _ownedRecipes.Refresh(); };
            foreach (var name in new[] { "Get", "TryGet" }) harmony.Patch(type.GetMethod(name, BindingFlags.Public | BindingFlags.Static),
                prefix: new HarmonyMethod(typeof(Patches.OwnedRecipePatches), nameof(Patches.OwnedRecipePatches.Lookup)));
            harmony.Patch(type.GetMethod("LoadAll"), postfix: new HarmonyMethod(typeof(Patches.OwnedRecipePatches), nameof(Patches.OwnedRecipePatches.Loaded)));
            _ownedItems!.DefinitionsChanged += RefreshOwnedRecipes;
            _hub.SetCapability("owned-recipes", _worldAvailable && !_ownedItemsStopped && _ownedItemCatalog != null, "Fixed plain-goods recipes with retained job definitions.");
        }
        catch (Exception error) { harmony.UnpatchSelf(); Patches.OwnedRecipePatches.Resolve = null; Patches.OwnedRecipePatches.Reload = null; _hub.SetCapability("owned-recipes", false, error.Message); }
    }
    private void RefreshOwnedRecipes() => _ownedRecipes?.Refresh();
    private void StopOwnedRecipes()
    {
        if (_ownedRecipesStopped) return; _ownedRecipesStopped = true;
        if (_ownedItems != null) _ownedItems.DefinitionsChanged -= RefreshOwnedRecipes;
        _ownedRecipes?.Dispose(); Patches.OwnedRecipePatches.Resolve = null; Patches.OwnedRecipePatches.Reload = null;
        // Retain live job hosts and reserved lookup refusal through shutdown.
    }
}
