using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VGModAPI.QualificationGuard;

// Development-only bootstrap; deliberately has no API assembly dependency.
[BepInPlugin(Id, "VGModAPI Qualification Isolation", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.qualification.guard";
    private static string? _saves;
    private string? _root;
    private string? _scenario;
    private bool _armed;
    private static int _injectedHashes;

    private void Awake()
    {
        var args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--vgmodapi-qualification-root");
        if (flag < 0) return;
        try
        {
            Require(flag + 1 < args.Length, "Missing root argument.");
            _root = Path.GetFullPath(args[flag + 1]);
            Require(File.ReadAllText(Path.Combine(_root, "qualification.marker")).Trim() == "vgmodapi-disposable-sandbox-v1", "Missing sandbox marker.");
            var harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("SteamManager"), "Awake"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSteam)));
            harmony.Patch(AccessTools.PropertyGetter(AccessTools.TypeByName("SteamManager"), "Initialized"), prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipSteam)));
            Require(Same(Path.GetDirectoryName(Application.dataPath)!, Path.Combine(_root, "game")), "Not the sandbox executable.");
            var save = AccessTools.TypeByName("Source.Util.SaveGame");
            var original = (string)AccessTools.Field(save, "SavesPath").GetValue(null)!;
            Require(Same(original, File.ReadAllText(Path.Combine(_root, "original-save-directory.txt")).Trim()), "Manifest does not protect the actual save directory.");
            _saves = Path.Combine(_root, "Saves");
            Require(!Same(original, _saves), "Refusing real save target.");
            Directory.CreateDirectory(_saves);
            harmony.Patch(AccessTools.Method(save, "Store"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckStore)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Source.Util.SaveGameFile"), "Recall"), prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckRecall)));
            AccessTools.Field(save, "SavesPath").SetValue(null, _saves);
            AccessTools.Field(save, "SavesDir").SetValue(null, new DirectoryInfo(_saves));
            AccessTools.Field(save, "_saves").SetValue(null, null);
            _scenario = File.ReadAllText(Path.Combine(_root, "scenario.txt")).Trim();
            Require(_scenario == "Full" || _scenario == "MissingApi" || _scenario == "UnavailableApi", "Unknown scenario.");
            if (_scenario == "UnavailableApi")
            {
                // Relies on BepInEx's plugin resolver before API Awake; recheck on loader upgrades.
                var api = Assembly.Load("VGModAPI").GetType("VGModAPI.Plugin", true)!;
                harmony.Patch(AccessTools.Method(api, "ReadAssemblyHash"), postfix: new HarmonyMethod(typeof(Plugin), nameof(InjectHash)));
                Logger.LogWarning("QA ONLY: injecting a mismatched hash result; game assembly bytes are unchanged.");
            }
            File.WriteAllText(Path.Combine(_root, "isolation-armed.txt"), _saves);
            _armed = true;
        }
        catch (Exception ex) { Finish(false, ex.ToString()); }
    }

    private IEnumerator Start()
    {
        if (!_armed || _scenario == "Full") yield break;
        float deadline = Time.realtimeSinceStartup + 90;
        while (!SceneManager.GetSceneByName("Main Menu").isLoaded)
        {
            if (Time.realtimeSinceStartup >= deadline) { Finish(false, "Menu timeout."); yield break; }
            yield return null;
        }
        yield return null;
        try
        {
            if (_scenario == "MissingApi")
                Require(!Chainloader.PluginInfos.ContainsKey("vgmodapi"), "API unexpectedly loaded.");
            else
            {
                Require(_injectedHashes == 1, "Hash probe was not exercised exactly once.");
                var api = Assembly.Load("VGModAPI.Abstractions").GetType("VGModAPI.ModApi", true)!;
                var service = api.GetProperty("Current")!.GetValue(null)!;
                Require(service != null, "Unavailable API service missing.");
                var caps = (IEnumerable)service!.GetType().GetProperty("Capabilities")!.GetValue(service)!;
                int found = 0;
                foreach (var cap in caps)
                {
                    var type = cap.GetType();
                    var name = (string)type.GetProperty("Name")!.GetValue(cap)!;
                    if (name != "session-lifecycle" && name != "save-outcomes") continue;
                    found++;
                    Require(!(bool)type.GetProperty("Available")!.GetValue(cap)!, "Capability remained available.");
                }
                Require(found == 2, "Expected capabilities missing.");
                Require(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("vgmodapi") == true), "API integration patches survived mismatch.");
            }
            if (File.Exists(Path.Combine(_root!, "missionjournal.enabled")))
            {
                if (_scenario == "MissingApi")
                    Require(!Chainloader.PluginInfos.ContainsKey("vgmissionjournal"), "Journal loaded without required API.");
                else
                {
                    Require(Chainloader.PluginInfos.TryGetValue("vgmissionjournal", out var journal) && journal.Instance != null
                        && !journal.Instance.enabled, "Journal did not disable itself for unavailable API.");
                    var facade = Assembly.Load("VGMissionJournal").GetType("VGMissionJournal.Api.MissionJournalApi", true)!;
                    Require(facade.GetProperty("Current")!.GetValue(null) == null, "Unavailable journal published a facade.");
                    Require(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("vgmissionjournal") == true), "Unavailable journal installed patches.");
                }
            }
            if (File.Exists(Path.Combine(_root!, "stockpile.enabled")))
            {
                if (_scenario == "MissingApi")
                    Require(!Chainloader.PluginInfos.ContainsKey("vgstockpile"), "Stockpile loaded without required API.");
                else
                {
                    Require(Chainloader.PluginInfos.TryGetValue("vgstockpile", out var stockpile) && stockpile.Instance != null
                        && !stockpile.Instance.enabled, "Stockpile did not disable itself for unavailable API.");
                    Require(AccessTools.Field(stockpile!.Instance!.GetType(), "_engine").GetValue(stockpile.Instance) == null, "Unavailable Stockpile created an engine.");
                    Require(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("vgstockpile") == true), "Unavailable Stockpile installed patches.");
                }
            }
            Finish(true, _scenario + "; selected consumer refusal checked."
                + " No alternate game binary qualification claimed.");
        }
        catch (Exception ex) { Finish(false, ex.ToString()); }
    }

    private static void InjectHash(ref string __result) { _injectedHashes++; __result = new string('0', 64); }
    private static bool SkipSteam() => false;
    private static void CheckStore(string saveName)
    {
        var save = AccessTools.TypeByName("Source.Util.SaveGame");
        var path = (string)AccessTools.Field(save, "SavesPath").GetValue(null)!;
        var dir = (DirectoryInfo)AccessTools.Field(save, "SavesDir").GetValue(null)!;
        Require(_saves != null && Same(path, _saves) && Same(dir.FullName, _saves)
            && Same(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(path, saveName + ".save")))!, _saves), "Save escaped isolation.");
    }
    private static void CheckRecall(object __instance)
    {
        var file = (FileInfo)AccessTools.Field(__instance.GetType(), "File").GetValue(__instance)!;
        Require(_saves != null && Same(file.DirectoryName!, _saves), "Load escaped isolation.");
    }
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private void Finish(bool ok, string detail)
    {
        Logger.LogInfo((ok ? "QA PASS: " : "QA FAIL: ") + detail);
        if (_root != null) File.WriteAllText(Path.Combine(_root, "result.txt"), (ok ? "PASS\n" : "FAIL\n") + detail);
        Application.Quit();
    }
    // Do not unpatch on destruction: isolation must remain active through quit-time writes.
}
