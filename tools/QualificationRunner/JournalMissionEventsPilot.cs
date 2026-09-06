using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private bool JournalMissionEvents => File.Exists(Path.Combine(_root!, "journal-mission-events.enabled"));
    private int _journalProjectionChecks;
    private static object JournalOccurrence(Guid id)
    {
        var facade = JournalFacade(); Require(facade != null, "Journal facade unavailable during mission verification.");
        var records = (IEnumerable)facade!.GetType().GetMethod("GetAllMissions")!.Invoke(facade, null)!;
        return records.Cast<object>().Single(r => (string)SpGet(r, "MissionInstanceId")! == id.ToString());
    }
    private static string[] JournalStates(object record) => ((IEnumerable)SpGet(record, "Timeline")!).Cast<object>().Select(e => SpGet(e, "State")!.ToString()!).ToArray();
    private void CheckJournalEventProjection(IEnumerable<MissionTransition> transitions)
    {
        if (!JournalMissionEvents) return;
        var plugin = Chainloader.PluginInfos["vgmissionjournal"].Instance;
        Require(SpGet(plugin, "_missionObserver") != null, "API journal observer is not selected.");
        var harmony = (HarmonyLib.Harmony)SpGet(plugin, "_harmony")!;
        Require(!harmony.GetPatchedMethods().Any(), "API journal mode retained direct mission patches.");
        var groups = transitions.GroupBy(e => e.Mission.InstanceId).ToArray(); Require(groups.Length > 0, "No journal projection fixtures.");
        foreach (var group in groups)
        {
            var expected = new List<string>();
            foreach (var e in group)
            {
                if (e.Kind == MissionTransitionKind.Accepted && expected.Count == 0) expected.Add("Accepted");
                else if (expected.Count == 1 && e.Kind is MissionTransitionKind.Completed or MissionTransitionKind.Failed or MissionTransitionKind.Abandoned or MissionTransitionKind.Removed) expected.Add(e.Kind.ToString());
            }
            var record = JournalOccurrence(group.Key);
            Require(JournalStates(record).SequenceEqual(expected), "Journal timeline diverged from witnessed API outcomes.");
            if (expected.Last() == "Removed") Require(SpGet(record, "Outcome") == null && !(bool)SpGet(record, "IsActive")!, "Neutral removal was misclassified.");
        }
        _journalProjectionChecks++;
    }
    private void CheckJournalIdentityState(Guid id, string terminal)
    {
        if (!JournalMissionEvents) return;
        var expected = terminal == "Accepted" ? new[] { "Accepted" } : new[] { "Accepted", terminal };
        Require(JournalStates(JournalOccurrence(id)).SequenceEqual(expected), "Saved journal acceptance/outcome history did not follow verified identity and rollback.");
    }
    private void FinishJournalMissionEvents()
    {
        if (!JournalMissionEvents) return;
        Require(_journalProjectionChecks == 3, "Not all native journal transition groups ran.");
        File.WriteAllText(Path.Combine(_root!, "journal-mission-events.txt"), "PASS\nDirect hooks absent; repeated/forced/eligible/archive/failure/replacement/neutral removal histories match API; accepted history survives repeated loads/save-as and rolls back failure with identity.");
        Passed("native-journal-api-mission-events");
    }
}
