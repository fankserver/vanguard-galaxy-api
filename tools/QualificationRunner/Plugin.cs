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
public sealed class Plugin : BaseUnityPlugin
{
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
            Require(SamePath(Path.GetDirectoryName(Application.dataPath)!, Path.Combine(_root, "game")), "Refusing to run outside the sandbox executable directory.");
            _saveRoot = Path.Combine(_root, "Saves");
            Require(!SamePath(_saveRoot, Path.Combine(Application.persistentDataPath, "Saves")), "Real save directory is not a test target.");
            Directory.CreateDirectory(_saveRoot);
            _api = ModApi.Current ?? throw new InvalidOperationException("API unavailable.");
            Require(_api.Capabilities.Where(c => c.Name == "session-lifecycle" || c.Name == "save-outcomes").Count(c => c.Available) == 2, "API hooks did not bind; no qualification possible.");
            _save = AccessTools.TypeByName("Source.Util.SaveGame");
            _player = AccessTools.TypeByName("Source.Player.GamePlayer");
            _manager = AccessTools.TypeByName("Behaviour.GameManager");
            _scenes = AccessTools.TypeByName("Behaviour.Bootstrap.SceneLoader");
            var harmony = new Harmony(Id);
            // Defense in depth: prevent any Store/Recall outside the redirected directory.
            harmony.Patch(AccessTools.Method(_save, "Store"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckSaveDestination)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Source.Util.SaveGameFile"), "Recall"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckLoadSource)));
            // An isolated test must not relaunch the installed game or grant Steam stats/achievements.
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("SteamManager"), "Awake"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSteam)));
            harmony.Patch(AccessTools.PropertyGetter(AccessTools.TypeByName("SteamManager"), "Initialized"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSteam)));
            AccessTools.Field(_save, "SavesPath").SetValue(null, _saveRoot);
            AccessTools.Field(_save, "SavesDir").SetValue(null, new DirectoryInfo(_saveRoot));
            AccessTools.Field(_save, "_saves").SetValue(null, null);
            File.WriteAllText(Path.Combine(_root, "events.tsv"), "sequence\tkind\tsession\tphase\toperation\tdestination\n");
            _subscription = _api.Subscribe(Id, e =>
            {
                _events.Add(e);
                File.AppendAllText(Path.Combine(_root, "events.tsv"), string.Join("\t", _events.Count, e.Kind, e.Session?.Id, e.Session?.Phase, e.OperationId, e.Destination == null ? "" : Path.GetFileName(e.Destination)) + "\n");
            });
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
        for (int n = 0; n < 2; n++)
        {
            var previous = _api!.CurrentSession?.Id;
            Load(n == 0 ? "fixture-a" : "fixture-b");
            foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != previous, "fixture initialization")) yield return frame;
            Time.timeScale = 0;
            var session = _api.CurrentSession!;
            var sequence = _events.Where(e => e.Session?.Id == session.Id).Select(e => e.Kind).ToArray();
            Require(sequence.SequenceEqual(new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.PlayerReady, LifecycleEventKind.GameplayInitialized }), "Unexpected load event order.");
            if (previous.HasValue) Require(_events.Any(e => e.Kind == LifecycleEventKind.SessionInvalidated && e.Session?.Id == previous), "Prior session was not invalidated.");
            Passed("fixture-load-" + n);
        }

        Save("qa-manual", LifecycleEventKind.SaveSucceeded);
        Passed("manual-save");
        // Existing fixture must remain deserializable after the API-observed write.
        var beforeRoundtrip = _api!.CurrentSession?.Id;
        Load("qa-manual");
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != beforeRoundtrip, "roundtrip")) yield return frame;
        Time.timeScale = 0;
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

        // Future-version and corrupt files were created from disposable copies by the provisioning tool.
        foreach (string name in new[] { "fixture-future", "fixture-corrupt" })
        {
            Load(name);
            foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.Failed, name + " rejection", allowFailure: true)) yield return frame;
            var id = _api!.CurrentSession!.Id;
            Require(!_events.Any(e => e.Session?.Id == id && e.Kind == LifecycleEventKind.GameplayInitialized), "Rejected load became initialized.");
            Require(_events.Count(e => e.Session?.Id == id && e.Kind == LifecycleEventKind.SessionStartFailed) == 1, "Failure was not reported exactly once.");
            Invoke(Instance(_scenes), "StartMenu");
            foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded, "menu after rejected load", allowFailure: true)) yield return frame;
            Passed(name + "-rejected");
        }
        Load("fixture-a");
        foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.GameplayInitialized, "post-failure reload")) yield return frame;
        Passed("reload-after-failure");
    }

    private void Load(string name)
    {
        Time.timeScale = 1;
        var file = AccessTools.Method(_save, "GetSaveGame").Invoke(null, new object[] { name });
        Require(file != null, "Fixture missing: " + name);
        Invoke(Instance(_manager), "LoadGame", file!);
    }

    private void Save(string name, LifecycleEventKind expected)
    {
        int start = _events.Count;
        var data = AccessTools.Method(_save, "SaveCurrentState").Invoke(null, null);
        var format = Enum.Parse(AccessTools.TypeByName("Source.Util.SaveGameFormat"), "Compressed");
        AccessTools.Method(_save, "Store").Invoke(null, new[] { data, name, format, (object)0 });
        var received = _events.Skip(start).ToArray();
        Require(received.Length == 2 && received[0].Kind == LifecycleEventKind.SaveStarted && received[1].Kind == expected, "Unexpected outcome/count for " + name);
        Require(received[0].OperationId == received[1].OperationId, "Save operation identity changed.");
        Require(received.All(e => SamePath(e.Destination!, Path.Combine(_saveRoot!, name + ".save"))), "Wrong save destination.");
    }

    private IEnumerable<object?> Wait(Func<bool> ready, string description, bool allowFailure = false)
    {
        float deadline = Time.realtimeSinceStartup + 90;
        while (!ready())
        {
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
