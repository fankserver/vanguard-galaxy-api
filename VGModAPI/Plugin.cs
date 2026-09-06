using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

[BepInPlugin(ModApi.PluginId, "Vanguard Galaxy Mod API", "0.1.5")]
[BepInProcess("VanguardGalaxy.exe")]
[BepInDependency("vgmodapi.qualification.guard", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    private LifecycleHub? _hub;
    private Harmony? _harmony;
    private GameAdapter? _adapter;
    private PersistenceService? _persistence;
    private MissionAdapter? _missions;
    private void Awake()
    {
        _hub = new LifecycleHub((owner, ex) => Logger.LogError($"Subscriber '{owner}' failed: {ex}"));
        _hub.SetCapability("session-lifecycle", false, "Not bound.");
        _hub.SetCapability("save-outcomes", false, "Not bound.");
        _hub.SetCapability("world-ready", false, "No universal POI/UI-ready guarantee; GameplayInitialized is narrower.");
        _hub.SetCapability("coordinated-persistence", false, "Disabled by configuration; experimental.");
        _hub.SetCapability("mission-transitions", false, "Disabled by configuration; experimental.");
        ModApi.Missions = null;
        ModApi.Current = _hub;
        ModApi.Persistence = null;
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Assembly-CSharp")
                ?? Assembly.Load("Assembly-CSharp");
            var hash = ReadAssemblyHash(assembly);
            Logger.LogInfo($"Game {UnityEngine.Application.version}, Unity {UnityEngine.Application.unityVersion}; assembly SHA-256: {hash}");
            if (hash != BindingCatalog.InspectedSha256)
                throw new NotSupportedException("Uninspected game assembly: lifecycle hooks disabled. Reverify adapter before adding support.");
            var bindings = new GameBindings(assembly);
            _adapter = new GameAdapter(_hub, bindings, ex => Logger.LogError($"Observer fault: {ex}"));
            _harmony = new Harmony(ModApi.PluginId);
            LifecyclePatches.Adapter = _adapter;
            SavePatches.Adapter = _adapter;
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
        InitializePersistence();
        InitializeMissions();
        Logger.LogInfo("VGModAPI " + Info.Metadata.Version + ": experimental, NOT runtime-qualified. Query capabilities; startup does not prove compatibility.");
    }

    private void InitializePersistence()
    {
        if (!Config.Bind("Persistence", "Enabled", false, "Experimental opt-in; use disposable saves until qualified.").Value) return;
        if (_hub!.Capabilities.Count(c => (c.Name == "session-lifecycle" || c.Name == "save-outcomes") && c.Available) != 2)
        {
            _hub.SetCapability("coordinated-persistence", false, "Lifecycle capabilities unavailable.");
            return;
        }
        try
        {
            var root = Config.Bind("Persistence", "Root", Path.Combine(Paths.ConfigPath, "VGModAPI-state"), "Short, non-linked owned storage root; do not share across installations.").Value;
            if (!Path.IsPathRooted(root)) throw new ArgumentException("Persistence root must be absolute.");
            var saves = (string)AccessTools.Field(AccessTools.TypeByName("Source.Util.SaveGame"), "SavesPath").GetValue(null)!;
            var files = new PersistenceFiles(saves);
            _persistence = new PersistenceService(_hub, new GenerationStore(root), files.Canonical, files.HashFile);
            ModApi.Persistence = _persistence;
            _hub.SetCapability("coordinated-persistence", true, "Experimental owner persistence enabled; native consumer qualification pending.");
        }
        catch (Exception error)
        {
            _hub.SetCapability("coordinated-persistence", false, "Persistence initialization failed: " + error.GetType().Name);
            Logger.LogError("Coordinated persistence unavailable: " + error.GetType().Name + ": " + error.Message);
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
                BindingCatalog.Missions.ToDictionary(binding => binding.Key, _ => typeof(MissionPatches)));
            if (_hub.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available)) ModApi.Missions = _missions.Events;
            else { _missions.Dispose(); _missions = null; MissionPatches.Adapter = null; }
        }
        catch (Exception error)
        {
            _missions?.Dispose(); _missions = null; MissionPatches.Adapter = null;
            _hub.SetCapability("mission-transitions", false, "Mission binding failed: " + error.Message);
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

    private void Update() { _adapter?.Poll(); _missions?.Poll(); }
    private void OnDestroy()
    {
        _adapter?.Guard(() => _adapter.Invalidate("API shutting down."));
        _missions?.Dispose(); _missions = null;
        MissionPatches.Adapter = null; ModApi.Missions = null;
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
