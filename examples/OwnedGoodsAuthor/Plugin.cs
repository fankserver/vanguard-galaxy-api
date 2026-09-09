using BepInEx;
using VGModAPI;

namespace OwnedGoodsAuthor;

[BepInPlugin("example.ownedgoods", "Owned Goods Example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.3")]
public sealed class Plugin : BaseUnityPlugin
{
    private IOwnedItemProvider? _provider;
    private void Awake()
    {
        _provider = ModApi.Services.Items.AcquireProvider(this);
        if (_provider == null) { Logger.LogWarning("Item provider unavailable."); return; }
        var result = _provider.Register(new OwnedItemDefinition("silo-container", 1, "Silo Container",
            "A manufactured container. Plain trade goods, not an installable silo.", "Carbon", 15, 100, OwnedItemStorage.Armory));
        Logger.LogInfo("Item declaration: " + result);
    }
    private void OnDestroy() => _provider?.Dispose();
}
