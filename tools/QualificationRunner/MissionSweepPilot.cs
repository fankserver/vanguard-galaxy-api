using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private static void ArmNativeMissionProbe(string boundary, object? target, Exception? error) =>
        AccessTools.Method(AccessTools.TypeByName("VGModAPI.QualificationGuard.Plugin"), "ArmMissionProbe")
            .Invoke(null, new object?[] { boundary, target, error });
    private void CheckMissionSweepClear()
    {
        if (!File.Exists(Path.Combine(_root!, "mission-transitions.enabled"))) return;
        var api = ModApi.Missions ?? throw new InvalidOperationException("Mission service missing.");
        var player = CurrentPlayer;
        var missionType = AccessTools.TypeByName("Source.MissionSystem.Mission");
        var active = (IList)AccessTools.Field(player.GetType(), "missions").GetValue(player);
        var retained = active.Cast<object>().ToArray();
        var map = SpGet(player, "map")!;
        var id = "vgmodapi-clear-" + Guid.NewGuid().ToString("N");
        Require(!retained.Any(m => (string?)AccessTools.Field(missionType, "storyId").GetValue(m) == id), "Clear probe ID collision.");
        var mission = Activator.CreateInstance(missionType)!;
        AccessTools.Field(missionType, "name").SetValue(mission, "VGModAPI clear probe");
        AccessTools.Field(missionType, "storyId").SetValue(mission, id);
        AccessTools.Field(missionType, "dynamicLevel").SetValue(mission, true);
        AccessTools.Field(missionType, "trackedOnHud").SetValue(mission, false);
        var observed = new List<MissionTransitionKind>();
        using var subscription = api.Subscribe("qualification.mission-clear", e => { if (e.Mission.DefinitionId == id) observed.Add(e.Kind); });
        var expected = new InvalidOperationException("VGModAPI intentional stop before map mutation");
        bool caught = false;
        try
        {
            ArmNativeMissionProbe("clear", map, expected);
            player.GetType().GetMethod("AddMissionWithLog", new[] { missionType, typeof(bool) })!.Invoke(player, new[] { mission, (object)false });
            try { player.GetType().GetMethod("TransitionTutorialToSandbox")!.Invoke(player, null); }
            catch (TargetInvocationException error) when (ReferenceEquals(error.InnerException, expected)) { caught = true; }
            Require(caught && active.Count == 0, $"Actual tutorial clear or exception identity missing: caught={caught}, remaining={active.Count}.");
            Require(observed.SequenceEqual(new[] { MissionTransitionKind.Accepted, MissionTransitionKind.Removed }), "Bulk clear invented abandonment or lost removal.");
        }
        finally
        {
            try { ArmNativeMissionProbe("clear", null, null); }
            finally { active.Clear(); foreach (var value in retained) active.Add(value); }
        }
        Require(active.Count == retained.Length && active.Cast<object>().Zip(retained, ReferenceEquals).All(equal => equal), "Copied mission membership not restored.");
        File.WriteAllText(Path.Combine(_root!, "mission-clear.txt"), "PASS\nActual mission clear observed before an injected map-clear exception; exception identity preserved. Not full tutorial-to-sandbox qualification.");
        Passed("native-mission-bulk-clear-exception");
    }
}
