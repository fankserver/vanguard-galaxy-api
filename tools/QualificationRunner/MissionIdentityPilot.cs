using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckMissionIdentity()
    {
        if (!File.Exists(Path.Combine(_root!, "mission-identity.enabled"))) yield break;
        Require(_api!.Capabilities.Any(c => c.Name == "mission-continuity" && c.Available), "Mission continuity unavailable.");
        var api = ModApi.Missions!; var events = new List<MissionTransition>();
        string prefix = "VGModAPI identity " + Guid.NewGuid().ToString("N");
        using var subscription = api.Subscribe("qualification.identity", e => { if (e.Mission.Name.StartsWith(prefix, StringComparison.Ordinal)) events.Add(e); });
        var type = AccessTools.TypeByName("Source.MissionSystem.Mission");
        var player = CurrentPlayer;
        foreach (string suffix in new[] { " unique", " duplicate", " duplicate" })
        {
            var mission = Activator.CreateInstance(type)!;
            AccessTools.Field(type, "name").SetValue(mission, prefix + suffix);
            AccessTools.Field(type, "dynamicLevel").SetValue(mission, true);
            AccessTools.Field(type, "trackedOnHud").SetValue(mission, false);
            AccessTools.Field(type, "sourcePoi").SetValue(mission, SpGet(player, "currentPointOfInterest"));
            var faction = AccessTools.Field(type, "sourceFaction");
            faction.SetValue(mission, SpGet(faction.FieldType, "player"));
            player.GetType().GetMethod("AddMissionWithLog", new[] { type, typeof(bool) })!.Invoke(player, new[] { mission, (object)false });
        }
        var accepted = events.Where(e => e.Kind == MissionTransitionKind.Accepted).ToArray();
        Require(accepted.Length == 3, "Identity fixtures were not accepted.");
        var unique = accepted.Single(e => e.Mission.Name.EndsWith(" unique", StringComparison.Ordinal)).Mission.InstanceId;
        var ambiguousIds = new HashSet<Guid>(accepted.Where(e => e.Mission.Name.EndsWith(" duplicate", StringComparison.Ordinal)).Select(e => e.Mission.InstanceId));
        CheckJournalIdentityState(unique, "Accepted");
        Save("qa-mission-identity", LifecycleEventKind.SaveSucceeded);
        foreach (string name in new[] { "qa-mission-identity", "qa-mission-identity" })
        {
            events.Clear();
            foreach (var frame in SpLoad(name)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var restored = events.Where(e => e.Kind == MissionTransitionKind.Restored).ToArray();
            Require(restored.Length == 3 && events.All(e => e.Kind == MissionTransitionKind.Restored), "Load fabricated acceptance or lost identity fixtures.");
            var matched = restored.Single(e => e.Mission.Name.EndsWith(" unique", StringComparison.Ordinal)).Mission;
            Require(matched.InstanceId == unique && matched.IdentityEvidence == MissionIdentityEvidence.SavedSnapshotMatch && !matched.AcceptanceObserved, "Exact saved identity did not restore without fabricated acceptance.");
            var ambiguous = restored.Where(e => e.Mission.Name.EndsWith(" duplicate", StringComparison.Ordinal)).ToArray();
            Require(ambiguous.All(e => e.Mission.IdentityEvidence == MissionIdentityEvidence.MissingOrAmbiguous && !ambiguousIds.Contains(e.Mission.InstanceId)) && ambiguous.Select(e => e.Mission.InstanceId).Distinct().Count() == 2, "Indistinguishable missions were assigned guessed identities.");
            ambiguousIds = new HashSet<Guid>(ambiguous.Select(e => e.Mission.InstanceId));
            CheckJournalIdentityState(unique, "Accepted");
        }
        var live = ((System.Collections.IEnumerable)SpGet(CurrentPlayer, "missions")!).Cast<object>().Single(m => (string)SpGet(m, "name")! == prefix + " unique");
        type.GetMethod("MissionFailed", new[] { typeof(string) })!.Invoke(live, new object[] { "VGModAPI identity rollback probe" });
        Require((bool)SpGet(live, "failed")!, "Rollback probe did not advance native failure state.");
        Save("qa-mission-identity-advanced", LifecycleEventKind.SaveSucceeded); events.Clear();
        foreach (var frame in SpLoad("qa-mission-identity-advanced")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        live = ((System.Collections.IEnumerable)SpGet(CurrentPlayer, "missions")!).Cast<object>().Single(m => (string)SpGet(m, "name")! == prefix + " unique");
        Require((bool)SpGet(live, "failed")! && events.Any(e => e.Mission.InstanceId == unique && e.Mission.IdentityEvidence == MissionIdentityEvidence.SavedSnapshotMatch), "Advanced snapshot lost state or identity.");
        CheckJournalIdentityState(unique, "Failed");
        events.Clear();
        foreach (var frame in SpLoad("qa-mission-identity")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        live = ((System.Collections.IEnumerable)SpGet(CurrentPlayer, "missions")!).Cast<object>().Single(m => (string)SpGet(m, "name")! == prefix + " unique");
        Require(!(bool)SpGet(live, "failed")! && events.Any(e => e.Mission.InstanceId == unique && e.Mission.IdentityEvidence == MissionIdentityEvidence.SavedSnapshotMatch), "Rollback did not restore earlier state and identity.");
        CheckJournalIdentityState(unique, "Accepted");
        Save("qa-mission-identity-copy", LifecycleEventKind.SaveSucceeded); events.Clear();
        foreach (var frame in SpLoad("qa-mission-identity-copy")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Require(events.Any(e => e.Mission.InstanceId == unique && e.Mission.IdentityEvidence == MissionIdentityEvidence.SavedSnapshotMatch), "Save-as lost verified identity.");
        CheckJournalIdentityState(unique, "Accepted");
        FinishJournalMissionEvents();
        foreach (var frame in SpLoad("fixture-a")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        CheckJournalLoad("fixture-a");
        File.WriteAllText(Path.Combine(_root!, "mission-identity.txt"), "PASS\nExact snapshot identity survived two loads, failure-state advancement/rollback and save-as; ambiguous duplicate records refused; no acceptance fabricated.");
        Passed("native-mission-identity-roundtrip");
    }
}
