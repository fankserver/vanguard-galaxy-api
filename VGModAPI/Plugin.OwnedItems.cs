using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;

namespace VGModAPI;
public sealed partial class Plugin
{
    private OwnedItemService? _ownedItems;
    private OwnedItemNativeCatalog? _ownedItemCatalog;
    private Harmony? _ownedItemHarmony;
    private bool _ownedItemsStopped;
    private OwnedItemService CreateOwnedItems() => new(_hub!, StoryHostAuthentication.Resolve, identity =>
    {
        if (_ownedItemsStopped || !_worldAvailable || _ownedItemCatalog == null) throw new InvalidOperationException("Owned item catalog unavailable.");
        if (_ownedItemCatalog.Loaded) _ownedItemCatalog.Ensure(identity.NativeId);
    });
    private void RestoreOwnedItem(string id)
    {
        _hub!.CheckThread();
        if (_ownedItemsStopped || !_worldAvailable || _ownedItemCatalog == null)
            throw new InvalidOperationException("Owned item save protection unavailable.");
        _ownedItemCatalog.Ensure(id);
    }
    private void InitializeOwnedItems()
    {
        _ownedItems ??= CreateOwnedItems();
        _hub!.SetCapability("owned-items", false, "Owned item bindings are initializing.");
        try
        {
            _ownedItemCatalog = new OwnedItemNativeCatalog();
            var type = Assembly.Load("Assembly-CSharp").GetType("Behaviour.Item.InventoryItemType", true)!;
            _ownedItemHarmony = new Harmony(ModApi.PluginId + ".owned-items");
            Patches.OwnedItemPatches.Resolve = RestoreOwnedItem;
            var inspector = new Core.Integration.WorldJsonInspection(type.Assembly);
            Patches.OwnedItemPatches.ValidateValue = inspector.RequirePlainItemValue;
            Patches.OwnedItemPatches.Rebuild = () =>
            {
                if (_ownedItemsStopped) return;
                _ownedItemCatalog.Reinsert();
                OwnedItemDeclarationPublication.Publish(_ownedItems.Definitions, id => _ownedItemCatalog.Ensure(id), _hub.ReportSubscriberFailure);
            };
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public))
                if (method.Name == "FromJson") _ownedItemHarmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(Patches.OwnedItemPatches), nameof(Patches.OwnedItemPatches.FromJson)));
            foreach (var name in new[] { "Get", "TryGet" })
                _ownedItemHarmony.Patch(type.GetMethod(name, BindingFlags.Static | BindingFlags.Public),
                    prefix: new HarmonyMethod(typeof(Patches.OwnedItemPatches), nameof(Patches.OwnedItemPatches.Lookup)));
            _ownedItemHarmony.Patch(type.GetMethod("LoadAll", BindingFlags.Static | BindingFlags.Public),
                postfix: new HarmonyMethod(typeof(Patches.OwnedItemPatches), nameof(Patches.OwnedItemPatches.Loaded)));
            _hub.SetCapability("owned-items", _worldAvailable, "Plain stackable trade goods with API-managed identity reconstruction.");
        }
        catch (Exception error)
        {
            _ownedItemsStopped = true; Patches.OwnedItemPatches.Resolve = null; Patches.OwnedItemPatches.Rebuild = null;
            _ownedItemHarmony?.UnpatchSelf(); _hub.SetCapability("owned-items", false, error.Message);
        }
    }
    private void StopOwnedItems()
    {
        if (_ownedItemsStopped) return;
        _ownedItemsStopped = true; _ownedItems?.Dispose();
        Patches.OwnedItemPatches.Resolve = null; Patches.OwnedItemPatches.Rebuild = null;
        // Keep the reserved-identity refusal hook. Native inventories may still reference retained hosts during shutdown.
        if (_hub?.CurrentSession == null) _ownedItemCatalog?.Dispose();
    }
}
