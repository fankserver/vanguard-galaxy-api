using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Bootstrap;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonConsumers(Mouse mouse)
    {
        var marker = Path.Combine(_root!, "dungeon-consumers.enabled");
        if (!File.Exists(marker)) yield break;
        Require(File.ReadAllText(marker) == "dungeon-consumers-v1", "Invalid dungeon consumer marker.");
        WriteAtomic("dungeon-consumers.txt", new[] { "INCOMPLETE" });
        Require(Chainloader.PluginInfos.ContainsKey("vg.boardalways") && Chainloader.PluginInfos.ContainsKey("vgmodapi.example.cargo"), "Both actual consumer plugins must be loaded.");
        foreach (var frame in Wait(() => DungeonProbeButton("Attach cargo encounter") != null, "Actual author attachment action")) yield return frame;
        var previous = EventSystem.current.currentSelectedGameObject;
        try
        {
            // This callback belongs to the separately loaded author plugin, not the runner.
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Attach cargo encounter")!.gameObject);
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Attach cargo encounter")!.transform)) yield return frame;
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Attach cargo encounter")!.transform)) yield return frame;
            // Post-exit verification requires exactly one Attached and one TargetInUse result in the consumer log.
            WriteAtomic("dungeon-consumers.txt", new[] { "INPUTS_SENT", "dungeon-consumers-v1", "attach-then-duplicate" });
        }
        finally { if (EventSystem.current) EventSystem.current.SetSelectedGameObject(previous ? previous : null); }
    }
}
