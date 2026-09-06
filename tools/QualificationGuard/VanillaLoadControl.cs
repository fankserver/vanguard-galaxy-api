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
    private IEnumerable<object?> VanillaLoadControl()
    {
        Require(_scenario == "MissingApi" && !Chainloader.PluginInfos.ContainsKey("vgmodapi"), "Vanilla control requires absent API.");
        Require(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("vgmodapi") == true), "Vanilla control has API patches.");
        var playerField = AccessTools.Field(AccessTools.TypeByName("Source.Player.GamePlayer"), "current");
        var gameType = AccessTools.TypeByName("Behaviour.GameManager");
        var scenesType = AccessTools.TypeByName("Behaviour.Bootstrap.SceneLoader");
        var gameplayType = AccessTools.TypeByName("GameplayManager");
        var gameplayField = AccessTools.Field(gameplayType, "Instance");
        var initialized = AccessTools.Field(gameplayType, "_initialized");
        foreach (var name in new[] { "fixture-a", "fixture-b" })
        {
            var previous = playerField.GetValue(null);
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
                Require(Time.realtimeSinceStartup < deadline, "Vanilla control gameplay initialization timeout.");
                yield return null;
            }
            var settle = Time.realtimeSinceStartup + 2;
            while (Time.realtimeSinceStartup < settle) yield return null;
            Require((bool)initialized.GetValue(gameplayField.GetValue(null))!, "Vanilla initialized state did not persist.");
            Logger.LogInfo("QA PASS: copied gameplay initialization without API lifecycle hooks: " + name);
            // Direct scene return deliberately avoids the save-producing options-menu action.
            AccessTools.Method(scenesType, "StartMenu").Invoke(AccessTools.Property(scenesType, "Instance").GetValue(null), null);
            deadline = Time.realtimeSinceStartup + 90;
            while (playerField.GetValue(null) != null || !SceneManager.GetSceneByName("Main Menu").isLoaded || SceneManager.GetSceneByName("Gameplay").isLoaded)
            {
                Require(Time.realtimeSinceStartup < deadline, "Vanilla control menu return timeout.");
                yield return null;
            }
        }
        Require(!Chainloader.PluginInfos.ContainsKey("vgmodapi"), "API appeared during vanilla control.");
        File.WriteAllText(Path.Combine(_root!, "vanilla-load-control.txt"), "PASS\nTwo copied loads initialized without API lifecycle hooks; original historical failure cause is not established.");
    }
}
