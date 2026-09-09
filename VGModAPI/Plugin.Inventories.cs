using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Runtime;

namespace VGModAPI;
public sealed partial class Plugin
{
    private InventoryService? _inventoryService;
    private InventoryNativeBackend? _inventoryBackend;
    private Harmony? _inventoryHarmony;
    private InventoryService CreateInventories() => new(_hub!, () => _inventoryBackend);
    private void InitializeInventories()
    {
        _inventoryService ??= CreateInventories();
        _hub!.SetCapability("inventories", false, "Inventory bindings initializing.");
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            var player = assembly.GetType("Source.Player.GamePlayer", true)!.GetField("current", BindingFlags.Public | BindingFlags.Static)!;
            if (player == null) throw new MissingFieldException("GamePlayer.current");
            _inventoryBackend = new InventoryNativeBackend(assembly, session => _adapter != null &&
                _adapter.TryGetObservedPlayer(session, out var observed) && ReferenceEquals(observed, player.GetValue(null)));
            var methods = new GameBindings(assembly).Resolve(new[]
            {
                new MethodBinding("inventoryPlayerSerialize", "Source.Player.GamePlayer", "ToJson", false, "LightJson.JsonValue"),
                new MethodBinding("inventorySerialize", "Source.Item.Inventory", "ToJson", false, "LightJson.JsonValue"),
                new MethodBinding("inventoryStore", BindingCatalog.Save, "Store", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32")
            });
            _inventoryHarmony = new Harmony(ModApi.PluginId + ".inventories");
            Patches.InventoryPatches.Service = _inventoryService;
            foreach (var method in methods.Values)
                _inventoryHarmony.Patch(method, prefix: new HarmonyMethod(typeof(Patches.InventoryPatches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(Patches.InventoryPatches).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic)));
            _hub.SetCapability("inventories", true, "Player-owned inventory snapshots and guarded immediate transfers.");
        }
        catch (Exception error)
        {
            _inventoryHarmony?.UnpatchSelf(); _inventoryBackend = null; Patches.InventoryPatches.Service = null;
            _hub.SetCapability("inventories", false, error.Message);
        }
    }
    private void StopInventories()
    {
        _inventoryService?.Dispose(); _inventoryBackend = null;
        // Keep save refusal installed if a failed compensation still owns recoverable state.
    }
}
