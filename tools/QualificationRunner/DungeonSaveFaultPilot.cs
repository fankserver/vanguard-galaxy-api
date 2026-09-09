using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonSaveFailures(Func<bool> unchanged)
    {
        WriteAtomic("dungeon-save-faults.txt", new[] { "INCOMPLETE" });
        var player = SpGet(NativeType("Source.Player.GamePlayer"), "current")!;
        var ephemeral = player.GetType().GetField("isEphemeral", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var original = (bool)ephemeral.GetValue(player)!;
        Require(!original, "Save failure fixture must not already be ephemeral.");
        try { ephemeral.SetValue(player, true); Save("qa-dungeon-skipped", LifecycleEventKind.SaveSkipped); }
        finally { ephemeral.SetValue(player, original); }
        Require(!File.Exists(Path.Combine(_saveRoot!, "qa-dungeon-skipped.save")) && !File.Exists(Path.Combine(_saveRoot!, "qa-dungeon-skipped.meta")), "Skipped dungeon save wrote an artifact.");
        yield return null; yield return null;
        Require(unchanged(), "Skipped save changed active dungeon state.");
        // Same native exhausted-retry mechanism as the base qualification scenario, confined to copied saves.
        var blocked = Path.Combine(_saveRoot!, "qa-dungeon-failed.meta");
        Require(!File.Exists(blocked) && !Directory.Exists(blocked), "Failure injection path already exists.");
        Directory.CreateDirectory(blocked);
        try { Save("qa-dungeon-failed", LifecycleEventKind.SaveFailed); }
        finally { Directory.Delete(blocked); }
        yield return null; yield return null;
        Require(unchanged(), "Failed save changed active dungeon state.");
        WriteAtomic("dungeon-save-faults.txt", new[] { "PASS", "dungeon-save-faults-v1", "skipped-and-failed-write-live-state-preserved" });
    }
}
