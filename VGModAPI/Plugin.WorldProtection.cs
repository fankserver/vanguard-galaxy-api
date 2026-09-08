using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private WorldLoadHookHost? _worldLoadHost;
    private WorldLifetimeHookHost? _worldLifetimeHost;
    private Harmony? _worldLoadHarmony;

    private void InitializeWorldProtection()
    {
        _hub!.SetCapability("world-load-protection", false, "Disabled by configuration; experimental.");
        if (!Config.Bind("WorldProtection", "Enabled", false,
            "Experimental pre-construction load protection. Owned world creation/reconstruction is not available; reserved world content is refused.").Value) return;
        if (_persistence == null || !_hub.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available))
        { _hub.SetCapability("world-load-protection", false, "Inspected lifecycle and persistence are required."); return; }
        if (WorldLoadPatches.Host != null || WorldLifetimePatches.Host != null)
        { _hub.SetCapability("world-load-protection", false, "Process-lived world load guards already exist; restart required."); return; }
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            // No authenticated world definition service is exposed yet. Do not admit a saved
            // definition merely because its metadata or a similarly named plugin exists.
            _worldLoadHost = new WorldLoadHookHost(assembly, _hub, _persistence, _persistence.CreateWorldReader(),
                _persistence.CanonicalLoadPath, _ => false, () => 0);
            _worldLifetimeHost = new WorldLifetimeHookHost(assembly, _hub);
            var selected = WorldNativeBindings.Methods.Where(m => m.Key == "worldPoiRead" || m.Key == "worldRecall" || m.Key == "worldCombatUpdate" || m.Key == "worldRemove").ToArray();
            var targets = new GameBindings(assembly).Resolve(selected);
            _worldLoadHarmony = new Harmony(ModApi.PluginId + ".world-load");
            WorldLoadHookInstallation.Install(
                () =>
                {
                    _worldLoadHarmony.Patch(targets["worldPoiRead"], prefix: new HarmonyMethod(typeof(WorldLoadPatches.Factory).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldCombatUpdate"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Ambient).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldRemove"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Remove).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                },
                () => _worldLoadHarmony.Patch(targets["worldRecall"], prefix: new HarmonyMethod(typeof(WorldLoadPatches.Recall).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static))),
                _worldLoadHarmony.UnpatchSelf);
            WorldLifetimePatches.Host = _worldLifetimeHost;
            WorldLoadPatches.Host = _worldLoadHost;
            _hub.SetCapability("world-load-protection", true, "Experimental load guard only; owned world definitions are not admitted. Not runtime-qualified.");
        }
        catch (Exception error)
        {
            WorldLoadHookInstallation.CleanupFailure(
                StopWorldProtection,
                () => { if (WorldLoadPatches.Host == null && WorldLifetimePatches.Host == null) _worldLoadHarmony?.UnpatchSelf(); },
                () =>
                {
                    if (_worldLifetimeHost != null) WorldLifetimePatches.Host = _worldLifetimeHost;
                    if (_worldLoadHost != null) WorldLoadPatches.Host = _worldLoadHost;
                },
                cleanup => Logger.LogError(cleanup));
            _hub.SetCapability("world-load-protection", false, "World load guard initialization failed: " + error.GetType().Name);
            Logger.LogError(error);
        }
    }

    private void StopWorldProtection()
    {
        try { _worldLoadHost?.Dispose(); }
        finally { _worldLifetimeHost?.Dispose(); }
        // Keep published factory guards attached: teardown must not turn reserved nodes into vanilla.
    }
}
