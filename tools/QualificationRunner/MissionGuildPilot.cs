using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private static object? _guildProbeMission;
    private static Exception? _guildProbeFailure;
    private static void StopProbeFocus(object __0)
    { if (ReferenceEquals(__0, _guildProbeMission) && _guildProbeFailure != null) throw _guildProbeFailure; }
    private void CheckGuildLaunches()
    {
        if (!File.Exists(Path.Combine(_root!, "mission-transitions.enabled"))) return;
        var player = CurrentPlayer;
        var station = SpGet(AccessTools.TypeByName("Source.Galaxy.POI.SpaceStation"), "current");
        Require(station != null, "Guild probe requires a current station.");
        var missionBase = AccessTools.TypeByName("Source.MissionSystem.Mission");
        var api = ModApi.Missions!;
        var access = (IVersionSensitiveMissionAccess)api;
        foreach (var kind in new[] { "Bounty", "Patrol", "Industry" })
        {
            var missionType = AccessTools.TypeByName("Source.MissionSystem." + kind + "Mission");
            var mission = Activator.CreateInstance(missionType)!;
            var id = "vgmodapi-guild-" + Guid.NewGuid().ToString("N");
            AccessTools.Field(missionBase, "name").SetValue(mission, "VGModAPI guild probe " + kind);
            AccessTools.Field(missionBase, "storyId").SetValue(mission, id);
            AccessTools.Field(missionBase, "dynamicLevel").SetValue(mission, true);
            AccessTools.Field(missionBase, "trackedOnHud").SetValue(mission, false);
            var slot = AccessTools.Field(player.GetType(), "current" + kind); var old = slot.GetValue(player);
            var boardData = SpGet(station!, char.ToLowerInvariant(kind[0]) + kind.Substring(1) + "Board")!;
            var counter = AccessTools.Field(boardData.GetType(), char.ToLowerInvariant(kind[0]) + kind.Substring(1) + "Counter");
            var oldCounter = counter.GetValue(boardData);
            var observed = new List<MissionTransitionKind>(); int removedOld = 0;
            using var subscription = api.Subscribe("qualification.guild", e =>
            {
                if (e.Mission.DefinitionId == id) observed.Add(e.Kind);
                if (old != null && e.Kind == MissionTransitionKind.Removed && access.TryGetNative(e.Mission, out var native) && ReferenceEquals(native, old)) removedOld++;
            });
            var gameObject = new GameObject("VGModAPI disposable guild probe"); gameObject.SetActive(false);
            var patcher = new Harmony("vgmodapi.qualification.guild." + Guid.NewGuid().ToString("N"));
            bool caught = false; var expected = new InvalidOperationException("VGModAPI intentional stop before focus/travel");
            try
            {
                var boardType = AccessTools.TypeByName("Behaviour.UI.Spacestation.Location." + kind + "Board");
                var board = gameObject.AddComponent(boardType);
                AccessTools.Field(boardType, "selectedMission").SetValue(board, mission);
                patcher.Patch(AccessTools.Method(AccessTools.TypeByName("FocusedMissionHandler"), "SetMission", new[] { missionBase }),
                    prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(StopProbeFocus), BindingFlags.NonPublic | BindingFlags.Static)));
                _guildProbeMission = mission; _guildProbeFailure = expected;
                try { boardType.GetMethod("LaunchClicked")!.Invoke(board, null); }
                catch (TargetInvocationException error) when (ReferenceEquals(error.InnerException, expected)) { caught = true; }
                Require(caught && ReferenceEquals(slot.GetValue(player), mission), "Guild assignment did not reach the controlled boundary: " + kind);
                Require(observed.SequenceEqual(new[] { MissionTransitionKind.Accepted }) && (old == null || removedOld == 1), "Guild assignment transitions incorrect: " + kind);
            }
            finally
            {
                _guildProbeMission = null; _guildProbeFailure = null;
                try { patcher.UnpatchSelf(); }
                finally { slot.SetValue(player, old); counter.SetValue(boardData, oldCounter); UnityEngine.Object.DestroyImmediate(gameObject); }
            }
            Require(ReferenceEquals(slot.GetValue(player), old) && Equals(counter.GetValue(boardData), oldCounter), "Guild probe state restoration failed.");
            Passed("native-guild-assignment-" + kind);
        }
        File.WriteAllText(Path.Combine(_root!, "mission-guild.txt"), "PASS\nThree actual board assignment callbacks, stopped before focus/travel by injected exceptions. Not complete UI/travel qualification.");
    }
}
