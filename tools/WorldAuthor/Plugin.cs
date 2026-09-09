using System;
using BepInEx;
using VGModAPI;

namespace VGModAPI.WorldQualificationAuthor;

[BepInPlugin(Id, "World qualification author", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.2.0")]
public sealed class Plugin : BaseUnityPlugin
{
#if WORLD_AUTHOR_B
    public const string Id = "vgmodapi.qualification.world.b";
#else
    public const string Id = "vgmodapi.qualification.world.a";
#endif
    private IWorldProvider? _provider;
    public bool Registered { get; private set; }
    private IWorldProvider Provider => _provider ?? throw new InvalidOperationException("World author is not registered.");
    private void Awake()
    {
        // Authentication must originate in this assembly, before participant readiness.
        _provider = ModApi.Services.World.AcquireProvider(this) ?? throw new InvalidOperationException("World provider authentication refused.");
        Registered = _provider.Register(new WorldCombatSiteDefinition("PoiX", 1, "Empty qualification site", "player", 1)) == WorldStatus.Succeeded;
        if (!Registered) { Release(); throw new InvalidOperationException("World declaration refused."); }
    }
    public WorldSiteResult Create(Guid session, Guid instance, string system, float x, float y)
        => Provider.CreatePersistentCombatSite(session, "PoiX", instance, system, x, y);
    public WorldSiteResult Find(Guid session, Guid instance)
        => Provider.FindPersistentCombatSite(session, new WorldSiteReference(Id, "PoiX", instance));
    public void Release() { _provider?.Dispose(); _provider = null; Registered = false; }
    private void OnDestroy() => Release();
}
