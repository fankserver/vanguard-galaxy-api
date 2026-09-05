using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>Opt-in test driver; never distributed in the API package.</summary>
[BepInPlugin(Id, "VGModAPI Controlled Qualification", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.1.0")]
[BepInDependency("vgmodapi.qualification.guard", "0.1.0")]
public sealed partial class Plugin : BaseUnityPlugin
{
    private bool _dispatchStateValid = true;
    private const string Id = "vgmodapi.qualification";
    private static string? _saveRoot;
    private string? _root;
    private ILifecycleApi? _api;
    private readonly List<LifecycleEvent> _events = new();
    private readonly List<string> _passed = new();
    private IDisposable? _subscription;
    private Type _save = null!;
    private Type _player = null!;
    private Type _manager = null!;
    private Type _scenes = null!;
    private bool _armed;
    private static Plugin? _diagnostics;
    private static bool _retryMetadataFailure;
    private static bool _configuringNewGame;
    private static int _configurationCalls;
    private static string? _newGameError;
    private bool _prematureNewGameReadiness;
    private static Type _alertType = null!;
    private static string? _expectedAlertKey;
    private static object? _observedAlert;
    private static bool _alertCollision;

    private void Awake()
    {
        var args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--vgmodapi-qualification-root");
        if (flag < 0) return;
        try
        {
            Require(flag + 1 < args.Length, "Qualification root argument missing.");
            _root = Path.GetFullPath(args[flag + 1]);
            Require(File.ReadAllText(Path.Combine(_root, "qualification.marker")).Trim() == "vgmodapi-disposable-sandbox-v1", "Sandbox marker missing.");
            // Disable Steam before any subsequent arming check can fail.
            var harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("SteamManager"), "Awake"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSteam)));
            harmony.Patch(AccessTools.PropertyGetter(AccessTools.TypeByName("SteamManager"), "Initialized"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSteam)));
            Require(SamePath(Path.GetDirectoryName(Application.dataPath)!, Path.Combine(_root, "game")), "Refusing to run outside the sandbox executable directory.");
            _saveRoot = Path.Combine(_root, "Saves");
            Directory.CreateDirectory(_saveRoot);
            _api = ModApi.Current ?? throw new InvalidOperationException("API unavailable.");
            Require(_api.Capabilities.Where(c => c.Name == "session-lifecycle" || c.Name == "save-outcomes").Count(c => c.Available) == 2, "API hooks did not bind; no qualification possible.");
            _save = AccessTools.TypeByName("Source.Util.SaveGame");
            _player = AccessTools.TypeByName("Source.Player.GamePlayer");
            _manager = AccessTools.TypeByName("Behaviour.GameManager");
            _scenes = AccessTools.TypeByName("Behaviour.Bootstrap.SceneLoader");
            _alertType = AccessTools.TypeByName("Behaviour.UI.AlertPopup");
            harmony.Patch(AccessTools.Method(_alertType, "ShowMessage"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(AlertEntering)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AlertShown)));
            var originalSavePath = (string)AccessTools.Field(_save, "SavesPath").GetValue(null)!;
            var protectedPath = File.ReadAllText(Path.Combine(_root, "original-save-directory.txt")).Trim();
            Require(SamePath(File.ReadAllText(Path.Combine(_root, "isolation-armed.txt")).Trim(), _saveRoot)
                && SamePath(originalSavePath, _saveRoot), "Independent guard did not establish isolation.");
            Require(!SamePath(_saveRoot, protectedPath), "Real save directory is not a test target.");
            // Defense in depth: prevent any Store/Recall outside the redirected directory.
            harmony.Patch(AccessTools.Method(_save, "Store"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckSaveDestination)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Source.Util.SaveGameFile"), "Recall"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckLoadSource)));
            AccessTools.Field(_save, "SavesPath").SetValue(null, _saveRoot);
            AccessTools.Field(_save, "SavesDir").SetValue(null, new DirectoryInfo(_saveRoot));
            AccessTools.Field(_save, "_saves").SetValue(null, null);
            Require(System.Text.RegularExpressions.Regex.IsMatch(Application.version, @"^\d+(\.\d+)+$"), "Unexpected game version syntax for control fixture.");
            File.WriteAllText(Path.Combine(_saveRoot, "fixture-current-empty.save"), "{\"Version\":\"" + Application.version + "\",\"Player\":{}}");
            harmony.Patch(AccessTools.Method(_save, "WriteVersionMetadata"), prefix: new HarmonyMethod(typeof(Plugin), nameof(InjectTransientMetadataFailure)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Behaviour.UI.Main.NewGame"), "SaveInputs"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(NewGameConfigurationEntering)),
                finalizer: new HarmonyMethod(typeof(Plugin), nameof(NewGameConfigurationExited)));
            File.WriteAllText(Path.Combine(_root, "events.tsv"), "sequence\tkind\tsession\tphase\toperation\tdestination\tdetail\n");
            _subscription = _api.Subscribe(Id, e =>
            {
                _dispatchStateValid &= _api is ILifecycleDispatchState state && state.IsDispatchingCallbacks;
                _events.Add(e);
                if (_configuringNewGame && e.Kind == LifecycleEventKind.PlayerReady) _prematureNewGameReadiness = true;
                File.AppendAllText(Path.Combine(_root, "events.tsv"), string.Join("\t", _events.Count, e.Kind, e.Session?.Id, e.Session?.Phase, e.OperationId, e.Destination == null ? "" : Path.GetFileName(e.Destination), (e.Detail ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")) + "\n");
            });
            if (Array.IndexOf(args, "--vgmodapi-qualification-diagnostics") >= 0)
            {
                _diagnostics = this;
                harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Behaviour.UI.Side_Menu.SidePanel"), "RefreshIfOpen"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(SidePanelEntering)));
                var gameplayStart = AccessTools.Method(AccessTools.TypeByName("GameplayManager"), "Start");
                Require(gameplayStart?.ReturnType == typeof(void), "Diagnostics require synchronous void GameplayManager.Start.");
                harmony.Patch(gameplayStart,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(GameplayEntering)),
                    finalizer: new HarmonyMethod(typeof(Plugin), nameof(GameplayExited)));
            }
            _armed = true;
            Logger.LogInfo("Qualification sandbox armed; real save directory is not used. Steam integration disabled for this process.");
        }
        catch (Exception ex) { Finish(false, ex.ToString()); }
    }

    private IEnumerator Start()
    {
        if (!_armed) yield break;
        var routine = Run();
        while (true)
        {
            object? current;
            try
            {
                if (!routine.MoveNext()) break;
                current = routine.Current;
            }
            catch (Exception ex) { Finish(false, ex.ToString()); yield break; }
            yield return current;
        }
        Finish(true, "Automated smoke only; full owner acceptance and untested scenarios remain pending.");
    }

    private IEnumerator Run()
    {
        foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded, "main menu")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        for (int n = 0; n < 2; n++)
        {
            var previous = _api!.CurrentSession?.Id;
            Load(n == 0 ? "fixture-a" : "fixture-b");
            foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != previous, "fixture initialization")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var session = _api.CurrentSession!;
            var sequence = _events.Where(e => e.Session?.Id == session.Id).Select(e => e.Kind).ToArray();
            Require(sequence.SequenceEqual(new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.PlayerReady, LifecycleEventKind.GameplayInitialized }), "Unexpected load event order.");
            if (previous.HasValue) Require(_events.Any(e => e.Kind == LifecycleEventKind.SessionInvalidated && e.Session?.Id == previous), "Prior session was not invalidated.");
            CheckJournalLoad(n == 0 ? "fixture-a" : "fixture-b");
            Passed("fixture-load-" + n);
        }

        Save("qa-manual", LifecycleEventKind.SaveSucceeded);
        Passed("manual-save");
        // Existing fixture must remain deserializable after the API-observed write.
        var beforeRoundtrip = _api!.CurrentSession?.Id;
        Load("qa-manual");
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != beforeRoundtrip, "roundtrip")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        CheckJournalLoad("qa-manual");
        Passed("saved-copy-roundtrip");

        object player = AccessTools.Field(_player, "current").GetValue(null)!;
        var ephemeral = AccessTools.Field(_player, "isEphemeral");
        bool original = (bool)ephemeral.GetValue(player)!;
        try { ephemeral.SetValue(player, true); Save("qa-skipped", LifecycleEventKind.SaveSkipped); }
        finally { ephemeral.SetValue(player, original); }
        Require(!File.Exists(Path.Combine(_saveRoot!, "qa-skipped.save")), "Skipped save wrote a file.");
        Passed("ephemeral-skip");

        // A directory at the metadata filename causes bounded vanilla retries, only in the sandbox.
        var blocked = Path.Combine(_saveRoot!, "qa-autosave-failure.meta");
        Directory.CreateDirectory(blocked);
        try { Save("qa-autosave-failure", LifecycleEventKind.SaveFailed); }
        finally { Directory.Delete(blocked); }
        Passed("exhausted-retries");

        _retryMetadataFailure = true;
        try
        {
            Save("qa-retry-success", LifecycleEventKind.SaveSucceeded);
            Require(!_retryMetadataFailure, "Transient failure injection was not exercised.");
            Require(File.Exists(Path.Combine(_saveRoot!, "qa-retry-success.meta")), "Recovered retry did not write metadata.");
        }
        finally { _retryMetadataFailure = false; }
        Passed("retry-recovered");

        foreach (int slot in new[] { 0, 1, 2 })
            Require(!File.Exists(Path.Combine(_saveRoot!, "autosave-" + slot + ".save")), "Autosave rotation requires initially empty sandbox slots.");
        var autosaveOperations = new HashSet<Guid?>();
        foreach (int slot in new[] { 0, 1, 2, 0 })
        {
            int start = _events.Count;
            AccessTools.Method(_save, "StoreAutosaveState").Invoke(null, new object?[] { null });
            ValidateSave(start, "autosave-" + slot, LifecycleEventKind.SaveSucceeded);
            Require(autosaveOperations.Add(_events[start].OperationId), "Autosave reused an operation identity.");
            yield return null;
        }
        Passed("autosave-rotation");

        int healthy = 0, disposed = 0;
        using (var broken = _api!.Subscribe(Id + ".expected-fault", _ => throw new InvalidOperationException("Expected qualification subscriber fault")))
        using (var good = _api.Subscribe(Id + ".healthy", _ => healthy++))
        {
            var removed = _api.Subscribe(Id + ".disposed", _ => disposed++);
            removed.Dispose();
            Save("qa-subscribers", LifecycleEventKind.SaveSucceeded);
        }
        Require(healthy == 2 && disposed == 0, "Subscriber isolation/disposal failed.");
        Passed("subscriber-isolation-and-disposal");

        Invoke(Instance(_scenes), "StartMenu");
        foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded && SceneManager.sceneCount <= 4
            && AccessTools.Field(_player, "current").GetValue(null) == null, "menu before rejection controls")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        // Equal empty Player payloads with newer/current headers distinguish rejection from deserialization failure.
        foreach (string name in new[] { "fixture-future", "fixture-current-empty", "fixture-corrupt" })
        {
            Require(!AlertOpen(), "Unexpected modal before rejection fixture; refusing blind acknowledgement.");
            _expectedAlertKey = name == "fixture-future" ? "@UILoadGameTooNew" : "@ELLoadGameError";
            _observedAlert = null; _alertCollision = false;
            var previous = _api!.CurrentSession?.Id;
            Load(name);
            foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.Failed && _api.CurrentSession.Id != previous, name + " rejection", allowFailure: true)) yield return frame;
            var id = _api!.CurrentSession!.Id;
            Require(!_events.Any(e => e.Session?.Id == id && (e.Kind == LifecycleEventKind.PlayerReady || e.Kind == LifecycleEventKind.GameplayInitialized)), "Rejected load became ready.");
            var failures = _events.Where(e => e.Session?.Id == id && e.Kind == LifecycleEventKind.SessionStartFailed).ToArray();
            Require(failures.Length == 1, "Failure was not reported exactly once.");
            var reason = name switch
            {
                "fixture-future" => "without player readiness",
                "fixture-current-empty" => "Load failed or canceled: NullReferenceException",
                _ => "Vanilla reported a save-load failure"
            };
            Require(failures[0].Detail?.Contains(reason) == true, "Wrong rejection path for " + name + ": " + failures[0].Detail);
            foreach (var frame in AcknowledgeRejection(name)) yield return frame;
            // Inspected StartMenu retains only Bootstrapper, Camera, Main Menu and Backdrop.
            foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded && SceneManager.sceneCount <= 4 && !SceneManager.GetSceneByName("Gameplay").isLoaded && AccessTools.Field(_player, "current").GetValue(null) == null, "menu after rejected load", allowFailure: true)) yield return frame;
            // isLoaded alone can still refer to the pre-existing menu after a rejection.
            foreach (var frame in Settle()) yield return frame;
            Passed(name + "-rejected");
        }
        var beforeRecovery = _api!.CurrentSession?.Id;
        Load("fixture-a");
        foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != beforeRecovery, "post-failure reload")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        CheckJournalLoad("fixture-a");
        Passed("reload-after-failure");
        foreach (var frame in NewGameAndSpaceLoad()) yield return frame;
        foreach (var frame in CheckJournalTeardown()) yield return frame;
        Require(_dispatchStateValid && _events.Count > 0 && _api is ILifecycleDispatchState state && !state.IsDispatchingCallbacks,
            "Callback dispatch state did not match native delivery boundaries.");
        Passed("callback-dispatch-state");
    }

    private IEnumerable<object?> NewGameAndSpaceLoad()
    {
        Invoke(Instance(_scenes), "StartMenu");
        foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded && SceneManager.sceneCount <= 4
            && AccessTools.Field(_player, "current").GetValue(null) == null, "menu before new game")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Invoke(SceneComponent("Behaviour.UI.MainMenuUI"), "StartGame");
        foreach (var frame in Wait(() => SceneManager.GetSceneByName("Start - New Game").isLoaded, "new-game wizard")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Require(!AlertOpen(), "Unexpected modal before new-game wizard.");
        var wizard = SceneComponent("Behaviour.UI.Main.NewGame");
        var previous = _api!.CurrentSession?.Id;
        for (int step = 1; step <= 5; step++)
        {
            Require((int)AccessTools.Field(wizard.GetType(), "currentStep").GetValue(wizard)! == step, "Wizard did not reach step " + step);
            Require(!AlertOpen(), "Unexpected modal during new-game wizard.");
            Invoke(wizard, "SubmitInput");
            if (step < 5) foreach (var frame in Settle()) yield return frame;
        }
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != previous, "new-game initialization")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Require(_api.CurrentSession!.Origin == SessionOrigin.NewGame && !_prematureNewGameReadiness
            && _configurationCalls == 1 && !_configuringNewGame && _newGameError == null, "New-game configuration/readiness boundary failed.");
        Require(_events.Where(e => e.Session?.Id == _api.CurrentSession.Id).Select(e => e.Kind).SequenceEqual(
            new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.PlayerReady, LifecycleEventKind.GameplayInitialized }), "Unexpected new-game event order.");
        if (JournalSelected)
            Require(!LiveJournalIds().Any(_previousJournalIds.Contains), "New game inherited prior journal history.");
        Passed("new-game-wizard-and-configuration-boundary");

        Require(InSpace(), "Native new-game setup did not produce a space fixture.");
        Save("qa-space", LifecycleEventKind.SaveSucceeded);
        previous = _api.CurrentSession.Id;
        Load("qa-space");
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != previous, "space-copy load")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Require(InSpace(), "Space fixture did not reload in space.");
        Passed("mining-space-save-load");

        Invoke(Instance(_scenes), "StartMenu");
        foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded && SceneManager.sceneCount <= 4
            && AccessTools.Field(_player, "current").GetValue(null) == null, "menu before replacement probe")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        AccessTools.Method(_player, "CreateNewGamePlayer").Invoke(null, new object?[] { null, false });
        var pending = _api.CurrentSession!;
        Require(pending.Phase == SessionPhase.Starting, "Creation prematurely became ready.");
        AccessTools.Method(_player, "CreateTestArenaPlayer").Invoke(null, null);
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.Invalidated, "untracked replacement invalidation")) yield return frame;
        Require(_api.CurrentSession!.Id == pending.Id && !_events.Any(e => e.Session?.Id == pending.Id
            && (e.Kind == LifecycleEventKind.PlayerReady || e.Kind == LifecycleEventKind.GameplayInitialized)), "Replacement adopted the pending attempt.");
        Require(_events.Any(e => e.Session?.Id == pending.Id && e.Kind == LifecycleEventKind.SessionInvalidated
            && e.Detail?.StartsWith("Player identity changed outside the tracked initialization boundary.", StringComparison.Ordinal) == true), "Poll invalidation was not observed.");
        Passed("pending-new-game-replacement-invalidated");
        Invoke(Instance(_scenes), "StartMenu");
        foreach (var frame in Wait(() => AccessTools.Field(_player, "current").GetValue(null) == null
            && SceneManager.GetSceneByName("Main Menu").isLoaded && SceneManager.sceneCount <= 4, "replacement probe cleanup")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Load("fixture-a");
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != pending.Id, "replacement probe recovery")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Passed("reload-after-untracked-replacement");
    }

    private IEnumerable<object?> AcknowledgeRejection(string fixture)
    {
        // Allow the popup's Start and any resumed parent coroutine to run without overriding pause.
        float until = Time.realtimeSinceStartup + 2;
        do { yield return null; } while (Time.realtimeSinceStartup < until);
        Require(!_alertCollision, "Expected rejection message collided with an existing modal.");
        if (_observedAlert != null)
        {
            Require(ReferenceEquals(_observedAlert, AccessTools.Field(_alertType, "activeInstance").GetValue(null)), "Rejection modal changed before acknowledgement.");
            var button = AccessTools.Field(_alertType, "submitButton").GetValue(_observedAlert)!;
            var click = button.GetType().GetProperty("onClick")!.GetValue(button)!;
            click.GetType().GetMethod("Invoke", Type.EmptyTypes)!.Invoke(click, null);
            foreach (var frame in Wait(() => !AlertOpen(), "rejection modal destruction", allowFailure: true)) yield return frame;
            Passed(fixture + "-alert-acknowledged");
        }
        else
        {
            Require(fixture == "fixture-current-empty" && !AlertOpen(), "Expected rejection modal was not observed.");
            Invoke(Instance(_scenes), "StartMenu");
        }
        _expectedAlertKey = null;
    }

    private static bool AlertOpen() => (bool)AccessTools.Property(_alertType, "IsOpen").GetValue(null)!;
    private static void AlertEntering(string __0, out bool __state)
    {
        bool expected = _expectedAlertKey != null && __0 == _expectedAlertKey;
        __state = expected && !AlertOpen();
        if (expected && !__state) _alertCollision = true;
    }
    private static void AlertShown(bool __state)
    {
        if (__state) _observedAlert = AccessTools.Field(_alertType, "activeInstance").GetValue(null);
    }

    private bool InSpace()
    {
        var player = AccessTools.Field(_player, "current").GetValue(null);
        var poi = player == null ? null : AccessTools.Field(_player, "currentPointOfInterest").GetValue(player);
        return poi != null && SceneManager.GetSceneByName("Mining").isLoaded
            && !SceneManager.GetSceneByName("SpacestationInterior").isLoaded;
    }

    private static Component SceneComponent(string name) => Resources.FindObjectsOfTypeAll(AccessTools.TypeByName(name))
        .OfType<Component>().Single(c => c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded);

    private void Load(string name)
    {
        Time.timeScale = 1;
        var file = AccessTools.Method(_save, "GetSaveGame").Invoke(null, new object[] { name });
        Require(file != null, "Fixture missing: " + name);
        Invoke(Instance(_manager), "LoadGame", file!);
    }

    private void Save(string name, LifecycleEventKind expected)
    {
        Time.timeScale = 1;
        int start = _events.Count;
        var data = AccessTools.Method(_save, "SaveCurrentState").Invoke(null, null);
        var format = Enum.Parse(AccessTools.TypeByName("Source.Util.SaveGameFormat"), "Compressed");
        AccessTools.Method(_save, "Store").Invoke(null, new[] { data, name, format, (object)0 });
        ValidateSave(start, name, expected);
    }

    private void ValidateSave(int start, string name, LifecycleEventKind expected)
    {
        var received = _events.Skip(start).ToArray();
        Require(received.Length == 2 && received[0].Kind == LifecycleEventKind.SaveStarted && received[1].Kind == expected, "Unexpected outcome/count for " + name);
        Require(received[0].OperationId == received[1].OperationId, "Save operation identity changed.");
        Require(received.All(e => SamePath(e.Destination!, Path.Combine(_saveRoot!, name + ".save"))), "Wrong save destination.");
        CheckJournalSave(name, expected);
    }

    // A harness grace period, not an API guarantee of UI/world readiness.
    private static IEnumerable<object?> Settle()
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + 2;
        do { yield return null; } while (Time.realtimeSinceStartup < until);
    }

    private IEnumerable<object?> Wait(Func<bool> ready, string description, bool allowFailure = false)
    {
        float deadline = Time.realtimeSinceStartup + 90;
        while (!ready())
        {
            Require(_newGameError == null, "Native new-game configuration failed: " + _newGameError);
            if (!allowFailure) Require(_api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for " + description);
            Require(Time.realtimeSinceStartup < deadline, "Timed out waiting for " + description);
            yield return null;
        }
    }

    private void Passed(string scenario) { _passed.Add(scenario); Logger.LogInfo("QA PASS: " + scenario); }
    private void Finish(bool passed, string detail)
    {
        Logger.LogInfo((passed ? "QA COMPLETE: " : "QA FAILED: ") + detail);
        if (_root != null && File.Exists(Path.Combine(_root, "qualification.marker")))
            File.WriteAllText(Path.Combine(_root, "result.txt"), (passed ? "PASS\n" : "FAIL\n") + string.Join("\n", _passed) + "\n" + detail);
        // Do not restore the original save path or unpatch safety guards before vanilla quit handling.
        Application.Quit(passed ? 0 : 1);
    }

    private static object Instance(Type type) => AccessTools.Property(type, "Instance").GetValue(null)!;
    private static void Invoke(object instance, string name, params object[] args) => AccessTools.Method(instance.GetType(), name).Invoke(instance, args);
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void GameplayEntering()
    {
        try
        {
            var panel = AccessTools.Field(AccessTools.TypeByName("Behaviour.UI.Side_Menu.SidePanel"), "instance").GetValue(null) as UnityEngine.Object;
            var owner = _diagnostics!;
            var player = AccessTools.Field(owner._player, "current").GetValue(null);
            var stories = player == null ? null : AccessTools.Field(owner._player, "storytellers").GetValue(player) as ICollection;
            owner.Logger.LogInfo($"QA DIAGNOSTIC Gameplay.Start entering: sidePanelAlive={panel != null}; playerPresent={player != null}; storytellersCount={stories?.Count.ToString() ?? "null"}; frame={Time.frameCount}");
        }
        catch (Exception ex) { _diagnostics?.Logger.LogWarning("QA diagnostic unavailable: " + ex.GetType().Name); }
    }
    private static void SidePanelEntering()
    {
        try { _diagnostics?.Logger.LogInfo("QA DIAGNOSTIC SidePanel.RefreshIfOpen entered"); } catch { }
    }
    private static Exception? GameplayExited(Exception? __exception)
    {
        try { _diagnostics?.Logger.LogInfo("QA DIAGNOSTIC Gameplay.Start exit: " + (__exception?.ToString() ?? "success")); } catch { }
        return __exception; // Observe only; never manufacture a successful initialization.
    }
    private static void InjectTransientMetadataFailure(string __0)
    {
        if (!_retryMetadataFailure || __0 != "qa-retry-success") return;
        _retryMetadataFailure = false;
        throw new IOException("Expected one-shot sandbox metadata failure.");
    }
    private static void NewGameConfigurationEntering() { _configurationCalls++; _configuringNewGame = true; _newGameError = null; }
    private static Exception? NewGameConfigurationExited(Exception? __exception)
    {
        _configuringNewGame = false;
        _newGameError = __exception?.ToString();
        return __exception;
    }
    private static bool SkipSteam() => false;
    private static bool CheckSaveDestination(object[] __args)
    {
        var name = (string)__args[1];
        Require(_saveRoot != null && name == Path.GetFileName(name) && !name.Contains(".."), "Unsafe sandbox save name.");
        var save = AccessTools.TypeByName("Source.Util.SaveGame");
        Require(SamePath((string)AccessTools.Field(save, "SavesPath").GetValue(null)!, _saveRoot!), "Save root changed.");
        Require(SamePath(((DirectoryInfo)AccessTools.Field(save, "SavesDir").GetValue(null)!).FullName, _saveRoot!), "Save directory changed.");
        return true;
    }
    private static void CheckLoadSource(object __instance)
    {
        var file = (FileInfo)AccessTools.Field(__instance.GetType(), "File").GetValue(__instance)!;
        Require(_saveRoot != null && SamePath(file.DirectoryName!, _saveRoot), "Refusing to load a non-sandbox save.");
    }
    private void OnDestroy() => _subscription?.Dispose();
}
