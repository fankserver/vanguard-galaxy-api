using System;
using BepInEx;
using VGModAPI;

namespace OwnedWorldAuthor;

[BepInPlugin(PluginId, "Owned Combat Example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.5")]
public sealed class Plugin : BaseUnityPlugin
{
#if AUTHOR_B
    public const string PluginId = "example.world.b";
#else
    public const string PluginId = "example.world.a";
#endif
    private IWorldProvider? _world;
    private void Awake()
    {
        _world = ModApi.Services.World.AcquireProvider(this);
        _world?.RegisterCombatSite(new CombatSiteDefinition("PoiX", 1, "Example Combat Site", "player", 1));
    }
    // Call from explicit gameplay logic outside API callbacks. Name the occurrence with your own
    // key; the API allocates the native identity and reconciles the same key to the same object.
    public ICombatSite? Create(string occurrenceKey, string system, float x, float y) =>
        _world?.CreateCombatSite("PoiX", occurrenceKey, system, x, y);
    public ICombatSite? Find(string occurrenceKey) => _world?.GetCombatSite("PoiX", occurrenceKey);
    private void OnDestroy() => _world?.Dispose();
}
