using System;
using BepInEx.Bootstrap;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private (object A, object B) RequireWorldAuthors()
    {
        Require(_api != null && _api.CurrentSession == null, "World authors must be checked before starting a session.");
        object Resolve(string id, string assemblyName)
        {
            Require(Chainloader.PluginInfos.TryGetValue(id, out var info) && info.Instance != null,
                "Missing authenticated world author: " + id);
            var instance = info!.Instance ?? throw new InvalidOperationException("World author instance unavailable: " + id);
            Require(instance.GetType().Assembly.GetName().Name == assemblyName,
                "World author assembly mismatch: " + id);
            Require((bool)SpGet(instance, "Registered")!, "World author registration failed: " + id);
            return instance;
        }
        var a = Resolve("vgmodapi.qualification.world.a", "WorldAuthorA");
        var b = Resolve("vgmodapi.qualification.world.b", "WorldAuthorB");
        Require(!ReferenceEquals(a, b) && a.GetType().Assembly != b.GetType().Assembly, "World authors must be independent instances and assemblies.");
        return (a, b);
    }
}
