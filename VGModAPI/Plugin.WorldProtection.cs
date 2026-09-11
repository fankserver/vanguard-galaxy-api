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
    private PocketSystemRegistry? _authoredDefinitions;
    private PocketSystemCoordinator? _authoredCoordinator;
    private WormholePairRegistry? _wormholeDefinitions;
    private WormholePairCoordinator? _wormholeCoordinator;
    private ResourceSiteRegistry? _siteDefinitions;
    private ResourceSiteCoordinator? _siteCoordinator;
    private MooredShipRegistry? _shipDefinitions;
    private MooredShipCoordinator? _shipCoordinator;
    private double _authoredDue;
    private bool _worldAvailable;

    /// <summary>Slow-cadence reconciled-invariant convergence for authored pocket systems; fails open.</summary>
    private void MaintainPocketSystems()
    {
        if (_authoredCoordinator == null || _worldContent == null) return;
        if (UnityEngine.Time.time < _authoredDue) return;
        _authoredDue = UnityEngine.Time.time + 2.0;
        var current = _hub?.CurrentSession;
        if (current == null || current.Phase != SessionPhase.GameplayInitialized) return;
        // Route through the service so the owned occurrence objects are refreshed and their Changed events
        // fire on their own transitions after the coordinator converges gate/reconstruction state.
        try { _worldContent.MaintainPocketSystems(current.Id); }
        catch (Exception error) { Logger.LogError(error); }
    }

    private void InitializeWorldProtection()
    {
        _worldAvailable = false;
        _hub!.SetCapability("world-authoring", false, "World service is initializing.");
        _hub.SetCapability("world-load-protection", false, "World service is initializing.");
        _hub.SetCapability("world-save-protection", false, "World service is initializing.");
        if (_persistence == null || _adapter == null || !_hub.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available))
        { _hub.SetCapability("world-load-protection", false, "Inspected lifecycle and persistence are required."); return; }
        if (WorldLoadPatches.Host != null || WorldLifetimePatches.Host != null || WorldSnapshotPatches.Host != null)
        { _hub.SetCapability("world-load-protection", false, "Process-lived world load guards already exist; restart required."); return; }
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            const bool emptyProfile = false;
            StoryHostAuthenticator authenticate = StoryHostAuthentication.Resolve;
            Func<bool> admission = () => _worldAvailable;
            Action requireContext = () => { if (!_worldAvailable) throw new System.IO.InvalidDataException("World service is unavailable."); };
            Action<object>? inspectProfile = null;
            _worldDefinitions = new WorldDefinitionRegistry(authenticate, _hub.CheckThread);
            var definitions = _worldDefinitions;
            _worldLoadHost = new WorldLoadHookHost(assembly, _hub, _persistence, _persistence.CreateWorldReader(),
                _persistence.CanonicalLoadPath, saved => admission() && definitions.MatchesRetained(saved) && admission(), () => definitions.Revision, emptyProfile: emptyProfile, requireContext: requireContext, restoreItem: RestoreOwnedItem, restoreRecipe: RestoreOwnedRecipe);
            var salvageConstructor = assembly.GetType("Source.Data.Persistable.SalvageData", true)!.GetConstructor(Type.EmptyTypes)
                ?? throw new MissingMethodException("SalvageData..ctor()");
            var lifetime = new WorldLifetimeGuard();
            var creation = new WorldCreationCoordinator(new WorldNativeAttachment(_adapter, inspectProfile), _hub.CheckThread, lifetime, inspectProfile);
            // Resource-pocket integration is fail-open: a binding failure disables only authored systems,
            // never the shared Combat-site machine. Generalizing the owned chain to kinds (system + gates)
            // is the follow-up unification slice (see #233 keyed-ownership pass).
            PocketSystemCoordinator? authored = null;
            try
            {
                _authoredDefinitions = new PocketSystemRegistry(authenticate, _hub.CheckThread);
                // Once-per-distinct-cause fault reporting: authored-path faults must be visible in the log.
                var authoredFaults = new System.Collections.Generic.HashSet<string>();
                Action<Exception> reportContent = fault =>
                {
                    if (authoredFaults.Add(fault.GetType().Name + ":" + fault.Message)) Logger.LogError(fault);
                };
                authored = new PocketSystemCoordinator(_hub, _authoredDefinitions,
                    new WorldNativePocketSystems(_adapter, assembly, report: reportContent),
                    admission, session => _worldPersistence != null && _worldPersistence.StateReady(session),
                    reportContent);
                _authoredCoordinator = authored;
                _wormholeDefinitions = new WormholePairRegistry(authenticate, _hub.CheckThread);
                _wormholeCoordinator = new WormholePairCoordinator(_hub, _wormholeDefinitions,
                    new WorldNativeWormholes(_adapter, assembly, reportContent),
                    session => _worldPersistence != null && _worldPersistence.StateReady(session), reportContent);
                _siteDefinitions = new ResourceSiteRegistry(authenticate, _hub.CheckThread);
                _siteCoordinator = new ResourceSiteCoordinator(_hub, _siteDefinitions,
                    new WorldNativeResourceSites(_adapter, assembly, reportContent),
                    session => _worldPersistence != null && _worldPersistence.StateReady(session), reportContent);
                _shipDefinitions = new MooredShipRegistry(authenticate, _hub.CheckThread);
                _shipCoordinator = new MooredShipCoordinator(_hub, _shipDefinitions,
                    new Runtime.MooredShipWorld(_adapter, assembly, reportContent),
                    session => _worldPersistence != null && _worldPersistence.StateReady(session),
                    (unitId, key) => (_unitProtection ??= new UnitProtectionService(_hub)).Protect(unitId, key),
                    reportContent);
            }
            catch (Exception authoredError)
            {
                _authoredDefinitions?.Dispose(); _authoredDefinitions = null; _authoredCoordinator = null;
                _wormholeDefinitions?.Dispose(); _wormholeDefinitions = null; _wormholeCoordinator?.Dispose(); _wormholeCoordinator = null;
                _siteDefinitions?.Dispose(); _siteDefinitions = null; _siteCoordinator?.Dispose(); _siteCoordinator = null;
                _shipDefinitions?.Dispose(); _shipDefinitions = null; _shipCoordinator?.Dispose(); _shipCoordinator = null;
                _hub.SetCapability("authored-systems", false, "Resource-system integration unavailable: " + authoredError.GetType().Name);
                Logger.LogError(authoredError);
            }
            _worldLifetimeHost = new WorldLifetimeHookHost(assembly, _hub, lifetime, new WorldActorPhysics(assembly).Stop,
                session => { creation.Refuse(session); _story?.RefreshWorldDependencies(); }, inspectProfile);
            _worldSnapshotHost = new WorldSnapshotHookHost(_hub, new WorldSnapshotRecorder(
                new WorldJsonInspection(assembly, emptyProfile, RestoreOwnedItem, RestoreOwnedRecipe),
                authored == null ? null : () => PocketSystemStateCodec.Encode(authored.CaptureRows(), _siteCoordinator?.CaptureRows() ?? Array.Empty<ResourceSiteOccurrence>(), _shipCoordinator?.CaptureRows() ?? Array.Empty<MooredShipOccurrence>(), _wormholeCoordinator?.CaptureRows() ?? Array.Empty<WormholePairOccurrence>(), _worldContent?.CaptureCombatKeys() ?? Array.Empty<CombatSiteKeyRow>())),
                creation.Snapshot, () => { requireContext(); return creation.Revision; }, requireContext);
            _worldPersistence = new WorldPersistenceBindings(_persistence, _hub, _worldLoadHost, _worldSnapshotHost, creation,
                authored == null ? null : new Action<Guid, byte[]?>((session, bytes) =>
                {
                    var decoded = bytes == null
                        ? (Array.Empty<PocketSystemOccurrence>(), Array.Empty<ResourceSiteOccurrence>(), Array.Empty<MooredShipOccurrence>(), Array.Empty<WormholePairOccurrence>(), Array.Empty<CombatSiteKeyRow>())
                        : PocketSystemStateCodec.DecodeAll(bytes);
                    authored.RestoreRows(session, decoded.Item1);
                    if (_siteCoordinator != null) _siteCoordinator.RestoreRows(session, decoded.Item2);
                    else if (decoded.Item2.Length > 0)
                        throw new System.IO.InvalidDataException("Resource-site rows present but the site integration is unavailable; refusing a restore that would erase them.");
                    if (_shipCoordinator != null) _shipCoordinator.RestoreRows(session, decoded.Item3);
                    else if (decoded.Item3.Length > 0)
                        throw new System.IO.InvalidDataException("Resource-ship rows present but the ship integration is unavailable; refusing a restore that would erase them.");
                    if (_wormholeCoordinator != null) _wormholeCoordinator.RestoreRows(session, decoded.Item4);
                    else if (decoded.Item4.Length > 0)
                        throw new System.IO.InvalidDataException("Resource-wormhole rows present but the wormhole integration is unavailable; refusing a restore that would erase them.");
                    if (_worldContent != null) _worldContent.RestoreCombatKeys(session, decoded.Item5);
                    else if (decoded.Item5.Length > 0)
                        throw new System.IO.InvalidDataException("Combat-site key rows present but the world module is unavailable; refusing a restore that would erase them.");
                }));
            _worldRuntime = new WorldRuntimeState(_adapter, _worldLoadHost, definitions, creation,
                lifetime, _worldPersistence.StateReady, admission);
            _worldReferences = new WorldReferenceResolver(_hub, creation, definitions, _worldPersistence, _worldLifetimeHost);
            _worldContent = new WorldContentService(_hub, definitions, new WorldAuthoringGate(definitions, creation, _worldPersistence.CanMutate), admission, () => { try { _story?.RefreshWorldDependencies(); } finally { _worldLifetimeHost?.MaintainActors(); } }, _ambientTraffic ??= new AmbientTrafficService(_hub), _unitProtection ??= new UnitProtectionService(_hub), _droneBays ??= new DroneBayService(_hub), _authoredDefinitions, authored, _siteDefinitions, _siteCoordinator, _shipDefinitions, _shipCoordinator,
                _siteCoordinator == null ? null : new WorldNativeEncounters(_adapter, assembly, fault => Logger.LogError(fault)), _wormholeDefinitions, _wormholeCoordinator);
            var selected = WorldNativeBindings.Methods.Where(m => m.Key == "worldPoiRead" || m.Key == "worldRecall" || m.Key == "worldCombatUpdate" || m.Key == "worldRemove" || m.Key == "worldSnapshot" || m.Key == "worldStore" || m.Key == "worldActiveUpdate" || m.Key == "worldCanTravel" || m.Key == "worldRoute" || m.Key == "worldBaseArrival" || m.Key == "worldCombatArrival" || m.Key == "worldSpawnPersistable" || m.Key == "worldSpawnUnit" || m.Key == "worldManagerStart" || m.Key == "worldManagerUpdate" || m.Key == "worldSecurityPatrol" || m.Key == "worldManagerInit" || m.Key == "worldInitializePoi" || m.Key == "worldInitializationComplete" || m.Key == "worldBaseAwake" || m.Key == "worldCombatAwake" || m.Key == "worldStoreLastX" || m.Key == "worldStorePosition" || m.Key == "worldIncomingReinforcements" || m.Key == "worldCreateSecurityPatrol" || m.Key == "worldStartTravel" || m.Key == "worldNextWaypoint" || m.Key == "worldTravelChild" || m.Key == "worldCheckLocalScene" || m.Key == "worldUnloadScene" || m.Key == "worldWaitUnload" || m.Key == "worldCancelTravel" || m.Key == "worldGenerate" || m.Key == "worldRegenerateGuards" || m.Key == "worldRegenerateCargo" || m.Key == "worldRegenerateSalvage" || m.Key == "worldRegenerateAsteroids" || m.Key == "worldRebuildStation" || m.Key == "worldJumpgateWave" || m.Key == "worldDeferGeneration" || m.Key == "worldPayloadUpdate" || m.Key == "worldPayloadTrigger" || m.Key == "worldPayloadSpawn" || m.Key == "worldPoiAddPersistable" || m.Key == "worldPoiRemovePersistable" || m.Key == "worldPoiAddUnit" || m.Key == "worldPoiRemoveUnit" || m.Key == "worldPoiAddPayload" || m.Key == "worldAddTriggered" || m.Key == "worldAddBudgetPayload" || m.Key == "worldAddFixedPayload" || m.Key == "worldActorAwake" || m.Key == "worldActorStart" || m.Key == "worldActorUpdate" || m.Key == "worldActorPhysics" || m.Key == "worldShipStart" || m.Key == "worldShipUpdate" || m.Key == "worldActorSetData" || m.Key == "worldShipSetData" || m.Key == "worldActorModules" || m.Key == "worldActorDamage" || m.Key == "worldShipDamage" || m.Key == "worldActorCollisionEnter" || m.Key == "worldActorCollisionStay" || m.Key == "worldPersistableStart" || m.Key == "worldPersistableUpdate" || m.Key == "worldBudgetBuilder" || m.Key == "worldSalvageReset" || m.Key == "worldSalvageAdd" || m.Key == "worldSalvageSlot" || m.Key == "worldSalvageDescriptor" || m.Key.StartsWith("worldEmpty", StringComparison.Ordinal) || m.Key.StartsWith("worldActorRoutine", StringComparison.Ordinal)).ToArray();
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
                    foreach (var key in targets.Keys.Where(key => key.StartsWith("worldEmpty", StringComparison.Ordinal)))
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
            _worldAvailable = true;
            _hub.SetCapability("world-save-protection", true, "Owned world state participates in coordinated saves.");
            _hub.SetCapability("world-load-protection", true, "Owned world state is restored before dependent content.");
            _hub.SetCapability("world-authoring", true, "Persistent Combat sites are available to authenticated providers in ready sessions.");
            if (authored != null)
                _hub.SetCapability("authored-systems", true, "Resource pocket systems are available to authenticated providers in ready sessions.");
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
        _worldAvailable = false;
        _worldReferences = null;
        try { _worldContent?.Dispose(); }
        finally { _authoredCoordinator?.Dispose(); _wormholeCoordinator?.Dispose(); _wormholeDefinitions?.Dispose(); _siteCoordinator?.Dispose(); _siteDefinitions?.Dispose(); _shipCoordinator?.Dispose(); _shipDefinitions?.Dispose(); StopWorldGuards(); }
        _authoredCoordinator = null; _siteCoordinator = null; _siteDefinitions = null; _shipCoordinator = null; _shipDefinitions = null;
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
                        finally { _authoredDefinitions?.Dispose(); _worldDefinitions?.Dispose(); }
                    }
                }
            }
        }
        // Keep published factory guards attached: teardown must not turn reserved nodes into vanilla.
    }
}
