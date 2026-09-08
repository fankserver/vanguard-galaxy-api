using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private WorldLoadHookHost? _worldLoadHost;
    private WorldLifetimeHookHost? _worldLifetimeHost;
    private Harmony? _worldLoadHarmony;
    private WorldDefinitionRegistry? _worldDefinitions;
    private WorldSnapshotHookHost? _worldSnapshotHost;
    private WorldRuntimeState? _worldRuntime;
    private WorldPersistenceBindings? _worldPersistence;

    private void InitializeWorldProtection()
    {
        _hub!.SetCapability("world-load-protection", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("world-save-protection", false, "Disabled by configuration; experimental.");
        if (!Config.Bind("WorldProtection", "Enabled", false,
            "Experimental world load/save protection. Owned world authoring is not available; saved owned definitions are refused. Restart required after teardown.").Value) return;
        if (_persistence == null || _adapter == null || !_hub.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available))
        { _hub.SetCapability("world-load-protection", false, "Inspected lifecycle and persistence are required."); return; }
        if (WorldLoadPatches.Host != null || WorldLifetimePatches.Host != null || WorldSnapshotPatches.Host != null)
        { _hub.SetCapability("world-load-protection", false, "Process-lived world load guards already exist; restart required."); return; }
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            // No authenticated world definition service is exposed yet. Do not admit a saved
            // definition merely because its metadata or a similarly named plugin exists.
            _worldDefinitions = new WorldDefinitionRegistry(StoryHostAuthentication.Resolve, _hub.CheckThread);
            var definitions = _worldDefinitions;
            _worldLoadHost = new WorldLoadHookHost(assembly, _hub, _persistence, _persistence.CreateWorldReader(),
                _persistence.CanonicalLoadPath, _ => false, () => definitions.Revision);
            _worldLifetimeHost = new WorldLifetimeHookHost(assembly, _hub);
            var creation = new WorldCreationCoordinator(new WorldNativeAttachment(_adapter), _hub.CheckThread);
            _worldSnapshotHost = new WorldSnapshotHookHost(_hub, new WorldSnapshotRecorder(new WorldJsonInspection(assembly)), creation.Snapshot, () => creation.Revision);
            _worldPersistence = new WorldPersistenceBindings(_persistence, _hub, _worldLoadHost, _worldSnapshotHost, creation);
            _worldRuntime = new WorldRuntimeState(_adapter, _worldLoadHost, definitions, creation);
            var selected = WorldNativeBindings.Methods.Where(m => m.Key == "worldPoiRead" || m.Key == "worldRecall" || m.Key == "worldCombatUpdate" || m.Key == "worldRemove" || m.Key == "worldSnapshot" || m.Key == "worldStore" || m.Key == "worldActiveUpdate" || m.Key == "worldCanTravel" || m.Key == "worldRoute").ToArray();
            var targets = new GameBindings(assembly).Resolve(selected);
            _worldLoadHarmony = new Harmony(ModApi.PluginId + ".world-load");
            WorldLoadHookInstallation.Install(
                () =>
                {
                    _worldLoadHarmony.Patch(targets["worldPoiRead"],
                        prefix: new HarmonyMethod(typeof(WorldLoadPatches.Factory).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(WorldLoadPatches.Factory).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldCombatUpdate"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Ambient).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldRemove"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Remove).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldSnapshot"],
                        prefix: new HarmonyMethod(typeof(WorldSnapshotPatches.Snapshot).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldSnapshotPatches.Snapshot).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    _worldLoadHarmony.Patch(targets["worldActiveUpdate"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Active).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldCanTravel"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.CanTravel).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldRoute"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Route).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    // Establish the owner capture scope before the lifecycle Store prefix emits SaveStarted.
                    _worldLoadHarmony.Patch(targets["worldStore"],
                        prefix: new HarmonyMethod(typeof(WorldSnapshotPatches.Store).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldSnapshotPatches.Store).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                },
                () => _worldLoadHarmony.Patch(targets["worldRecall"], prefix: new HarmonyMethod(typeof(WorldLoadPatches.Recall).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static))),
                _worldLoadHarmony.UnpatchSelf);
            WorldSnapshotPatches.Host = _worldSnapshotHost;
            WorldLifetimePatches.Host = _worldLifetimeHost;
            WorldLoadPatches.Host = _worldLoadHost;
            _hub.SetCapability("world-save-protection", true, "Experimental scoped snapshot/save protection; no world authoring surface. Not runtime-qualified.");
            _hub.SetCapability("world-load-protection", true, "Experimental load guard only; owned world definitions are not admitted. Not runtime-qualified.");
        }
        catch (Exception error)
        {
            WorldLoadHookInstallation.CleanupFailure(
                StopWorldProtection,
                () => { if (WorldLoadPatches.Host == null && WorldLifetimePatches.Host == null && WorldSnapshotPatches.Host == null) _worldLoadHarmony?.UnpatchSelf(); },
                () =>
                {
                    if (_worldSnapshotHost != null) WorldSnapshotPatches.Host = _worldSnapshotHost;
                    if (_worldLifetimeHost != null) WorldLifetimePatches.Host = _worldLifetimeHost;
                    if (_worldLoadHost != null) WorldLoadPatches.Host = _worldLoadHost;
                },
                cleanup => Logger.LogError(cleanup));
            _hub.SetCapability("world-save-protection", false, "World guard initialization failed: " + error.GetType().Name);
            _hub.SetCapability("world-load-protection", false, "World load guard initialization failed: " + error.GetType().Name);
            Logger.LogError(error);
        }
    }

    private void StopWorldProtection()
    {
        try { _worldLoadHost?.Dispose(); }
        finally
        {
            try { _worldLifetimeHost?.Dispose(); }
            finally
            {
                try { _worldSnapshotHost?.Dispose(); }
                finally
                {
                    try { _worldRuntime?.Dispose(); }
                    finally
                    {
                        try { _worldPersistence?.Dispose(); }
                        finally { _worldDefinitions?.Dispose(); }
                    }
                }
            }
        }
        // Keep published factory guards attached: teardown must not turn reserved nodes into vanilla.
    }
}
