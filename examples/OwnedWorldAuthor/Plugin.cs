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
        _world?.Register(new WorldCombatSiteDefinition("PoiX", 1, "Example Combat Site", "player", 1));
    }
    // Call from explicit gameplay logic outside API callbacks. Supply an existing system ID
    // and retain the occurrence GUID in your campaign definition, not a native object.
    public WorldSiteResult Create(Guid session, Guid occurrence, string system, float x, float y) =>
        _world?.CreatePersistentCombatSite(session, "PoiX", occurrence, system, x, y) ?? new(WorldStatus.Unavailable);
    public WorldSiteResult Find(Guid session, Guid occurrence) =>
        _world?.FindPersistentCombatSite(session, new WorldSiteReference(PluginId, "PoiX", occurrence)) ?? new(WorldStatus.Unavailable);
    private void OnDestroy() => _world?.Dispose();
}
