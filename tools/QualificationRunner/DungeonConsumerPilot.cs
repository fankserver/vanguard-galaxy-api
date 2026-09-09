using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using BepInEx.Bootstrap;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private bool DungeonConsumerReady(List<string> records)
    {
        // Layout teardown removes its tail lease; wait for that row rebuild before retaining a click target.
        var ready = DungeonProbeButton("Dungeon scroll tail") == null && DungeonProbeButton("Attach cargo encounter") != null;
        var root = GameObject.Find("Mod API dungeon contributions");
        var text = "ready=" + ready + " board=" + Chainloader.PluginInfos.ContainsKey("vg.boardalways")
            + " author=" + Chainloader.PluginInfos.ContainsKey("vgmodapi.example.cargo")
            + " content=" + ModApi.Services.Dungeons.Availability.Reason
            + " panel=" + ModApi.Services.DungeonPanel.Availability.Reason
            + " operation=" + (ModApi.Services.DungeonPanel.Current?.Operation != null)
            + " labels=" + (root ? string.Join("|", root.GetComponentsInChildren<TMPro.TMP_Text>().Take(12).Select(label => label.text.Substring(0, Math.Min(100, label.text.Length)))) : "hidden");
        if (records.Count < 12 && !records.Contains(text)) { records.Add(text); WriteAtomic("dungeon-consumer-diagnostic.txt", records); }
        return ready;
    }
    private IEnumerable<object?> CheckDungeonConsumers(Mouse mouse)
    {
        var marker = Path.Combine(_root!, "dungeon-consumers.enabled");
        if (!File.Exists(marker)) yield break;
        Require(File.ReadAllText(marker) == "dungeon-consumers-v2", "Invalid dungeon consumer marker.");
        WriteAtomic("dungeon-consumers.txt", new[] { "INCOMPLETE" });
        Require(Chainloader.PluginInfos.ContainsKey("vg.boardalways") && Chainloader.PluginInfos.ContainsKey("vgmodapi.example.cargo"), "Both actual consumer plugins must be loaded.");
        var records = new List<string>();
        foreach (var frame in Wait(() => DungeonConsumerReady(records), "Actual author attachment action")) yield return frame;
        var previous = EventSystem.current.currentSelectedGameObject;
        try
        {
            // This callback belongs to the separately loaded author plugin, not the runner.
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Attach cargo encounter")!.gameObject);
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Attach cargo encounter")!.transform)) yield return frame;
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Attach cargo encounter")!.transform)) yield return frame;
            // Post-exit verification requires exactly one Attached and one TargetInUse result in the consumer log.
            foreach (var frame in CheckDungeonCommandAdmission()) yield return frame;
            WriteAtomic("dungeon-consumers.txt", new[] { "INPUTS_SENT", "dungeon-consumers-v2", "attach-then-duplicate-command-admission-cancel" });
        }
        finally { if (EventSystem.current) EventSystem.current.SetSelectedGameObject(previous ? previous : null); }
    }
}
