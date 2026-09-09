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
    private WorldContentService? _worldContent;
    private WorldReferenceResolver? _worldReferences;

    private void InitializeWorldProtection()
    {
        _hub!.SetCapability("world-authoring", false, "Native world creation is not yet runtime-qualified.");
        _hub.SetCapability("world-load-protection", false, "Disabled by configuration; experimental.");
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
#if VG_WORLD_QUALIFICATION
            const bool emptyProfile = true;
            Action<object>? inspectProfile = new WorldEmptyCombatProfile(assembly).Require;
#else
            const bool emptyProfile = false;
            Action<object>? inspectProfile = null;
#endif
            // Authenticated declarations do not qualify native loading. Keep admission closed
            // until the complete runtime profile has been independently qualified.
            _worldDefinitions = new WorldDefinitionRegistry(StoryHostAuthentication.Resolve, _hub.CheckThread);
            var definitions = _worldDefinitions;
            _worldLoadHost = new WorldLoadHookHost(assembly, _hub, _persistence, _persistence.CreateWorldReader(),
                _persistence.CanonicalLoadPath, _ => false, () => definitions.Revision, emptyProfile: emptyProfile);
            var salvageConstructor = assembly.GetType("Source.Data.Persistable.SalvageData", true)!.GetConstructor(Type.EmptyTypes)
                ?? throw new MissingMethodException("SalvageData..ctor()");
            var lifetime = new WorldLifetimeGuard();
            var creation = new WorldCreationCoordinator(new WorldNativeAttachment(_adapter, inspectProfile), _hub.CheckThread, lifetime, inspectProfile);
            _worldLifetimeHost = new WorldLifetimeHookHost(assembly, _hub, lifetime, new WorldActorPhysics(assembly).Stop,
                session => { creation.Refuse(session); _story?.RefreshWorldDependencies(); }, inspectProfile);
            _worldSnapshotHost = new WorldSnapshotHookHost(_hub, new WorldSnapshotRecorder(new WorldJsonInspection(assembly, emptyProfile)), creation.Snapshot, () => creation.Revision);
            _worldPersistence = new WorldPersistenceBindings(_persistence, _hub, _worldLoadHost, _worldSnapshotHost, creation);
            _worldRuntime = new WorldRuntimeState(_adapter, _worldLoadHost, definitions, creation,
                lifetime, _worldPersistence.StateReady, () => false);
            _worldReferences = new WorldReferenceResolver(_hub, creation, definitions, _worldPersistence, _worldLifetimeHost);
            _worldContent = new WorldContentService(_hub, definitions, new WorldAuthoringGate(definitions, creation, _worldPersistence.CanMutate), () => false, () => { try { _story?.RefreshWorldDependencies(); } finally { _worldLifetimeHost?.MaintainActors(); } });
            var selected = WorldNativeBindings.Methods.Where(m => m.Key == "worldPoiRead" || m.Key == "worldRecall" || m.Key == "worldCombatUpdate" || m.Key == "worldRemove" || m.Key == "worldSnapshot" || m.Key == "worldStore" || m.Key == "worldActiveUpdate" || m.Key == "worldCanTravel" || m.Key == "worldRoute" || m.Key == "worldBaseArrival" || m.Key == "worldCombatArrival" || m.Key == "worldSpawnPersistable" || m.Key == "worldSpawnUnit" || m.Key == "worldManagerStart" || m.Key == "worldManagerUpdate" || m.Key == "worldSecurityPatrol" || m.Key == "worldManagerInit" || m.Key == "worldInitializePoi" || m.Key == "worldInitializationComplete" || m.Key == "worldBaseAwake" || m.Key == "worldCombatAwake" || m.Key == "worldStoreLastX" || m.Key == "worldStorePosition" || m.Key == "worldIncomingReinforcements" || m.Key == "worldCreateSecurityPatrol" || m.Key == "worldStartTravel" || m.Key == "worldNextWaypoint" || m.Key == "worldTravelChild" || m.Key == "worldCheckLocalScene" || m.Key == "worldUnloadScene" || m.Key == "worldWaitUnload" || m.Key == "worldCancelTravel" || m.Key == "worldGenerate" || m.Key == "worldRegenerateGuards" || m.Key == "worldRegenerateCargo" || m.Key == "worldRegenerateSalvage" || m.Key == "worldRegenerateAsteroids" || m.Key == "worldRebuildStation" || m.Key == "worldJumpgateWave" || m.Key == "worldDeferGeneration" || m.Key == "worldPayloadUpdate" || m.Key == "worldPayloadTrigger" || m.Key == "worldPayloadSpawn" || m.Key == "worldPoiAddPersistable" || m.Key == "worldPoiRemovePersistable" || m.Key == "worldPoiAddUnit" || m.Key == "worldPoiRemoveUnit" || m.Key == "worldPoiAddPayload" || m.Key == "worldAddTriggered" || m.Key == "worldAddBudgetPayload" || m.Key == "worldAddFixedPayload" || m.Key == "worldActorAwake" || m.Key == "worldActorStart" || m.Key == "worldActorUpdate" || m.Key == "worldActorPhysics" || m.Key == "worldShipStart" || m.Key == "worldShipUpdate" || m.Key == "worldActorSetData" || m.Key == "worldShipSetData" || m.Key == "worldActorModules" || m.Key == "worldActorDamage" || m.Key == "worldShipDamage" || m.Key == "worldActorCollisionEnter" || m.Key == "worldActorCollisionStay" || m.Key == "worldPersistableStart" || m.Key == "worldPersistableUpdate" || m.Key == "worldBudgetBuilder" || m.Key == "worldSalvageReset" || m.Key == "worldSalvageAdd" || m.Key == "worldSalvageSlot" || m.Key == "worldSalvageDescriptor" || m.Key.StartsWith("worldActorRoutine", StringComparison.Ordinal)).ToArray();
            var targets = new GameBindings(assembly).Resolve(selected);
            _worldLoadHarmony = new Harmony(ModApi.PluginId + ".world-load");
            WorldLoadHookInstallation.Install(
                () =>
                {
                    _worldLoadHarmony.Patch(targets["worldPoiRead"],
                        prefix: new HarmonyMethod(typeof(WorldLoadPatches.Factory).GetMethod("ConstructPrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(WorldLoadPatches.Factory).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldCombatUpdate"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Ambient).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldRemove"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Remove).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldSnapshot"],
                        prefix: new HarmonyMethod(typeof(WorldSnapshotPatches.Snapshot).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldSnapshotPatches.Snapshot).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    _worldLoadHarmony.Patch(targets["worldActiveUpdate"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Active).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldCanTravel"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.CanTravel).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldRoute"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Route).GetMethod("CapturePrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Route).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    foreach (var key in new[] { "worldBaseArrival", "worldCombatArrival", "worldManagerStart", "worldManagerUpdate", "worldSecurityPatrol", "worldStoreLastX", "worldStorePosition", "worldIncomingReinforcements", "worldCreateSecurityPatrol" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Arrival).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    foreach (var key in new[] { "worldSpawnPersistable", "worldSpawnUnit" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Spawn).GetMethod("CapturePrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(WorldLifetimePatches.Spawn).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last },
                            finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Spawn).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    foreach (var key in new[] { "worldManagerInit", "worldInitializePoi", "worldInitializationComplete" })
                        _worldLoadHarmony.Patch(targets[key], postfix: new HarmonyMethod(typeof(WorldLifetimePatches.Initialization).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                    foreach (var key in new[] { "worldBaseAwake", "worldCombatAwake" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Awake).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldStartTravel"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.TravelLeg).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(WorldLifetimePatches.TravelLeg).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldNextWaypoint"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Waypoint).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Waypoint).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)));
                    _worldLoadHarmony.Patch(targets["worldTravelChild"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.TravelChild).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(WorldLifetimePatches.TravelChild).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                    foreach (var key in new[] { "worldCheckLocalScene", "worldUnloadScene" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.SceneTransition).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldWaitUnload"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.SceneUnload).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldCancelTravel"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.CancelTravel).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.CancelTravel).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    foreach (var key in new[] { "worldGenerate", "worldRegenerateAsteroids", "worldRebuildStation", "worldJumpgateWave", "worldDeferGeneration", "worldPoiAddPersistable", "worldPoiRemovePersistable", "worldPoiAddUnit", "worldPoiRemoveUnit", "worldAddTriggered", "worldAddBudgetPayload", "worldAddFixedPayload" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generate).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldRegenerateGuards", "worldRegenerateCargo", "worldRegenerateSalvage" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.GenerateArgument).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldPayloadUpdate", "worldPayloadTrigger", "worldPayloadSpawn" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Payload).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldPoiAddPayload"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.PayloadAttachment).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldActorAwake"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ActorAwake).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldActorStart", "worldActorUpdate", "worldActorPhysics", "worldShipStart", "worldShipUpdate", "worldActorCollisionEnter", "worldActorCollisionStay" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ActorActivity).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldActorSetData", "worldShipSetData" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ActorData).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldActorModules", "worldActorDamage", "worldShipDamage" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ActorMutation).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in targets.Keys.Where(key => key.StartsWith("worldActorRoutine", StringComparison.Ordinal)))
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ActorContinuation).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(WorldLifetimePatches.ActorContinuation).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    foreach (var key in new[] { "worldPersistableStart", "worldPersistableUpdate" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.PersistableActivity).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldPoiAddPersistable", "worldPoiAddUnit", "worldPoiAddPayload", "worldAddTriggered", "worldAddBudgetPayload", "worldAddFixedPayload", "worldSalvageAdd" })
                        _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ProfileMutation).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldSalvageDescriptor"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.ProfileMutation).GetMethod("StaticPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    foreach (var key in new[] { "worldGenerate", "worldSalvageReset", "worldSalvageAdd" })
                    _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    foreach (var key in new[] { "worldRegenerateSalvage" })
                    _worldLoadHarmony.Patch(targets[key], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("StaticPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    _worldLoadHarmony.Patch(targets["worldSalvageDescriptor"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("DescriptorPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("DescriptorFinalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    _worldLoadHarmony.Patch(salvageConstructor, prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("ConstructorPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldSalvageSlot"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("SlotPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.Last });
                    _worldLoadHarmony.Patch(targets["worldPoiAddPersistable"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("PublicationPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                    _worldLoadHarmony.Patch(targets["worldBudgetBuilder"], prefix: new HarmonyMethod(typeof(WorldLifetimePatches.Generation).GetMethod("BuilderPrefix", BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
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
            ModApi.World = _worldContent;
            _hub.SetCapability("world-save-protection", true, "Experimental scoped snapshot/save protection; declarations only, native authoring unavailable. Not runtime-qualified.");
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
        _worldReferences = null;
        if (ReferenceEquals(ModApi.World, _worldContent)) ModApi.World = null;
        try { _worldContent?.Dispose(); }
        finally { StopWorldGuards(); }
    }

    private void StopWorldGuards()
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
