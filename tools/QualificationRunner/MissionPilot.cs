using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using HarmonyLib;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckMissionTransitions()
    {
        if (!File.Exists(Path.Combine(_root!, "mission-transitions.enabled"))) return;
        var api = ModApi.Missions ?? throw new InvalidOperationException("Mission service missing.");
        Require(ModApi.Current!.Capabilities.Any(c => c.Name == "mission-transitions" && c.Available), "Mission capability unavailable.");
        var nativeAccess = api as IVersionSensitiveMissionAccess ?? throw new InvalidOperationException("Native mission access missing.");
        var type = AccessTools.TypeByName("Source.MissionSystem.Mission");
        var story = AccessTools.TypeByName("Source.MissionSystem.StoryMission");
        var player = CurrentPlayer;
        var add = player.GetType().GetMethod("AddMissionWithLog", new[] { type, typeof(bool) })!;
        var remove = player.GetType().GetMethod("RemoveMission", new[] { type, typeof(bool) })!;
        var active = (IList)AccessTools.Field(player.GetType(), "missions").GetValue(player);
        var archive = (IList)AccessTools.Field(player.GetType(), "missionsArchive").GetValue(player);
        var registry = (IDictionary)AccessTools.Field(story, "allMissions").GetValue(null);
        string prefix = "vgmodapi-qa-" + Guid.NewGuid().ToString("N");
        string replacementId = prefix + "-replacement";
        var observed = new List<MissionTransition>(); var trace = new List<string>(); bool nativeValid = true;
        using var subscription = api.Subscribe("qualification.missions", e =>
        {
            if (e.Mission.DefinitionId?.StartsWith(prefix, StringComparison.Ordinal) != true) return;
            observed.Add(e); trace.Add(e.Sequence + "\t" + e.Kind + "\t" + e.Mission.InstanceId.ToString("N"));
            nativeValid &= nativeAccess.TryGetNative(e.Mission, out var value) && value != null && type.IsInstanceOfType(value);
        });
        object Create(string id, bool failed = false)
        {
            var value = Activator.CreateInstance(type)!;
            AccessTools.Field(type, "name").SetValue(value, "VGModAPI mission probe " + id);
            AccessTools.Field(type, "storyId").SetValue(value, id);
            AccessTools.Field(type, "failed").SetValue(value, failed);
            AccessTools.Field(type, "dynamicLevel").SetValue(value, true);
            AccessTools.Field(type, "trackedOnHud").SetValue(value, false);
            return value;
        }
        void Add(object value) => add.Invoke(player, new[] { value, (object)false });
        void Expect(params MissionTransitionKind[] kinds) => Require(observed.Select(e => e.Kind).SequenceEqual(kinds), "Unexpected native mission ordering: " + string.Join(",", observed.Select(e => e.Kind)));
        try
        {
            Require(!registry.Contains(replacementId), "Probe registry ID collision.");
            var generatorType = story.GetNestedType("CreateMission")!;
            var body = Expression.MemberInit(Expression.New(type), Expression.Bind(AccessTools.Field(type, "name"), Expression.Constant("VGModAPI mission probe replacement")),
                Expression.Bind(AccessTools.Field(type, "dynamicLevel"), Expression.Constant(true)), Expression.Bind(AccessTools.Field(type, "trackedOnHud"), Expression.Constant(false)));
            var generator = Expression.Lambda(generatorType, body, Expression.Parameter(player.GetType(), "owner")).Compile();
            var definition = Activator.CreateInstance(story, replacementId, generator, null, null)!;
            story.GetMethod("Add")!.Invoke(null, new[] { definition });

            var forced = Create(prefix + "-forced", true); Add(forced); Add(Create(prefix + "-forced"));
            Expect(MissionTransitionKind.Accepted);
            type.GetMethod("ClaimRewards")!.Invoke(forced, new object[] { false }); Expect(MissionTransitionKind.Accepted);
            type.GetMethod("ClaimRewards")!.Invoke(forced, new object[] { true });
            Expect(MissionTransitionKind.Accepted, MissionTransitionKind.Completed, MissionTransitionKind.Archived);
            Require(observed.Select(e => e.Mission.InstanceId).Distinct().Count() == 1, "One mission acquired multiple IDs.");
            Require(!nativeAccess.TryGetNative(observed[0].Mission, out _), "Native escape outlived dispatch.");
            var oldId = observed[0].Mission.InstanceId;
            var repeated = Create(prefix + "-forced"); add.Invoke(player, new[] { repeated, (object)true });
            Require(observed.Last().Kind == MissionTransitionKind.Accepted && observed.Last().Mission.InstanceId != oldId, "Repeated definition reused occurrence identity.");
            type.GetMethod("ClaimRewards")!.Invoke(repeated, new object[] { false });
            Require(observed.Last().Kind == MissionTransitionKind.Archived, "Eligible claim did not archive.");

            observed.Clear();
            var failure = Create(prefix + "-failure"); AccessTools.Field(type, "nextMissionOnFailed").SetValue(failure, replacementId); Add(failure);
            type.GetMethod("MissionFailed")!.Invoke(failure, new object[] { "VGModAPI disposable probe" });
            Expect(MissionTransitionKind.Accepted, MissionTransitionKind.Failed, MissionTransitionKind.Removed, MissionTransitionKind.Accepted);
            Require(nativeValid, "Native access failed during observed callbacks.");
            File.WriteAllLines(Path.Combine(_root!, "mission-events.tsv"), trace);
            File.WriteAllLines(Path.Combine(_root!, "mission-transitions.txt"), new[] { "PASS", "Native duplicate/no-eligibility/forced/eligible/repeated/archive/failure-replacement probes passed; no cross-load identity claim." });
            Passed("native-mission-transitions");
        }
        finally
        {
            foreach (var value in active.Cast<object>().ToArray())
                if (((string?)AccessTools.Field(type, "storyId").GetValue(value))?.StartsWith(prefix, StringComparison.Ordinal) == true)
                    remove.Invoke(player, new[] { value, (object)true });
            foreach (var id in archive.Cast<object>().ToArray()) if ((id as string)?.StartsWith(prefix, StringComparison.Ordinal) == true) archive.Remove(id);
            registry.Remove(replacementId);
        }
    }
}
