using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using TMPro;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> ModInformationGameplay(Action<string> record)
    {
        Require(JournalSelected && StockpileSelected, "Full information qualification needs both real consumers.");
        ModApi.Mods!.Refresh();
        foreach (var id in new[] { "vgmissionjournal", "vgstockpile" })
        {
            var loaded = Chainloader.PluginInfos[id];
            var row = ModApi.Mods.Snapshot.Single(item => item.PluginId == id);
            Require(row.Name == loaded.Metadata.Name && row.InstalledVersion == loaded.Metadata.Version,
                "Inventory disagrees with real loader metadata.");
            Require(row.MetadataStatus == ModMetadataStatus.Missing && row.Metadata == null, "Expected metadata-free real consumer.");
        }
        record("inventory-two-real-consumers-without-metadata");
        var metadata = Path.Combine(Path.GetDirectoryName(Chainloader.PluginInfos[Id].Location)!, Id + ".vgmod.json");
        var pluginRoot = Path.GetFullPath(Path.Combine(_root!, "game", "BepInEx", "plugins")) + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(metadata).StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase) && !File.Exists(metadata), "Unsafe metadata fixture destination.");
        var created = false;
        var oldKeyboard = Keyboard.current;
        Keyboard? keyboard = null;
        try
        {
            keyboard = InputSystem.AddDevice<Keyboard>();
            using (var stream = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write)) { created = true; }
            foreach (var invalid in new[] { "not JSON", "{\"schemaVersion\":1,\"pluginId\":\"not-the-driver\"}" })
            {
                File.WriteAllText(metadata, invalid); ModApi.Mods.Refresh();
                var row = ModApi.Mods.Snapshot.Single(item => item.PluginId == Id);
                Require(row.MetadataStatus == ModMetadataStatus.Invalid && row.Metadata == null &&
                    row.Name == Chainloader.PluginInfos[Id].Metadata.Name && row.InstalledVersion == Chainloader.PluginInfos[Id].Metadata.Version,
                    "Bad metadata changed authoritative loader identity.");
                Require(ModApi.Mods.Snapshot.Any(item => item.PluginId == "vgmissionjournal") && ModApi.Mods.Snapshot.Any(item => item.PluginId == "vgstockpile"),
                    "Bad metadata hid other consumers.");
                var events = EventSystem.current;
                events.SetSelectedGameObject(GameObject.Find("VGModAPI Mods"));
                foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
                var panel = GameObject.Find("VGModAPI Mods panel");
                var driverRow = panel.GetComponentsInChildren<Button>().Single(button => button.GetComponentInChildren<TMP_Text>().text.Contains("Controlled Qualification"));
                events.SetSelectedGameObject(driverRow.gameObject);
                foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
                Require(panel.GetComponentsInChildren<TMP_Text>().Any(text => text.text.Contains("Optional author metadata is invalid; installed identity is still shown.")), "Invalid metadata was not presented truthfully.");
                foreach (var frame in MenuKey(keyboard, Key.Escape)) yield return frame;
                Require(!panel.activeSelf, "Metadata fixture menu did not close.");
            }
        }
        finally
        {
            if (keyboard != null) InputSystem.RemoveDevice(keyboard);
            oldKeyboard?.MakeCurrent();
            if (created) File.Delete(metadata); ModApi.Mods.Refresh();
        }
        record("inventory-malformed-wrong-guid-isolated");
        foreach (var name in new[] { "fixture-a", "fixture-b" })
        {
            var previous = _api!.CurrentSession?.Id;
            Load(name);
            foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _api.CurrentSession.Id != previous, "information gameplay load")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            Require(GameObject.Find("VGModAPI Mods") == null && GameObject.Find("VGModAPI Mods panel") == null, "Menu UI survived into gameplay.");
            CheckJournalLoad(name); CheckStockpileLoad(name);
            foreach (var frame in Wait(() => (bool)SpGet(Stockpile, "IconAttached")!, "coexisting Stockpile UI")) yield return frame;
            Require(SpGet(Stockpile, "_icon") is UnityEngine.Object icon && icon, "Real UI consumer lost its icon.");
            Invoke(Instance(_scenes), "StartMenu");
            foreach (var frame in Wait(() => SceneManager.GetSceneByName("Main Menu").isLoaded && ModMenuSessionChecks.Inactive(_api.CurrentSession, true) && AccessTools.Field(_player, "current").GetValue(null) == null && GameObject.Find("VGModAPI Mods") != null, "information return from gameplay")) yield return frame;
            var entry = GameObject.Find("VGModAPI Mods");
            Require(entry.transform.parent.GetComponentsInChildren<Button>().Count(button => button.name == "VGModAPI Mods") == 1, "Return duplicated the entry.");
        }
        record("gameplay-two-loads-return-single-entry");
        record("real-stockpile-ui-journal-coexistence");
        foreach (var frame in NewGameAndSpaceLoad(false)) yield return frame;
        foreach (var frame in Wait(() => ModMenuSessionChecks.Inactive(_api!.CurrentSession, true) && AccessTools.Field(_player, "current").GetValue(null) == null && GameObject.Find("VGModAPI Mods") != null, "information post-new-game menu")) yield return frame;
        record("new-game-save-load-return");
        // Subsequent menu-only assertions cover that phase, not the already-recorded gameplay phases.
        _events.Clear();
    }
}
