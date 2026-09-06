using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckAnimaMissions()
    {
        if (!File.Exists(Path.Combine(_root!, "anima-missions.enabled"))) yield break;
        var anima = Chainloader.PluginInfos["vganima"].Instance;
        Require(anima.enabled && (bool)SpGet(anima, "_active")!, "Anima provider did not start.");
        var retiredHooks = new[] { "AddMissionWithLog", "ClaimRewards", "MissionFailed", "ArchiveMission", "RemoveMission" };
        Require(!((Harmony)SpGet(anima, "_harmony")!).GetPatchedMethods().Any(m => retiredHooks.Contains(m.Name)), "Anima retained a direct mission transition hook.");
        var registry = SpGet(anima, "PersistedRegistry")!;
        var assembly = anima.GetType().Assembly;
        var missionType = AccessTools.TypeByName("Source.MissionSystem.Mission");
        var storyType = AccessTools.TypeByName("Source.MissionSystem.StoryMission");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<MissionTransition>();
        var trace = new List<string>();
        using var subscription = ModApi.Missions!.Subscribe("qualification.anima", e =>
        {
            if (e.Mission.DefinitionId == null || !ids.Contains(e.Mission.DefinitionId)) return;
            events.Add(e); trace.Add(e.Sequence + "\t" + e.Kind + "\t" + e.Mission.InstanceId + "\t" + e.Mission.DefinitionId);
        });
        object Make(string name, params object[] args) => Activator.CreateInstance(assembly.GetType("VGAnima.Llm." + name, true)!, args)!;
        Array One(string name, object value)
        {
            var array = Array.CreateInstance(assembly.GetType("VGAnima.Llm." + name, true)!, 1);
            array.SetValue(value, 0); return array;
        }
        (string Id, object Mission) Create()
        {
            var station = SpStations().First(s => SpGet(s, "faction") != null && SpGet(s, "system") != null);
            var block = Make("LlmMissionBlock", "Anima disposable probe", "Gather one ore", "Probe complete",
                SpGet(SpGet(station, "faction")!, "identifier")!,
                One("LlmMissionStep", Make("LlmMissionStep", Make("GatherOreIntent", 1, "Gather one ore"))),
                One("LlmReward", Make("LlmCreditsReward", 1)));
            var story = Make("LlmStory", new[] { "Probe offer" }, new[] { "Probe status" }, new[] { "Probe thanks" }, block);
            var assigner = SpGet(anima, "MissionAssigner")!;
            var assign = assigner.GetType().GetMethods().Single(m => m.Name == "Assign" && m.GetParameters().Length == 8);
            var id = (string)assign.Invoke(assigner, new object?[] { block, 1, station, "qa-" + Guid.NewGuid().ToString("N"), story, "Probe", "Probe", null })!;
            ids.Add(id);
            var mission = AccessTools.Method(storyType, "Get", new[] { _player, typeof(string) }).Invoke(null, new[] { CurrentPlayer, (object)id })!;
            AccessTools.Field(missionType, "trackedOnHud").SetValue(mission, false);
            Require(SpCall(registry, "Get", id) != null, "Real Anima assigner did not publish its offered definition.");
            return (id, mission);
        }
        void Add(object mission, bool repeat = false) => CurrentPlayer.GetType().GetMethod("AddMissionWithLog", new[] { missionType, typeof(bool) })!.Invoke(CurrentPlayer, new[] { mission, (object)repeat });
        void Claim(object mission, bool force) => missionType.GetMethod("ClaimRewards")!.Invoke(mission, new object[] { force });
        void State(string id, string? expected)
        {
            var entry = SpCall(registry, "Get", id);
            Require(expected == null ? entry == null : entry != null && (string)SpGet(entry, "State")! == expected, "Anima definition state mismatch: " + expected);
        }
        string Sidecar(string name) => Path.Combine(_saveRoot!, name + ".save.vganima.json");
        try
        {
            var baseline = "[]";
            if (File.Exists(Sidecar("fixture-a")))
            {
                var stored = SpCall(SpGet(anima, "SidecarIO")!, "Read", Sidecar("fixture-a"));
                Require(SpGet(stored, "Status")!.ToString() == "Loaded", "Anima fixture sidecar is not readable.");
                baseline = SpJson(SpGet(SpGet(stored, "Schema")!, "Entries")!);
            }
            var forced = Create(); var offered = SpCall(registry, "Get", forced.Id);
            AccessTools.Field(missionType, "failed").SetValue(forced.Mission, true);
            Add(forced.Mission); Add(forced.Mission);
            Require(events.Count == 1 && events[0].Kind == MissionTransitionKind.Accepted, "Duplicate acceptance was emitted.");
            State(forced.Id, "accepted"); Claim(forced.Mission, false);
            Require(events.Count == 1, "Ineligible reward claim emitted an outcome."); State(forced.Id, "accepted");
            Claim(forced.Mission, true); State(forced.Id, null);
            Require(events.Select(e => e.Kind).SequenceEqual(new[] { MissionTransitionKind.Accepted, MissionTransitionKind.Completed, MissionTransitionKind.Archived }), "Forced/nested outcome mismatch.");
            var firstOccurrence = events[0].Mission.InstanceId;
            // Native repeated-definition fixture: reuse the owned blueprint with a fresh empty native occurrence.
            SpCall(registry, "Add", offered);
            var repeated = Activator.CreateInstance(missionType)!;
            AccessTools.Field(missionType, "storyId").SetValue(repeated, forced.Id);
            AccessTools.Field(missionType, "name").SetValue(repeated, "Anima repeated occurrence probe");
            AccessTools.Field(missionType, "dynamicLevel").SetValue(repeated, true);
            AccessTools.Field(missionType, "trackedOnHud").SetValue(repeated, false);
            Add(repeated, true); State(forced.Id, "accepted");
            Require(events.Last().Kind == MissionTransitionKind.Accepted && events.Last().Mission.InstanceId != firstOccurrence, "Repeated occurrence reused identity.");
            Claim(repeated, false); State(forced.Id, null);

            var replacement = Create(); var failed = Create();
            AccessTools.Field(missionType, "nextMissionOnFailed").SetValue(failed.Mission, replacement.Id);
            events.Clear(); Add(failed.Mission);
            missionType.GetMethod("MissionFailed")!.Invoke(failed.Mission, new object[] { "Disposable provider probe" });
            Require(events.Select(e => e.Kind).SequenceEqual(new[] { MissionTransitionKind.Accepted, MissionTransitionKind.Failed, MissionTransitionKind.Removed, MissionTransitionKind.Accepted }), "Failure/replacement ordering mismatch.");
            State(failed.Id, null); State(replacement.Id, "accepted");
            CurrentPlayer.GetType().GetMethod("RemoveMission", new[] { missionType, typeof(bool) })!.Invoke(CurrentPlayer, new[] { replacement.Mission, (object)false });
            State(replacement.Id, null);

            var held = Create(); Add(held.Mission); State(held.Id, "accepted");
            var savedEntry = SpJson(SpCall(registry, "Get", held.Id));
            var session = ModApi.Current!.CurrentSession!.Id;
            Save("qa-anima-held", LifecycleEventKind.SaveSucceeded);
            Require(File.Exists(Sidecar("qa-anima-held")), "Anima sidecar missing.");
            foreach (var name in new[] { "qa-anima-held", "qa-anima-held" })
            {
                events.Clear(); foreach (var frame in SpLoad(name)) yield return frame;
                Require(events.Count == 1 && events[0].Kind == MissionTransitionKind.Restored, "Reload fabricated acceptance or lost restoration.");
                Require(SpJson(SpCall(registry, "Get", held.Id)) == savedEntry, "Anima saved definition changed on reload.");
            }
            Require(!(bool)SpCall(anima, "CanPublishFor", (Guid?)session), "Old-session publication remained allowed.");
            Save("qa-anima-copy", LifecycleEventKind.SaveSucceeded);
            events.Clear(); foreach (var frame in SpLoad("qa-anima-copy")) yield return frame;
            Require(events.Count == 1 && events[0].Kind == MissionTransitionKind.Restored && SpJson(SpCall(registry, "Get", held.Id)) == savedEntry, "Save-as restoration mismatch.");

            // Explicit malformed-callback fault injection, not a claim that the API produces malformed events.
            var observer = SpGet(anima, "_missionObserver")!;
            SpCall(observer, "Receive", new object[] { null! });
            Require((bool)SpGet(observer, "Faulted")! && !(bool)SpGet(anima, "_active")!, "Observer fault failed to stop authoring.");
            Require(!((Harmony)SpGet(anima, "_harmony")!).GetPatchedMethods().Any(), "Stopped authoring hooks remain.");
            Require(((Harmony)SpGet(anima, "_loadSafetyHarmony")!).GetPatchedMethods().Count() == 2, "Load safeguards did not survive stop.");
            var frozen = File.ReadAllBytes(Sidecar("qa-anima-copy"));
            // Diverge memory so an accidental identical-byte rewrite cannot look like refusal.
            SpCall(registry, "Clear");
            Save("qa-anima-copy", LifecycleEventKind.SaveSucceeded);
            SpCall(anima, "OnAppQuitting");
            Require(File.ReadAllBytes(Sidecar("qa-anima-copy")).SequenceEqual(frozen), "Stopped provider published a sidecar.");
            Save("qa-anima-stopped", LifecycleEventKind.SaveSucceeded);
            Require(!File.Exists(Sidecar("qa-anima-stopped")), "Stopped provider wrote a new sidecar.");
            foreach (var frame in SpLoad("fixture-a")) yield return frame;
            Require(SpJson(SpCall(registry, "All")) == baseline && !((IDictionary)SpGet(storyType, "allMissions")!).Contains(held.Id), "Stopped next-slot load leaked restored definitions.");
            var jsonType = AccessTools.TypeByName("LightJson.JsonValue");
            var missingId = "vganima_llm_missing-" + Guid.NewGuid().ToString("N");
            var missing = Activator.CreateInstance(jsonType, missingId)!;
            var placeholder = missionType.GetMethod("FromJson")!.Invoke(null, new[] { missing });
            Require(placeholder != null && (string)SpGet(placeholder, "storyId")! == missingId, "Stopped string-path lookup lost identity-preserving placeholder.");
            File.WriteAllText(Path.Combine(_root!, "anima-missions.txt"), "PASS\nReal provider construction; duplicate/ineligible/forced/repeated/failure-replacement outcomes; repeated load/save-as; explicit observer fault; retained load/lookup safeguards; stopped sidecar publication refused. No LLM backend or full authoring-intent qualification.");
            Passed("native-anima-api-missions");
        }
        finally
        {
            File.WriteAllLines(Path.Combine(_root!, "anima-mission-events.tsv"), trace);
            foreach (var id in ids) ((IDictionary)SpGet(storyType, "allMissions")!).Remove(id);
        }
    }
}
