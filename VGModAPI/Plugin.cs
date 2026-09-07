using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Menu;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

[BepInPlugin(ModApi.PluginId, "Vanguard Galaxy Mod API", "0.1.15")]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency("vgmodapi.qualification.guard", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    private LifecycleHub? _hub;
    private Harmony? _harmony;
    private GameAdapter? _adapter;
    private PersistenceService? _persistence;
    private MissionAdapter? _missions;
    private TravelNativeAdapter? _travel;
    private StoryNativeWorld? _storyWorld;
    private StoryContentService? _story;
    private StoryProtection? _protection;
    private StoryQuarantine? _quarantine;
    private IDisposable? _protectionSubscription;
    private bool _pendingProtectionRecovery;
    private bool _identityHooksBound;
    private ModInformationCatalog? _modCatalog;
    private ModMenuModule? _modMenu;
    private ModUpdateService? _updates;
    private ModUpdatePresenter? _updatePresenter;
    private BepInEx.Configuration.ConfigEntry<bool>? _updatesEnabled, _automaticUpdates;
    private Assembly? _inspectedGameAssembly;

    private void Start()
    {
        try { _modCatalog?.Refresh(); if (_modCatalog != null) _updates?.Sync(_modCatalog.Snapshot); }
        catch (Exception error) { Logger.LogWarning($"Mod information inventory could not be refreshed ({error.GetType().Name}); game services are unaffected."); }
    }
    private void Awake()
    {
        _hub = new LifecycleHub((owner, ex) => Logger.LogError($"Subscriber '{owner}' failed: {ex}"));
        _hub.SetCapability("session-lifecycle", false, "Not bound.");
        _hub.SetCapability("save-outcomes", false, "Not bound.");
        _hub.SetCapability("world-ready", false, "No universal POI/UI-ready guarantee; GameplayInitialized is narrower.");
        _hub.SetCapability("native-travel", false, "Not bound; experimental.");
        _hub.SetCapability("save-data", false, "Not initialized; experimental.");
        _hub.SetCapability("mission-continuity", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("mission-transitions", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("owned-story", false, "Not initialized; experimental.");
        _hub.SetCapability("story-protection", false, "Not bound.");
        ModApi.Missions = null;
        ModApi.Story = null;
        ModApi.Current = _hub;
        ModApi.Persistence = null;
        _modCatalog = new ModInformationCatalog(ModInformationSource.Snapshot);
        ModApi.Mods = _modCatalog;
        InitializeUpdates();
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Assembly-CSharp")
                ?? Assembly.Load("Assembly-CSharp");
            var hash = ReadAssemblyHash(assembly);
            Logger.LogInfo($"Game {UnityEngine.Application.version}, Unity {UnityEngine.Application.unityVersion}; assembly SHA-256: {hash}");
            if (hash != BindingCatalog.InspectedSha256)
                throw new NotSupportedException("Uninspected game assembly: lifecycle hooks disabled. Reverify adapter before adding support.");
            _inspectedGameAssembly = assembly;
            var bindings = new GameBindings(assembly);
            _adapter = new GameAdapter(_hub, bindings, ex => Logger.LogError($"Observer fault: {ex}"));
            _harmony = new Harmony(ModApi.PluginId);
            LifecyclePatches.Adapter = _adapter;
            SavePatches.Adapter = _adapter;
            if (Config.Bind("Missions", "Enabled", false, "Experimental observed mission transitions; use disposable saves until qualified.").Value &&
                Config.Bind("Missions", "IdentityContinuity", false, "Experimental exact-snapshot identity; requires API-managed saves.").Value)
            {
                InstallGroup("mission-continuity", bindings, BindingCatalog.MissionSnapshots,
                    new Dictionary<string, Type> { ["missionSnapshot"] = typeof(MissionSerializationPatches) });
                _identityHooksBound = _hub.Capabilities.Any(c => c.Name == "mission-continuity" && c.Available);
                if (_identityHooksBound) _hub.SetCapability("mission-continuity", false, "Identity provider not initialized.");
            }
            InstallGroup("session-lifecycle", bindings, BindingCatalog.Session, new Dictionary<string, Type>
            {
                ["load"] = typeof(LifecyclePatches.Load), ["loadRoutine"] = typeof(LifecyclePatches.LoadRoutine),
                ["loadFailure"] = typeof(LifecyclePatches.LoadFailure), ["newPlayer"] = typeof(LifecyclePatches.NewPlayer),
                ["scenes"] = typeof(LifecyclePatches.Scenes), ["menu"] = typeof(LifecyclePatches.Menu),
                ["splash"] = typeof(LifecyclePatches.Menu), ["gameplay"] = typeof(LifecyclePatches.Gameplay)
            });
            InstallGroup("save-outcomes", bindings, BindingCatalog.Saves, new Dictionary<string, Type>
            {
                ["store"] = typeof(SavePatches.Store), ["writeFile"] = typeof(SavePatches.WriteFile),
                ["writeMetadata"] = typeof(SavePatches.WriteMetadata), ["storeFailure"] = typeof(SavePatches.StoreFailure)
            });
            // Load safety, not a feature: an owned mission restored from a save must not progress or
            // pay out while nobody vouches for it, and that is true whether or not the story module is
            // enabled. Bound before anything else story-related, and on by default.
            InstallStoryProtection(assembly, bindings);
            if (Config.Bind("Travel", "Enabled", false, "Experimental native travel and station observation; use disposable saves until qualified.").Value)
                InstallTravel(assembly, bindings);
        }
        catch (Exception ex)
        {
            // Stop observation even if a failed rollback leaves a detour installed.
            _adapter?.Guard(() => throw new InvalidOperationException("Adapter installation failed.", ex));
            try { _harmony?.UnpatchSelf(); }
            catch (Exception cleanupError) { Logger.LogError($"Patch rollback failed: {cleanupError}"); }
            _hub.SetCapability("session-lifecycle", false, ex.Message);
            _hub.SetCapability("save-outcomes", false, ex.Message);
            Logger.LogError(ex);
        }
        // Subscription order is contractual: coordinated owners restore before mission PlayerReady identity seeding.
        InitializePersistence();
        InitializeMissions();
        InitializeStory();
        InitializeModMenu();
        Logger.LogInfo("VGModAPI " + Info.Metadata.Version + ": experimental, NOT runtime-qualified. Query capabilities; startup does not prove compatibility.");
    }

    private void InitializeUpdates()
    {
        try
        {
            _updatesEnabled = Config.Bind("ModUpdates", "Enabled", true, "Allow explicit update checks. No network unless manually confirmed or Automatic is enabled. Disable to stop future checks; in-flight requests may finish.");
            _automaticUpdates = Config.Bind("ModUpdates", "Automatic", false, "Opt in to HTTPS feed requests for all declared API consumers, exposing IP and feed paths to GitHub hosts/redirects. No saves, profile, machine ID or full inventory sent. Six-hour success interval; no downloads or installs.");
            _updates = new ModUpdateService(new HttpModFeedTransport(), new ModUpdateCache(Path.Combine(Paths.CachePath, "VGModAPI-updates-v1")))
                { Enabled = _updatesEnabled.Value, Automatic = _automaticUpdates.Value };
            _updatePresenter = new ModUpdatePresenter(_updates, value => _automaticUpdates.Value = value);
        }
        catch (Exception error) { Logger.LogWarning("Update checker unavailable (" + error.GetType().Name + "); offline inventory is unaffected."); }
    }

    private void InitializeModMenu()
    {
        _hub!.SetCapability("mod-information-menu", false, "Not bound; local catalog remains available.");
        try
        {
            if (!Config.Bind("ModInformation", "MenuEnabled", true, "Show the Mods entry on the inspected native main menu. Inventory is offline; optional update requests have separate configuration and confirmation. Disable if another menu replacement conflicts.").Value)
            {
                _hub.SetCapability("mod-information-menu", false, "Disabled by configuration; local catalog remains available.");
                Logger.LogInfo("Mods menu disabled by configuration; ModApi.Mods remains available.");
                return;
            }
            var assembly = _inspectedGameAssembly
                ?? throw new NotSupportedException("No inspected game assembly; local catalog remains available.");
            _modMenu = new ModMenuModule(assembly, _modCatalog!, () => string.Join("\n", _hub.Capabilities.Select(capability =>
                capability.Name + ": " + capability.Detail)), DisableModMenu, _updatePresenter);
            _hub.SetCapability("mod-information-menu", true, "Inspected native menu binding; UI qualification pending.");
        }
        catch (Exception error) { DisableModMenu(error); }
    }

    private void DisableModMenu(Exception error)
    {
        var menu = _modMenu; _modMenu = null;
        try { menu?.Dispose(); }
        catch (Exception) { /* UI cleanup must not fault gameplay/save observation. */ }
        try
        {
            _hub?.SetCapability("mod-information-menu", false, "Menu unavailable (" + error.GetType().Name + "); local catalog remains available.");
            Logger.LogWarning("Mods menu unavailable (" + error.GetType().Name + "). ModApi.Mods remains available; game/save services are unaffected. Check the inspected menu/input layout or disable ModInformation.MenuEnabled.");
        }
        catch (Exception) { /* Diagnostic sinks must not propagate UI errors into the game. */ }
    }

    private void LateUpdate()
    {
        try
        {
            if (_updates != null)
            {
                _updates.Enabled = _updatesEnabled!.Value;
                _updates.Automatic = _automaticUpdates!.Value;
                _updates.Pump();
            }
        }
        catch (Exception error)
        {
            try { _updates?.Dispose(); } catch (Exception) { }
            _updates = null;
            Logger.LogWarning("Update checker stopped (" + error.GetType().Name + "); offline inventory remains available.");
        }
        try { _modMenu?.Poll(); }
        catch (Exception error) { DisableModMenu(error); }
    }

    private void InitializePersistence()
    {
        if (!Config.Bind("Persistence", "Enabled", true, "Enable API-managed mod save data. Experimental; use disposable saves until qualified.").Value)
        { _hub!.SetCapability("save-data", false, "Disabled by configuration."); return; }
        if (_hub!.Capabilities.Count(c => (c.Name == "session-lifecycle" || c.Name == "save-outcomes") && c.Available) != 2)
        {
            _hub.SetCapability("save-data", false, "Lifecycle capabilities unavailable.");
            return;
        }
        try
        {
            var root = Config.Bind("Persistence", "Root", Path.Combine(Paths.ConfigPath, "VGModAPI-state"), "Folder for mod save data: use a short absolute path without links; do not share across installations.").Value;
            if (!Path.IsPathRooted(root)) throw new ArgumentException("Persistence root must be absolute.");
            var saves = (string)AccessTools.Field(AccessTools.TypeByName("Source.Util.SaveGame"), "SavesPath").GetValue(null)!;
            var files = new PersistenceFiles(saves);
            _persistence = new PersistenceService(_hub, new GenerationStore(root), files.Canonical, files.HashFile);
            ModApi.Persistence = _persistence;
            _hub.SetCapability("save-data", true, "Experimental API-managed saves enabled; full in-game acceptance remains pending.");
        }
        catch (Exception error)
        {
            _hub.SetCapability("save-data", false, "Persistence initialization failed: " + error.GetType().Name);
            Logger.LogError("API-managed saves unavailable: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void InitializeMissions()
    {
        if (!Config.Bind("Missions", "Enabled", false, "Experimental observed mission transitions; use disposable saves until qualified.").Value) return;
        if (!_hub!.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available))
        { _hub.SetCapability("mission-transitions", false, "Lifecycle capability unavailable."); return; }
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            _missions = new MissionAdapter(_hub, new MissionBindings(assembly), ex => Logger.LogError($"Mission observer fault: {ex}"));
            MissionPatches.Adapter = _missions;
            InstallGroup("mission-transitions", new GameBindings(assembly), BindingCatalog.Missions,
                BindingCatalog.Missions.ToDictionary(binding => binding.Key, binding => binding.Key.StartsWith("missionSweep", StringComparison.Ordinal) ? typeof(MissionSweepPatches) : typeof(MissionPatches)));
            if (_hub.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available))
            {
                ModApi.Missions = _missions.Events;
                InitializeMissionIdentity(assembly);
            }
            else { _missions.Dispose(); _missions = null; MissionPatches.Adapter = null; }
        }
        catch (Exception error)
        {
            _missions?.Dispose(); _missions = null; MissionPatches.Adapter = null;
            _hub.SetCapability("mission-transitions", false, "Mission binding failed: " + error.Message);
            Logger.LogError(error);
        }
    }

    private void InitializeMissionIdentity(Assembly assembly)
    {
        if (!Config.Bind("Missions", "IdentityContinuity", false, "Experimental exact-snapshot identity; requires API-managed saves.").Value) return;
        if (_persistence == null) { _hub!.SetCapability("mission-continuity", false, "API-managed saves unavailable."); return; }
        try
        {
            if (!_identityHooksBound) throw new InvalidOperationException("Early snapshot hooks unavailable.");
            _missions!.EnableIdentity(_persistence, new MissionJsonBindings(assembly));
            _hub!.SetCapability("mission-continuity", true, "Experimental exact-snapshot identity enabled; no persistent history ownership.");
        }
        catch (Exception error)
        {
            _missions!.DisableIdentity(); _hub!.SetCapability("mission-continuity", false, "Mission identity initialization failed: " + error.Message);
            Logger.LogError(error);
        }
    }

    /// <summary>
    /// Binds the owned-story module. It is constructed BEFORE any session, because its persistence
    /// owner cannot be registered once one is live, and because vanilla resolves saved story payloads
    /// out of its catalog while it deserializes: definitions registered from a consumer's Awake are
    /// installed here, ahead of any load. A binding failure leaves the capability unavailable and the
    /// public surface null; it never leaves a half-installed catalog behind.
    /// </summary>
    /// <summary>
    /// Installs the quarantine guards. They exist for content the API wrote into a PREVIOUS session's
    /// save, so they are independent of Story/Enabled and of whether the story module binds at all;
    /// with nothing admitted they refuse every owned identifier, which is the safe default.
    /// </summary>
    private void InstallStoryProtection(Assembly assembly, GameBindings bindings)
    {
        if (!Config.Bind("Story", "Protection", true,
            "Load safety for API-owned story missions restored from a save: they cannot progress or pay out unless the owning module vouches for them. Disable only to diagnose.").Value)
        { _hub!.SetCapability("story-protection", false, "Disabled by configuration; owned story content in a save would be unguarded."); return; }
        try
        {
            var guard = new StoryProtectionGuard(assembly);
            _protection = new StoryProtection(_hub!.CheckThread);
            var player = AccessTools.Field(AccessTools.TypeByName(BindingCatalog.Player), "current");
            var missions = AccessTools.Field(AccessTools.TypeByName(BindingCatalog.Player), "missions");
            _quarantine = new StoryQuarantine(guard, _protection,
                () => player.GetValue(null) is { } current && missions.GetValue(current) is System.Collections.IEnumerable held
                    ? held.Cast<object>().ToArray() : Array.Empty<object>(),
                error => Logger.LogError("Story protection fault: " + error),
                reason =>
                {
                    _hub!.SetCapability("story-protection", false,
                        "Owned story content is refused because the guard could not decide: " + reason);
                    Logger.LogError("Story protection degraded, refusing owned story content: " + reason);
                });
            // The guards are session-scoped like the content they protect, and they say so whether or
            // not the story module exists: a new load starts with nothing vouched for.
            _protectionSubscription = _hub!.Subscribe("vgmodapi.story-protection", e =>
            {
                if (e.Kind == LifecycleEventKind.PlayerReady)
                {
                    if (!_pendingProtectionRecovery || _quarantine == null) return;
                    if (!_quarantine.VerifyHealthy())
                    {
                        _hub!.SetCapability("story-protection", false,
                            "Owned story content is refused because the guard could not decide: " + _quarantine.DegradedReason);
                        return;
                    }
                    _pendingProtectionRecovery = false;
                    _hub!.SetCapability("story-protection", true, "Bound to inspected assembly; in-game qualification pending.");
                    return;
                }
                if (e.Kind is not (LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated
                    or LifecycleEventKind.SessionStartFailed)) return;
                _protection?.WithdrawAll("a new session started; nothing has been vouched for yet");
                // A degraded guard is NOT restored by a session boundary alone: the world it failed to
                // read is not loaded yet. Recovery is attempted when the player is ready, and only a
                // scan that actually completes restores the capability.
                if (e.Kind == LifecycleEventKind.SessionStarting) _pendingProtectionRecovery = _quarantine?.DegradedReason != null;
            });
            StoryProtectionPatches.Quarantine = _quarantine;
            InstallGroup("story-protection", bindings, BindingCatalog.StoryProtection, new Dictionary<string, Type>
            {
                ["storyGuardUpdate"] = typeof(StoryProtectionPatches.MissionUpdate),
                ["storyGuardClaim"] = typeof(StoryProtectionPatches.ClaimRewards),
                ["storyGuardComplete"] = typeof(StoryProtectionPatches.CompleteMission),
                ["storyGuardFail"] = typeof(StoryProtectionPatches.MissionFailed),
                ["storyGuardRetry"] = typeof(StoryProtectionPatches.RetryAsNextMission),
                ["storyGuardAbandon"] = typeof(StoryProtectionPatches.AbandonMission),
                ["storyGuardTrigger"] = typeof(StoryProtectionPatches.ProcessMissionTrigger)
            });
            if (!_hub.Capabilities.Any(c => c.Name == "story-protection" && c.Available))
            { StoryProtectionPatches.Quarantine = null; _quarantine = null; _protection = null; }
        }
        catch (Exception error)
        {
            StoryProtectionPatches.Quarantine = null; _quarantine = null; _protection = null;
            _hub!.SetCapability("story-protection", false, "Story protection unavailable: " + error.Message);
            Logger.LogError("Owned story content in a save would be unguarded: " + error);
        }
    }

    private void InitializeStory()
    {
        if (!Config.Bind("Story", "Enabled", false, "Experimental API-owned story content installed into the game's catalog; use disposable saves until qualified.").Value)
        { _hub!.SetCapability("owned-story", false, "Disabled by configuration."); return; }
        if (_persistence == null) { _hub!.SetCapability("owned-story", false, "API-managed saves unavailable."); return; }
        if (!_hub!.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available))
        { _hub.SetCapability("owned-story", false, "Lifecycle capability unavailable."); return; }
        // Owning content the guards could not protect is worse than owning none: without them an
        // orphan from a later save would run unguarded, so the module does not install content at all.
        if (_protection == null)
        { _hub.SetCapability("owned-story", false, "Story protection unavailable; owned content would be unguarded in a later session."); return; }
        // Without observed mission transitions a completion could never be recorded, and the only
        // alternative would be letting a caller declare one. The capability stays off instead.
        if (_missions == null || !_hub.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available))
        { _hub.SetCapability("owned-story", false, "Observed mission transitions unavailable; owned story outcomes could not be recorded."); return; }
        try
        {
            var assembly = Assembly.Load("Assembly-CSharp");
            _storyWorld = new StoryNativeWorld(new StoryNativeBindings(assembly), _hub.CheckThread,
                error => Logger.LogError("Story world fault: " + error));
            // Outcomes are observed through the same mission boundary consumers see; without it the
            // module can still install and offer, but completions cannot be recorded at all.
            _story = new StoryContentService(_persistence, _hub, StoryHostAuthentication.Resolve, null, _hub.CheckThread,
                _storyWorld, _missions?.Events,
                (detail, available) => _hub!.SetCapability("owned-story", available, detail), _protection,
                () => _quarantine?.Healthy ?? false);
            ModApi.Story = _story;
            // Only a module that exists can say what a UI abandon or retry of owned content means.
            if (_quarantine != null) _quarantine.Transactions = _story;
            _hub.SetCapability("owned-story", true, "Experimental owned story content enabled; native qualification pending.");
        }
        catch (Exception error)
        {
            ModApi.Story = null;
            if (_quarantine != null) _quarantine.Transactions = null;
            _story?.Dispose(); _story = null;
            _storyWorld?.Dispose(); _storyWorld = null;
            _hub!.SetCapability("owned-story", false, "Story binding failed: " + error.Message);
            Logger.LogError(error);
        }
    }

    internal static string ReadAssemblyHash(Assembly assembly)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(assembly.Location);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private void InstallGroup(string name, GameBindings bindings, MethodBinding[] catalog, Dictionary<string, Type> patches)
    {
        var touched = new List<MethodInfo>();
        try
        {
            var targets = bindings.Resolve(catalog); // Resolve whole group before touching anything.
            foreach (var binding in catalog)
            {
                var target = targets[binding.Key];
                var type = patches[binding.Key];
                HarmonyMethod? Hook(string method)
                {
                    var info = type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
                    return info == null ? null : new HarmonyMethod(info);
                }
                touched.Add(target);
                _harmony!.Patch(target, prefix: Hook("Prefix"), postfix: Hook("Postfix"), finalizer: Hook("Finalizer"));
            }
            _hub!.SetCapability(name, true, "Bound to inspected assembly; in-game qualification pending.");
        }
        catch (Exception ex)
        {
            foreach (var method in touched) _harmony!.Unpatch(method, HarmonyPatchType.All, ModApi.PluginId);
            _hub!.SetCapability(name, false, "Binding failed: " + ex.Message);
            Logger.LogError($"Capability {name} disabled: {ex}");
        }
    }

    private void InstallTravel(Assembly assembly, GameBindings bindings)
    {
        _hub!.SetCapability("native-travel", false, "Not bound.");
        var touched = new List<MethodInfo>();
        try
        {
            var adapter = new TravelNativeAdapter(new TravelNativeBindings(assembly),
                (owner, ex) => Logger.LogError($"{owner} observer fault: {ex}"));
            TravelPatches.Adapter = adapter;
            var resolved = bindings.Resolve(BindingCatalog.Travel);
            var managerBase = assembly.GetType("Behaviour.Managers.BasePoiManager", true)!;
            var arrivals = TravelArrivalBindings.Resolve(managerBase);
            HarmonyMethod? Hook(Type holder, string method)
            {
                var info = holder.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
                return info == null ? null : new HarmonyMethod(info);
            }
            void Patch(MethodInfo target, Type holder)
            {
                touched.Add(target);
                _harmony!.Patch(target, prefix: Hook(holder, "Prefix"), postfix: Hook(holder, "Postfix"), finalizer: Hook(holder, "Finalizer"));
            }
            // Whole-group resolution before touching anything: a type-load failure must
            // disable the entire travel group, never leave partial hooks installed.
            foreach (var arrival in arrivals) Patch(arrival, typeof(TravelPatches.Arrival));
            foreach (var binding in BindingCatalog.Travel)
            {
                var holder = binding.Key switch
                {
                    "route" => typeof(TravelPatches.Route),
                    "cancel" => typeof(TravelPatches.Cancel),
                    "unloadDeparted" => typeof(TravelPatches.Departure),
                    "jumpGate" => typeof(TravelPatches.JumpGate),
                    "jumpWormhole" => typeof(TravelPatches.JumpWormhole),
                    "travelNextWaypoint" => typeof(TravelPatches.RouteBoundary),
                    "inSystemWarp" => typeof(TravelPatches.InSystemWarp),
                    "dockRequest" => typeof(TravelPatches.DockRequest),
                    "dockAssign" => typeof(TravelPatches.DockAssign),
                    "dock" => typeof(TravelPatches.Dock),
                    "undock" => typeof(TravelPatches.Undock),
                    "emergencyUndock" => typeof(TravelPatches.EmergencyUndock),
                    "interiorAwake" => typeof(TravelPatches.InteriorAwake),
                    "interiorStart" => typeof(TravelPatches.InteriorStart),
                    "interiorDestroy" => typeof(TravelPatches.InteriorDestroy),
                    _ => throw new InvalidOperationException("Unknown travel binding: " + binding.Key)
                };
                Patch(resolved[binding.Key], holder);
            }
            _travel = adapter;
            ModApi.Travel = adapter.Events;
            ModApi.Station = adapter.Station;
            _hub!.SetCapability("native-travel", true, "Bound to inspected assembly; in-game qualification pending.");
        }
        catch (Exception ex)
        {
            foreach (var method in touched) { try { _harmony!.Unpatch(method, HarmonyPatchType.All, ModApi.PluginId); } catch { } }
            TeardownTravel("Binding failed: " + ex.Message, ex);
        }
    }

    private void TeardownTravel(string reason, Exception? ex = null)
    {
        if (_travel != null)
        {
            _travel.SetSession(null); _travel.Dispose(); _travel = null;
        }
        TravelPatches.Adapter = null;
        ModApi.Travel = null; ModApi.Station = null;
        _hub?.SetCapability("native-travel", false, reason);
        Logger.LogError(ex == null ? reason : reason + " " + ex);
    }

    private void Update()
    {
        _adapter?.Poll(); _missions?.Poll();
        if (_travel != null)
        {
            // A genuine travel adapter fault (main-thread violation) disables the whole group.
            if (_travel.IsFaulted)
            {
                TeardownTravel("Travel observer fault; capability disabled.");
                return;
            }
            var session = _hub?.CurrentSession;
            var active = session != null && session.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;
            _travel.SetSession(session != null && active ? session.Id : (Guid?)null);
            if (active)
            {
                try
                {
                    var travel = _travel.Bindings.TravelManager();
                    var player = travel == null ? null : _travel.Bindings.Player;
                    var manager = travel == null ? null : _travel.Bindings.LocalManager(travel);
                    _travel.Tick(player, manager);
                }
                catch (Exception error) { Logger.LogError($"Travel tick fault: {error}"); }
            }
        }
    }
    private void OnDestroy()
    {
        try { _updates?.Dispose(); } catch (Exception) { }
        try { _modMenu?.Dispose(); } catch (Exception error) { DisableModMenu(error); }
        _modMenu = null;
        _modCatalog?.Dispose();
        ModApi.Mods = null;
        _adapter?.Guard(() => _adapter.Invalidate("API shutting down."));
        _missions?.Dispose(); _missions = null;
        MissionPatches.Adapter = null; ModApi.Missions = null;
        _travel?.SetSession(null); _travel?.Dispose(); _travel = null;
        TravelPatches.Adapter = null; ModApi.Travel = null; ModApi.Station = null;
        // The story module owns catalog entries AND a persistence owner, so it is torn down before
        // the coordinator: uninstalling its content cannot race an owner that is already gone, and
        // disposing the coordinator first would pause coordinated saves for every other owner.
        ModApi.Story = null;
        try { _story?.Dispose(); } catch (Exception error) { Logger.LogError("Story shutdown failed: " + error); }
        // The guards outlive the module on purpose: content it installed may still be held.
        if (_quarantine != null) _quarantine.Transactions = null;
        _protection?.WithdrawAll("the story module was shut down");
        _protectionSubscription?.Dispose(); _protectionSubscription = null;
        try { _storyWorld?.Dispose(); } catch (Exception error) { Logger.LogError("Story world shutdown failed: " + error); }
        _story = null; _storyWorld = null;
        _persistence?.Dispose();
        ModApi.Persistence = null;
        _harmony?.UnpatchSelf();
        LifecyclePatches.Adapter = null;
        SavePatches.Adapter = null;
        ModApi.Current = null;
        _hub?.Dispose();
        _adapter = null;
    }
}
