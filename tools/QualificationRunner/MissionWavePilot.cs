using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckGuildWaves()
    {
        if (!File.Exists(Path.Combine(_root!, "mission-transitions.enabled"))) yield break;
        var trace = new List<string>();
        foreach (var kind in new[] { "Industry", "Patrol" })
        foreach (var force in new[] { false, true })
        {
            foreach (var frame in SpLoad("fixture-a")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var station = SpGet(AccessTools.TypeByName("Source.Galaxy.POI.SpaceStation"), "current")!;
            Require(station != null, "Wave probe requires a station fixture.");
            var boardName = char.ToLowerInvariant(kind[0]) + kind.Substring(1) + "Board";
            var boardField = AccessTools.Field(station!.GetType(), boardName);
            var boardData = boardField.GetValue(station) ?? Activator.CreateInstance(boardField.FieldType, new[] { station })!;
            boardField.SetValue(station, boardData);
            var missions = (IEnumerable)SpCall(boardData, "Generate" + kind + "Missions");
            var mission = missions.Cast<object>().First();
            var missionType = mission.GetType();
            var slot = AccessTools.Field(CurrentPlayer.GetType(), "current" + kind);
            var poi = SpGet(((IList)SpGet(mission, "steps")!)[0]!, "dynamicPointOfInterest")!;
            var access = (IVersionSensitiveMissionAccess)ModApi.Missions!;
            var owned = new HashSet<object> { mission };
            var observed = new List<MissionTransition>();
            using var subscription = ModApi.Missions!.Subscribe("qualification.waves", e =>
            {
                if (!access.TryGetNative(e.Mission, out var native) || native == null) return;
                if (e.Kind == MissionTransitionKind.Accepted && missionType.IsInstanceOfType(native) && ReferenceEquals(slot.GetValue(CurrentPlayer), native)) owned.Add(native);
                if (!owned.Contains(native)) return;
                observed.Add(e); trace.Add(kind + "\t" + force + "\t" + e.Kind + "\t" + e.Mission.InstanceId);
            });
            var ui = new GameObject("VGModAPI native wave launcher"); ui.SetActive(false);
            try
            {
                var boardType = AccessTools.TypeByName("Behaviour.UI.Spacestation.Location." + kind + "Board");
                var board = ui.AddComponent(boardType);
                AccessTools.Field(boardType, "selectedMission").SetValue(board, mission);
                var ship = SpGet(SpGet(AccessTools.TypeByName("GameplayManager"), "Instance")!, "spaceShip")!;
                Require((bool)AccessTools.Method(ship.GetType(), "AmmoInCargoForTurrets", new[] { typeof(bool) }).Invoke(ship, new object[] { false }), "Wave launch refused: fixture has no turret ammo.");
                var travel = SpGet(AccessTools.TypeByName("Behaviour.Managers.TravelManager"), "Instance")!;
                // No focus/travel exception guard: exercise the full native launch and actual arrival.
                boardType.GetMethod("LaunchClicked")!.Invoke(board, null);
                UnityEngine.Object.DestroyImmediate(ui);
                Require(ReferenceEquals(slot.GetValue(CurrentPlayer), mission), "Wave launch did not assign the mission.");
                Require(ReferenceEquals(SpGet(travel, "targetPoi"), poi), "Wave travel refused: inspect engine/reactor, cargo and emergency-jump state.");
                foreach (var frame in Wait(() => ReferenceEquals(SpGet(AccessTools.TypeByName("Source.Galaxy.MapPointOfInterest"), "current"), poi)
                    && (bool)SpCall(travel, "IsLocalPoiReady"), "wave local POI readiness " + kind)) yield return frame;
                Require(ReferenceEquals(slot.GetValue(CurrentPlayer), mission), "Wave fixture was replaced before its claim.");
                Require(observed.Select(e => e.Kind).SequenceEqual(new[] { MissionTransitionKind.Accepted }), "Initial wave acceptance mismatch.");
                Require(!(bool)SpCall(mission, "CanClaimRewards"), "Wave fixture must be initially ineligible.");
                var oldId = observed[0].Mission.InstanceId;
                observed.Clear();
                missionType.GetMethod("ClaimRewards")!.Invoke(mission, new object[] { force });
                var next = slot.GetValue(CurrentPlayer);
                Require(next != null && !ReferenceEquals(next, mission) && (int)SpGet(next, "wave")! == (int)SpGet(mission, "wave")! + 1, "Native override did not construct and assign the next wave.");
                Require(observed.Select(e => e.Kind).SequenceEqual(new[] { force ? MissionTransitionKind.Completed : MissionTransitionKind.Removed, MissionTransitionKind.Accepted }), "Native wave claim emitted incorrect completion/membership facts.");
                Require(observed[0].Mission.InstanceId == oldId && observed[1].Mission.InstanceId != oldId, "Wave replacement did not preserve/separate occurrence identity.");
                Require(ReferenceEquals(SpGet(SpGet(AccessTools.TypeByName("Behaviour.UI.Missions.FocusedMissionHandler"), "Instance")!, "focusedMission"), next), "Native wave focus did not complete.");
                Passed("native-wave-" + kind + (force ? "-forced" : "-ineligible"));
            }
            finally
            {
                if (ui) UnityEngine.Object.DestroyImmediate(ui);
                File.WriteAllLines(Path.Combine(_root!, "mission-wave-events.tsv"), trace);
            }
        }
        foreach (var frame in SpLoad("fixture-a")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        File.WriteAllText(Path.Combine(_root!, "mission-waves.txt"), "PASS\nActual Industry/Patrol generation, launch, arrival and next-wave override execution; ineligible replacement is not completion; forced removal is completion. Not complete combat/crafting qualification.");
    }
}
