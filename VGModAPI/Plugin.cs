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

[BepInPlugin(ModApi.PluginId, "Mod API", PluginBuildVersion.Value)]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency("vgmodapi.qualification.guard", BepInDependency.DependencyFlags.SoftDependency)]
public sealed partial class Plugin : BaseUnityPlugin
{
    private LifecycleHub? _hub;
    private Harmony? _harmony;
    private GameAdapter? _adapter;
    private PersistenceService? _persistence;
    private MissionAdapter? _missions;
    private TravelNativeAdapter? _travel;
    private RecipeCatalogService? _recipes;
    private RecipeQuoteService? _recipeQuotes;
    private CraftingJobService? _craftingJobs;
    private CraftingJobObserver? _craftingJobObserver;
    private Harmony? _craftingJobHarmony;
    private CraftingCommandService? _craftingCommands;
    private Harmony? _craftingCommandHarmony;
    private RecipeCatalogNativeSource? _craftingCommandSource;
    private BoardingObserver? _boarding;
    private BoardingRuleAdapter? _boardingRules;
    private BoardingCommandService? _boardingCommands;
    private BoardingCombatService? _boardingCombat;
    private DungeonSettlementService? _dungeonSettlement;
    private DungeonRewardService? _dungeonRewards;
    private DungeonContentService? _dungeons;
    private DungeonStateStore? _dungeonState;
    private DungeonContentAdapter? _dungeonAdapter;
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
        _hub.SetCapability("recipe-catalog", false, "Disabled or not bound; experimental.");
        _hub.SetCapability("recipe-quotes", false, "Disabled or not bound; experimental.");
        ModApi.RecipeQuotes = null;
        ModApi.CraftingJobs = null;
        ModApi.CraftingCommands = null;
        ModApi.ForgeUi = null;
        ModApi.Hud = null;
        _hub.SetCapability("hud", false, "Disabled or not bound; experimental.");
        _hub.SetCapability("forge-ui", false, "Disabled or not bound; experimental.");
        _hub.SetCapability("crafting-commands", false, "Disabled or not bound; experimental.");
        _hub.SetCapability("crafting-jobs", false, "Disabled or not bound; experimental.");
        _hub.SetCapability("save-data", false, "Not initialized; experimental.");
        _hub.SetCapability("mission-continuity", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("mission-transitions", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("owned-story", false, "Not initialized; experimental.");
        _hub.SetCapability("story-protection", false, "Not bound.");
        _hub.SetCapability("boarding-observation", false, "Disabled by configuration; experimental.");
        ModApi.Recipes = null;
        ModApi.Boarding = null;
        ModApi.BoardingRules = null;
        ModApi.BoardingCommands = null;
        ModApi.BoardingTactics = null; ModApi.BoardingCombat = null;
        _hub.SetCapability("boarding-tactics", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("boarding-combat", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("boarding-commands", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("boarding-rules", false, "Disabled by configuration; experimental.");
        ModApi.Missions = null;
        ModApi.Story = null;
        ModApi.Bars = null;
        _hub.SetCapability("owned-bars", false, "Not initialized; experimental.");
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
            if (Config.Bind("Hud", "Enabled", false, "Experimental shared HUD buttons and information panels.").Value) InstallHud(assembly);
            if (Config.Bind("Recipes", "Enabled", false, "Experimental recipe catalog, advisory quotes and Forge/refinery job observations.").Value)
                InstallRecipes(assembly);
            if (Config.Bind("Boarding", "Enabled", false, "Experimental boarding observation and rules on the inspected game build.").Value)
            {
                InstallBoarding(bindings);
                InstallBoardingRules(bindings);
                InstallBoardingCommands(bindings);
                InstallBoardingTactics(bindings);
                InstallDungeonRewards(bindings);
            }
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
            TeardownHud();
            TeardownForgeUi();
            TeardownCraftingJobs();
            try { _harmony?.UnpatchSelf(); }
            catch (Exception cleanupError) { Logger.LogError($"Patch rollback failed: {cleanupError}"); }
            _hub.SetCapability("session-lifecycle", false, ex.Message);
            _hub.SetCapability("save-outcomes", false, ex.Message);
            Logger.LogError(ex);
        }
        // Subscription order is contractual: coordinated owners restore before mission PlayerReady identity seeding.
        InitializePersistence();
        InitializeDungeons();
        InitializeMissions();
        InitializeStory();
        InitializeBars();
        InitializeModMenu();
        Logger.LogInfo("VGModAPI " + Info.Metadata.Version + ": experimental, NOT runtime-qualified. Query capabilities; startup does not prove compatibility.");
    }

    private void InitializeUpdates()
    {
        try
        {
            _updates = new ModUpdateService(new HttpModFeedTransport(), new ModUpdateCache(Path.Combine(Paths.CachePath, "VGModAPI-updates-v1")))
                { Enabled = true, Automatic = true };
            _updatePresenter = new ModUpdatePresenter(_updates);
        }
        catch (Exception error) { Logger.LogWarning("Update checker unavailable (" + error.GetType().Name + "); offline inventory is unaffected."); }
    }

    private void InitializeModMenu()
    {
        _hub!.SetCapability("mod-information-menu", false, "Not bound; local catalog remains available.");
        try
        {
            if (!Config.Bind("ModInformation", "MenuEnabled", true, "Show the Mods entry on the inspected native main menu. Update checks run automatically without downloading or installing mods. Disable if another menu replacement conflicts.").Value)
            {
                _hub.SetCapability("mod-information-menu", false, "Disabled by configuration; local catalog remains available.");
                Logger.LogInfo("Mods menu disabled by configuration; ModApi.Mods remains available.");
                return;
            }
            var assembly = _inspectedGameAssembly
                ?? throw new NotSupportedException("No inspected game assembly; local catalog remains available.");
            _modMenu = new ModMenuModule(assembly, _modCatalog!, DisableModMenu, _updatePresenter);
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
                ["storyGuardTrigger"] = typeof(StoryProtectionPatches.ProcessMissionTrigger),
                ["storyGuardScriptedTrigger"] = typeof(StoryProtectionPatches.ProcessMissionTrigger)
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

    private void InstallRecipes(System.Reflection.Assembly assembly)
    {
        try
        {
            var translate = assembly.GetType("Source.Util.Translation", true)!.GetMethod("Translate", new[] { typeof(string), typeof(object[]) })
                ?? throw new MissingMethodException("Translation.Translate");
            var source = new RecipeCatalogNativeSource(assembly,
                (prefab, type) => prefab is UnityEngine.GameObject gameObject && gameObject != null ? gameObject.GetComponent(type) : null,
                text => (string)translate.Invoke(null, new object[] { text, Array.Empty<object>() })!);
            _recipes = new RecipeCatalogService(_hub!, source, error => Logger.LogError(error));
            ModApi.Recipes = _recipes;
            _hub!.SetCapability("recipe-catalog", true, "Experimental read-only definitions; not runtime-qualified.");
            try
            {
                source.BindQuotes();
                _recipeQuotes = new RecipeQuoteService(_hub, source, error => Logger.LogError(error));
                ModApi.RecipeQuotes = _recipeQuotes;
                _hub.SetCapability("recipe-quotes", true, "Experimental advisory requirements; not runtime-qualified.");
            }
            catch (Exception quoteError)
            {
                _recipeQuotes?.Dispose(); _recipeQuotes = null; ModApi.RecipeQuotes = null;
                _hub.SetCapability("recipe-quotes", false, "Recipe quote binding failed."); Logger.LogError(quoteError);
            }
            if (_recipeQuotes != null) { InstallCraftingJobs(assembly, source); InstallForgeUi(assembly, source); }
        }
        catch (Exception error)
        {
            _recipes?.Dispose(); _recipes = null; ModApi.Recipes = null;
            _hub!.SetCapability("recipe-catalog", false, "Recipe catalog binding failed."); Logger.LogError(error);
        }
    }
    private void InstallCraftingJobs(Assembly assembly, RecipeCatalogNativeSource source)
    {
        try
        {
            var methods = CraftingJobBindings.Validate(assembly);
            _craftingJobs = new CraftingJobService(_hub!, source, (owner, error) => Logger.LogError(owner + ": " + error));
            _craftingJobObserver = new CraftingJobObserver(_hub!, _craftingJobs, source);
            CraftingJobPatches.Keys = CraftingJobBindings.Hooks.ToDictionary(spec => (MethodBase)methods[spec.Key], spec => spec.Key);
            CraftingJobPatches.Observer = _craftingJobObserver;
            _craftingJobHarmony = new Harmony(ModApi.PluginId + ".crafting-jobs");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var prefix = new HarmonyMethod(typeof(CraftingJobPatches).GetMethod("Prefix", flags));
            foreach (var spec in CraftingJobBindings.Hooks)
            {
                var name = spec.ReturnType == "System.Void" ? "VoidFinalizer" : spec.ReturnType == "System.Boolean" ? "BoolFinalizer" : "ObjectFinalizer";
                _craftingJobHarmony.Patch(methods[spec.Key], prefix: prefix, finalizer: new HarmonyMethod(typeof(CraftingJobPatches).GetMethod(name, flags)));
            }
            _craftingJobs.SetAvailable(true); ModApi.CraftingJobs = _craftingJobs;
            InstallCraftingCommands(assembly, source);
        }
        catch (Exception error) { TeardownCraftingJobs(); Logger.LogError(error); }
    }
    private void TeardownCraftingJobs()
    {
        TeardownCraftingCommands();
        CraftingJobPatches.Observer = null;
        _craftingJobObserver?.Dispose(); _craftingJobObserver = null;
        _craftingJobs?.Dispose(); _craftingJobs = null; ModApi.CraftingJobs = null;
        CraftingJobPatches.Keys = new Dictionary<MethodBase, string>();
        try { _craftingJobHarmony?.UnpatchSelf(); } catch (Exception error) { Logger.LogError(error); }
        _craftingJobHarmony = null;
        _hub?.SetCapability("crafting-jobs", false, "Crafting job observation unavailable.");
    }
    private void InstallCraftingCommands(Assembly assembly, RecipeCatalogNativeSource source)
    {
        try
        {
            if (!Config.Bind("Recipes", "CommandsEnabled", false, "Experimental crafting mutations; requires recipe/job observation and disposable-save qualification.").Value) return;
            var methods = CraftingCommandBindings.Validate(assembly);
            _craftingCommandSource = source;
            source.CommandSession = () => _hub?.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _adapter?.IsBoundPlayer(source.NativePlayer) == true ? _hub.CurrentSession.Id : null;
            source.CommandObserver = _craftingJobObserver; source.CommandJobEvents = _craftingJobs;
            source.CommandReport = error => Logger.LogError(error);
            source.RefreshCommandUi = new CraftingCommandUiRefresh(assembly, methods).Refresh;
            _craftingCommands = new CraftingCommandService(_hub!, _craftingJobs!, source, error => Logger.LogError(error));
            CraftingCommandPatches.Service = _craftingCommands;
            _craftingCommandHarmony = new Harmony(ModApi.PluginId + ".crafting-commands");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            foreach (var spec in CraftingCommandBindings.Serialization)
                _craftingCommandHarmony.Patch(methods[spec.Key], prefix: new HarmonyMethod(typeof(CraftingCommandPatches).GetMethod("Prefix", flags)),
                    finalizer: new HarmonyMethod(typeof(CraftingCommandPatches).GetMethod("Finalizer", flags)));
            _craftingCommands.SetAvailable(true); ModApi.CraftingCommands = _craftingCommands;
            _hub!.SetCapability("crafting-commands", true, "Experimental guarded commands; not runtime-qualified.");
        }
        catch (Exception error) { TeardownCraftingCommands(); Logger.LogError(error); }
    }
    private void TeardownCraftingCommands()
    {
        CraftingCommandPatches.Service = null;
        _craftingCommands?.Dispose(); _craftingCommands = null; ModApi.CraftingCommands = null;
        if (_craftingCommandSource != null)
        {
            _craftingCommandSource.CommandSession = null; _craftingCommandSource.CommandObserver = null;
            _craftingCommandSource.CommandJobEvents = null; _craftingCommandSource.CommandReport = null; _craftingCommandSource.RefreshCommandUi = null;
            _craftingCommandSource = null;
        }
        try { _craftingCommandHarmony?.UnpatchSelf(); } catch (Exception error) { Logger.LogError(error); }
        _craftingCommandHarmony = null; _hub?.SetCapability("crafting-commands", false, "Crafting commands unavailable.");
    }
    private void InitializeDungeons()
    {
        _hub!.SetCapability("dungeon-content", false, "Experimental authored content is disabled.");
        if (_boarding == null || !Config.Bind("Dungeons", "Enabled", false, "Experimental authored dungeon content; requires boarding and API save data.").Value) return;
        try
        {
            if (_persistence == null) throw new NotSupportedException("API save data is required.");
            var bindings = new GameBindings(Assembly.Load("Assembly-CSharp"));
            var crewNative = new BoardingCommandNativeBindings(bindings, DungeonCrewResumeBindings.Hooks, DungeonPodResumeBindings.Members);
            var directiveType = bindings.Assembly.GetType("Source.CompartmentSystem.SimCrewDirective", true)!;
            var priorityType = bindings.Assembly.GetType("Source.CompartmentSystem.DirectivePriority", true)!;
            var filterType = bindings.Assembly.GetType("Source.CompartmentSystem.MovementOrderFilter", true)!;
            var directives = new DungeonDirectiveAdapter(crewNative, () => Activator.CreateInstance(directiveType)!,
                (kind, value) => Enum.ToObject(kind == "priority" ? priorityType : filterType, value));
            DungeonCrewResumePatches.Coordinator = new DungeonCrewResumeCoordinator(crewNative, new DungeonCrewResumeJson(bindings.Assembly), error => Logger.LogError(error), directives);
            InstallGroup("dungeon-crew-resume", bindings, DungeonCrewResumeBindings.Hooks, new Dictionary<string, Type>
            {
                ["crewResumeSave"] = typeof(DungeonCrewResumePatches.Save), ["crewResumeLoad"] = typeof(DungeonCrewResumePatches.Load),
                ["crewSimulationSave"] = typeof(DungeonCrewResumePatches.SimulationSave),
                ["crewSimulationLoad"] = typeof(DungeonCrewResumePatches.SimulationLoad), ["crewSimulationTick"] = typeof(DungeonCrewResumePatches.Tick)
            });
            if (!_hub.Capabilities.Any(c => c.Name == "dungeon-crew-resume" && c.Available)) throw new NotSupportedException("Crew save/load hooks unavailable.");
            _dungeonState = new DungeonStateStore(_hub, _persistence);
            _dungeonAdapter = new DungeonContentAdapter(_hub, bindings, _boarding, _dungeonState);
            _dungeons = new DungeonContentService(_hub, _dungeonAdapter.Catalogs(), _dungeonState, _dungeonAdapter.Bindings(), (owner, error) => Logger.LogError($"Dungeon provider '{owner}': {error}"),
                () => (_dungeonSettlement?.IsDispatchingCallbacks ?? false) || (_dungeonRewards?.IsEvaluating ?? false) || (_boardingCombat?.IsEvaluating ?? false) || (ModApi.BoardingRules?.IsEvaluating ?? false));
            DungeonContentPatches.Adapter = _dungeonAdapter; DungeonContentPatches.Json = new DungeonMarkerJson(bindings.Assembly);
            var patches = new Dictionary<string, Type>
            {
                ["dungeonEntered"] = typeof(DungeonContentPatches.Entered), ["dungeonGuardTick"] = typeof(DungeonContentPatches.GuardTick),
                ["dungeonResumeShip"] = typeof(DungeonContentPatches.Resumed), ["dungeonResumeLocation"] = typeof(DungeonContentPatches.Resumed),
                ["dungeonSerialization"] = typeof(DungeonContentPatches.Serialization),
                ["dungeonWalkCreated"] = typeof(DungeonContentPatches.WalkCreated),
                ["dungeonHazard"] = typeof(DungeonContentPatches.Hazard), ["dungeonReinforcements"] = typeof(DungeonContentPatches.Reinforcements),
                ["dungeonLocationSave"] = typeof(DungeonContentPatches.SaveLocation), ["dungeonLocationLoad"] = typeof(DungeonContentPatches.LoadLocation),
                ["dungeonShipLayout"] = typeof(DungeonContentPatches.ShipLayout), ["dungeonWalkLayout"] = typeof(DungeonContentPatches.WalkLayout),
                ["dungeonShipDefenders"] = typeof(DungeonContentPatches.Defenders), ["dungeonWalkDefenders"] = typeof(DungeonContentPatches.Defenders)
            };
            InstallGroup("dungeon-content", bindings, DungeonNativeSchema.Methods.Where(b => patches.ContainsKey(b.Key)).ToArray(), patches);
            if (!_hub.Capabilities.Any(c => c.Name == "dungeon-content" && c.Available)) throw new NotSupportedException("Dungeon hooks unavailable.");
            ModApi.Dungeons = _dungeons;
        }
        catch (Exception error)
        {
            StopDungeons(); _hub.SetCapability("dungeon-content", false, error.GetType().Name); Logger.LogError(error);
        }
    }
    private void StopDungeons()
    {
        DungeonCrewResumePatches.Coordinator?.Clear(); DungeonCrewResumePatches.Coordinator = null;
        DungeonContentPatches.Adapter = null; DungeonContentPatches.Json = null; ModApi.Dungeons = null;
        _dungeons?.Dispose(); _dungeons = null; _dungeonAdapter?.Dispose(); _dungeonAdapter = null; _dungeonState?.Dispose(); _dungeonState = null;
    }

    private void InstallDungeonRewards(GameBindings bindings)
    {
        _hub!.SetCapability("dungeon-rewards", false, "Boarding observation required; experimental.");
        if (_boarding == null) return;
        try
        {
            _dungeonRewards = new DungeonRewardService(_hub, (owner, error) => Logger.LogError($"Dungeon reward '{owner}': {error}"));
            _dungeonSettlement = new DungeonSettlementService(_hub, ModApi.Boarding!, (owner, error) => Logger.LogError($"Dungeon settlement '{owner}': {error}"));
            DungeonRewardPatches.Crew = new DungeonCrewObserver(bindings, _boarding, _dungeonSettlement, error => Logger.LogError(error));
            DungeonRewardPatches.Adapter = new DungeonRewardAdapter(_hub, bindings, _boarding, _dungeonRewards);
            InstallGroup("dungeon-rewards", bindings, DungeonSettlementBindings.Hooks, new Dictionary<string, Type>
            {
                ["settlementTerminal"] = typeof(DungeonRewardPatches.Terminal),
                ["settlementPrisonerScope"] = typeof(DungeonRewardPatches.PrisonerScope), ["settlementPrisoners"] = typeof(DungeonRewardPatches.Prisoners),
                ["settlementCrewSample"] = typeof(DungeonRewardPatches.CrewSample),
                ["settlementLoot"] = typeof(DungeonRewardPatches.Loot), ["settlementLootCount"] = typeof(DungeonRewardPatches.Count),
                ["settlementMasteryScope"] = typeof(DungeonRewardPatches.MasteryScope), ["settlementMastery"] = typeof(DungeonRewardPatches.Mastery)
            });
            if (!_hub.Capabilities.Any(c => c.Name == "dungeon-rewards" && c.Available)) throw new NotSupportedException("Reward hooks unavailable.");
            ModApi.DungeonRewards = _dungeonRewards;
            ModApi.DungeonSettlement = _dungeonSettlement;
        }
        catch (Exception error)
        {
            DungeonRewardPatches.Crew = null; _dungeonSettlement?.Dispose(); _dungeonSettlement = null; ModApi.DungeonSettlement = null;
            DungeonRewardPatches.Adapter = null; _dungeonRewards?.Dispose(); _dungeonRewards = null; ModApi.DungeonRewards = null;
            _hub.SetCapability("dungeon-rewards", false, error.GetType().Name); Logger.LogError(error);
        }
    }

    private void InstallBoardingTactics(GameBindings bindings)
    {
        if (_boarding == null || ModApi.Boarding == null || _boardingCommands == null) return;
        try
        {
            var tactics = new BoardingTacticalAdapter(_hub!, bindings, _boarding, ModApi.Boarding, _boardingCommands);
            BoardingTacticalPatches.Adapter = tactics;
            InstallGroup("boarding-tactics", bindings, BoardingTacticalBindings.Actions, BoardingTacticalBindings.Actions.ToDictionary(b => b.Key,
                b => b.ReturnType == "System.Boolean" ? typeof(BoardingTacticalPatches.BoolAction) : typeof(BoardingTacticalPatches.VoidAction)));
            if (!_hub!.Capabilities.Any(c => c.Name == "boarding-tactics" && c.Available)) throw new NotSupportedException("Tactical hooks unavailable.");
            ModApi.BoardingTactics = tactics;
        }
        catch (Exception error)
        {
            BoardingTacticalPatches.Adapter = null; ModApi.BoardingTactics = null;
            _hub!.SetCapability("boarding-tactics", false, error.GetType().Name); Logger.LogError(error);
        }
        try
        {
            _boardingCombat = new BoardingCombatService(_hub!, (owner, error) => Logger.LogError($"Boarding combat rule '{owner}': {error}"));
            BoardingCombatPatches.Adapter = new BoardingCombatAdapter(_hub!, _boardingCombat, bindings);
            var hooks = BoardingCombatBindings.Scopes.Concat(BoardingCombatBindings.Hooks).ToArray();
            var scopeKeys = BoardingCombatBindings.Scopes.Select(b => b.Key).ToHashSet();
            InstallGroup("boarding-combat", bindings, hooks, hooks.ToDictionary(b => b.Key, b => scopeKeys.Contains(b.Key) ? typeof(BoardingCombatPatches.Scope) : b.Key switch
            {
                "combatPlayerReinforcements" => typeof(BoardingCombatPatches.PlayerReinforcements),
                "combatPower" => typeof(BoardingCombatPatches.Power), "combatHealth" => typeof(BoardingCombatPatches.Health),
                "combatCasualties" => typeof(BoardingCombatPatches.Casualties),
                "combatAttackerState" => typeof(BoardingCombatPatches.AttackerState),
                "combatMoraleRecovery" or "combatMoraleGlobal" or "combatMoraleAttackers" or "combatMoraleCombat" => typeof(BoardingCombatPatches.Morale),
                _ => b.ReturnType == "System.Boolean" ? typeof(BoardingCombatPatches.BoolEffect) : typeof(BoardingCombatPatches.VoidEffect)
            }));
            if (!_hub!.Capabilities.Any(c => c.Name == "boarding-combat" && c.Available)) throw new NotSupportedException("Combat hooks unavailable.");
            ModApi.BoardingCombat = _boardingCombat;
            if (BoardingCommandPatches.Adapter != null) BoardingCommandPatches.Adapter.ReinforcementAllowed = BoardingCombatPatches.Adapter.AllowPlayerReinforcement;
        }
        catch (Exception error)
        {
            BoardingCombatPatches.Adapter = null; _boardingCombat?.Dispose(); _boardingCombat = null; ModApi.BoardingCombat = null;
            _hub!.SetCapability("boarding-combat", false, error.GetType().Name); Logger.LogError(error);
        }
    }

    private void InstallBoardingCommands(GameBindings bindings)
    {
        _hub!.SetCapability("boarding-commands", false, "Boarding observation required; experimental.");
        if (_boarding == null || ModApi.Boarding == null) return;
        try
        {
            var adapter = new BoardingCommandAdapter(new BoardingCommandNativeBindings(bindings), _boarding, ModApi.Boarding,
                value => value is UnityEngine.Object native && native != null);
            _boardingCommands = new BoardingCommandService(_hub, ModApi.Boarding, adapter, () => (ModApi.BoardingRules?.IsEvaluating ?? false) || (_boardingCombat?.IsEvaluating ?? false) || (_dungeonRewards?.IsEvaluating ?? false) || (_dungeonSettlement?.IsDispatchingCallbacks ?? false));
            BoardingCommandPatches.Adapter = adapter; BoardingCommandPatches.Service = _boardingCommands;
            InstallGroup("boarding-commands", bindings, BoardingCommandBindings.Hooks, BoardingCommandBindings.Hooks.ToDictionary(b => b.Key, b => b.Key switch
            {
                "commandRemoveAssigned" => typeof(BoardingCommandPatches.RemoveAssigned),
                "commandBeginWalk" => typeof(BoardingCommandPatches.BeginWalk),
                "commandHudCancel" => typeof(BoardingCommandPatches.HudCancel),
                "commandSerialization" => typeof(BoardingCommandPatches.Serialization),
                "commandAutonomyHook" => typeof(BoardingCommandPatches.Autonomous),
                _ => typeof(BoardingCommandPatches.Manual)
            }));
            if (!_hub.Capabilities.Any(c => c.Name == "boarding-commands" && c.Available)) throw new NotSupportedException("Boarding command hooks unavailable.");
            ModApi.BoardingCommands = _boardingCommands;
        }
        catch (Exception error)
        {
            BoardingCommandPatches.Adapter = null; BoardingCommandPatches.Service = null;
            _boardingCommands?.Dispose(); _boardingCommands = null; ModApi.BoardingCommands = null;
            _hub.SetCapability("boarding-commands", false, error.GetType().Name); Logger.LogError(error);
        }
    }

    private void InstallBoardingRules(GameBindings bindings)
    {
        if (!_hub!.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available)) return;
        BoardingRuleService? rules = null;
        try
        {
            rules = new BoardingRuleService(_hub, (owner, error) => Logger.LogError($"Boarding rule '{owner}': {error}"));
            _boardingRules = new BoardingRuleAdapter(_hub, rules, bindings, value => value is UnityEngine.Object native && native != null, error => Logger.LogError(error), () => UnityEngine.Random.value);
            BoardingRulePatches.Adapter = _boardingRules;
            var patches = BoardingRuleBindings.Hooks.ToDictionary(binding => binding.Key, binding => binding.Key switch
            {
                "disable" => typeof(BoardingRulePatches.Disable), "scaling" => typeof(BoardingRulePatches.Scaling),
                "damage" => typeof(BoardingRulePatches.Damage), "scuttle" => typeof(BoardingRulePatches.Scuttle),
                "explosion" => typeof(BoardingRulePatches.Explosion),
                "createShip" => typeof(BoardingRulePatches.CreateShip), "createWalk" => typeof(BoardingRulePatches.CreateWalk),
                "estimate" => typeof(BoardingRulePatches.Estimate), "estimatePower" => typeof(BoardingRulePatches.EstimatePower),
                _ => typeof(BoardingRulePatches.Cause)
            });
            InstallGroup("boarding-rules", bindings, BoardingRuleBindings.Hooks, patches);
            if (!_hub.Capabilities.Any(c => c.Name == "boarding-rules" && c.Available)) throw new NotSupportedException("Boarding rules unavailable.");
            ModApi.BoardingRules = rules;
        }
        catch (Exception error)
        {
            BoardingRulePatches.Adapter = null; _boardingRules?.Dispose(); _boardingRules = null; rules?.Dispose();
            _hub.SetCapability("boarding-rules", false, "Boarding rules unavailable: " + error.GetType().Name); Logger.LogError(error);
        }
    }

    private void InstallBoarding(GameBindings bindings)
    {
        if (!_hub!.Capabilities.Any(c => c.Name == "session-lifecycle" && c.Available)) return;
        BoardingService? service = null;
        try
        {
            service = new BoardingService(_hub, (owner, error) => Logger.LogError($"Boarding subscriber '{owner}': {error}"));
            _boarding = new BoardingObserver(_hub, service, bindings.Assembly, value => value is UnityEngine.Object native && native != null, error => Logger.LogError(error));
            BoardingPatches.Observer = _boarding;
            var patches = BindingCatalog.Boarding.ToDictionary(binding => binding.Key, binding => binding.Key switch
            {
                "boardingShipReady" or "boardingLocationReady" => typeof(BoardingPatches.Target),
                "boardingStartShip" or "boardingStartLocation" or "boardingResumeShip" or "boardingResumeLocation" or "boardingRestoreApproach" => typeof(BoardingPatches.Start),
                "boardingCapture" => typeof(BoardingPatches.Capture),
                "boardingLoot" or "boardingPartialLoot" or "boardingDataLoot" => typeof(BoardingPatches.Rewards),
                "boardingInventoryDelivery" => typeof(BoardingPatches.Inventory),
                "boardingCreditDelivery" => typeof(BoardingPatches.Credits),
                "boardingWorldDelivery" => typeof(BoardingPatches.WorldLoot),
                _ => typeof(BoardingPatches.Operation)
            });
            InstallGroup("boarding-observation", bindings, BindingCatalog.Boarding, patches);
            if (!_hub.Capabilities.Any(c => c.Name == "boarding-observation" && c.Available)) throw new NotSupportedException("Boarding hooks unavailable.");
            ModApi.Boarding = service;
        }
        catch (Exception error)
        {
            BoardingPatches.Observer = null; _boarding?.Dispose(); _boarding = null; service?.Dispose();
            _hub.SetCapability("boarding-observation", false, "Boarding unavailable: " + error.GetType().Name);
            Logger.LogError(error);
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
        _hudRuntime?.Tick();
        _forgeUiRuntime?.Tick();
        var craftingFault = _craftingJobObserver?.PumpFault();
        if (craftingFault != null) { Logger.LogError(craftingFault); TeardownCraftingCommands(); }
        var commandFault = _craftingCommands?.PumpFault();
        if (commandFault != null) { Logger.LogError(commandFault); TeardownCraftingCommands(); }
        _boarding?.Poll();
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
        TeardownHud();
        TeardownForgeUi();
        TeardownCraftingCommands();
        StopBars();
        DungeonRewardPatches.Crew = null; _dungeonSettlement?.Dispose(); _dungeonSettlement = null; ModApi.DungeonSettlement = null;
        DungeonRewardPatches.Adapter = null; _dungeonRewards?.Dispose(); _dungeonRewards = null; ModApi.DungeonRewards = null;
        StopDungeons();
        BoardingTacticalPatches.Adapter = null; ModApi.BoardingTactics = null;
        BoardingCombatPatches.Adapter = null; _boardingCombat?.Dispose(); _boardingCombat = null; ModApi.BoardingCombat = null;
        BoardingCommandPatches.Adapter = null; BoardingCommandPatches.Service = null; _boardingCommands?.Dispose(); _boardingCommands = null; ModApi.BoardingCommands = null;
        BoardingRulePatches.Adapter = null; _boardingRules?.Dispose(); _boardingRules = null; ModApi.BoardingRules = null;
        BoardingPatches.Observer = null; _boarding?.Dispose(); _boarding = null; ModApi.Boarding = null;
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
        TeardownCraftingJobs();
        _recipeQuotes?.Dispose(); _recipeQuotes = null; ModApi.RecipeQuotes = null;
        _recipes?.Dispose(); _recipes = null; ModApi.Recipes = null;
        _hub?.Dispose();
        _adapter = null;
    }
}
