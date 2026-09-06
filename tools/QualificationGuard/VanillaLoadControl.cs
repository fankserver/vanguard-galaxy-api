using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VGModAPI.QualificationGuard;

public sealed partial class Plugin
{
    private static string? _orbitFailure;
    private static string? _vanillaEvidenceRoot;

    private static Exception? ObserveOrbitFailure(object __0, Exception? __exception)
    {
        if (__exception == null) return null;
        try
        {
            var world = AccessTools.TypeByName("Source.Simulation.World.SandboxWorld");
            _orbitFailure = $"{__exception.GetType().Name}; systemNull={__0 == null}; randomNull={AccessTools.Field(world, "random").GetValue(null) == null}";
            File.WriteAllText(Path.Combine(_vanillaEvidenceRoot!, "vanilla-orbit-failure.txt"), _orbitFailure);
        }
        catch { /* Diagnostic failure must not replace vanilla's exception. */ }
        return __exception;
    }

    private IEnumerable<object?> ReturnFailedControlToMenu()
    {
        var playerField = AccessTools.Field(AccessTools.TypeByName("Source.Player.GamePlayer"), "current");
        var player = playerField.GetValue(null);
        if (player != null) AccessTools.Method(player.GetType(), "Cleanup").Invoke(player, null);
        var scenes = AccessTools.TypeByName("Behaviour.Bootstrap.SceneLoader");
        AccessTools.Method(scenes, "StartMenu").Invoke(AccessTools.Property(scenes, "Instance").GetValue(null), null);
        var deadline = Time.realtimeSinceStartup + 90;
        while (playerField.GetValue(null) != null || !SceneManager.GetSceneByName("Main Menu").isLoaded)
        {
            Require(Time.realtimeSinceStartup < deadline, "Failed-control menu cleanup timeout.");
            yield return null;
        }
    }

    private IEnumerable<object?> VanillaLoadControl()
    {
        Require(File.ReadAllText(Path.Combine(_root!, "vanilla-load.enabled")).Trim() == "control-v1", "Invalid vanilla control marker.");
        Require(_scenario == "MissingApi" && !Chainloader.PluginInfos.ContainsKey("vgmodapi"), "Vanilla control requires absent API.");
        Require(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("vgmodapi") == true), "Vanilla control has API patches.");
        _vanillaEvidenceRoot = _root;
        new Harmony(Id).Patch(AccessTools.Method(AccessTools.TypeByName("Source.Simulation.World.SandboxWorld"), "GetFreeOrbit"),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(ObserveOrbitFailure)));
        var playerField = AccessTools.Field(AccessTools.TypeByName("Source.Player.GamePlayer"), "current");
        var gameType = AccessTools.TypeByName("Behaviour.GameManager");
        var scenesType = AccessTools.TypeByName("Behaviour.Bootstrap.SceneLoader");
        var gameplayType = AccessTools.TypeByName("GameplayManager");
        var gameplayField = AccessTools.Field(gameplayType, "Instance");
        var initialized = AccessTools.Field(gameplayType, "_initialized");
        Time.timeScale = 1;
        var menuSettle = Time.realtimeSinceStartup + 2;
        while (Time.realtimeSinceStartup < menuSettle) yield return null;
        object? previous = null;
        foreach (var name in new[] { "fixture-a", "fixture-b" })
        {
            var file = AccessTools.Method(AccessTools.TypeByName("Source.Util.SaveGame"), "GetSaveGame").Invoke(null, new object[] { name });
            Require(file != null, "Vanilla control fixture missing.");
            Time.timeScale = 1;
            AccessTools.Method(gameType, "LoadGame").Invoke(AccessTools.Property(gameType, "Instance").GetValue(null), new[] { file });
            var deadline = Time.realtimeSinceStartup + 90;
            while (true)
            {
                var player = playerField.GetValue(null);
                var gameplay = gameplayField.GetValue(null) as UnityEngine.Object;
                if (player != null && !ReferenceEquals(player, previous) && gameplay
                    && SceneManager.GetSceneByName("Gameplay").isLoaded && (bool)initialized.GetValue(gameplay)!) break;
                Require(_orbitFailure == null, "Observed vanilla orbit failure: " + _orbitFailure);
                Require(Time.realtimeSinceStartup < deadline, "Vanilla control gameplay initialization timeout.");
                yield return null;
            }
            var settle = Time.realtimeSinceStartup + 2;
            while (Time.realtimeSinceStartup < settle) yield return null;
            var settledPlayer = playerField.GetValue(null);
            var settledGameplay = gameplayField.GetValue(null) as UnityEngine.Object;
            Require(settledPlayer != null && !ReferenceEquals(settledPlayer, previous) && settledGameplay
                && SceneManager.GetSceneByName("Gameplay").isLoaded && (bool)initialized.GetValue(settledGameplay)!, "Vanilla initialized state did not persist.");
            Require(_orbitFailure == null, "Observed vanilla orbit failure: " + _orbitFailure);
            Logger.LogInfo("QA PASS: copied gameplay initialization without API lifecycle hooks: " + name);
            previous = playerField.GetValue(null);
            // Match Full's direct replacement load; GameManager cleans the previous player.
            if (name == "fixture-a") continue;
            // Preserve vanilla player cleanup, but omit the options-menu save action.
            AccessTools.Method(previous!.GetType(), "Cleanup").Invoke(previous, null);
            AccessTools.Method(scenesType, "StartMenu").Invoke(AccessTools.Property(scenesType, "Instance").GetValue(null), null);
            deadline = Time.realtimeSinceStartup + 90;
            while (playerField.GetValue(null) != null || !SceneManager.GetSceneByName("Main Menu").isLoaded || SceneManager.GetSceneByName("Gameplay").isLoaded)
            {
                Require(_orbitFailure == null, "Observed vanilla orbit failure: " + _orbitFailure);
                Require(Time.realtimeSinceStartup < deadline, "Vanilla control menu return timeout.");
                yield return null;
            }
            yield return null; // Let the newly loaded menu run Start before the next load.
        }
        Require(_orbitFailure == null, "Observed vanilla orbit failure: " + _orbitFailure);
        Require(!Chainloader.PluginInfos.ContainsKey("vgmodapi"), "API appeared during vanilla control.");
        File.WriteAllText(Path.Combine(_root!, "vanilla-load-control.txt"), "PASS\nTwo copied loads initialized without API lifecycle hooks; original historical failure cause is not established.");
    }
}
