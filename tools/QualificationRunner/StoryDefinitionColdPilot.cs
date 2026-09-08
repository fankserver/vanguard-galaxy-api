using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckColdStoryDefinitions(object campaign)
    {
        var donor = File.ReadAllLines(Path.Combine(_root!, "story-definition-donor.txt"));
        Require(donor.Length == 6 && donor[0] == "PASS", "Cold-start donor receipt is malformed.");
        var registration = (StoryRegistrationResult)campaign.GetType().GetMethod("RegisterChangedObservedObjectives")!
            .Invoke(campaign, new object[] { donor[2] })!;
        Require(registration.Succeeded, "Changed startup definition did not register.");
        var provider = (IStoryProvider)campaign.GetType().GetProperty("Provider")!.GetValue(campaign)!;
        Require(provider.ProviderId == donor[1], "Cold-start author identity changed.");
        var active = Guid.Parse(donor[4]); var offered = Guid.Parse(donor[5]);
        Require(active != offered, "Cold-start donor identities coincide.");
        var queries = (IStoryObjectiveProvider)provider;
        foreach (var frame in StoryLoadReady("qa-story-definition-cold")) yield return frame;
        var before = provider.Unresolved("observed-x");
        Require(before.Knowledge == StoryKnowledge.Known && before.Occurrences.Count == 2
            && before.Occurrences.Any(row => row.OccurrenceId == active && row.Stage == StoryOccurrenceStage.Active)
            && before.Occurrences.Any(row => row.OccurrenceId == offered && row.Stage == StoryOccurrenceStage.Offered),
            "Cold-start did not recover the exact offered and active occurrences.");
        AssertColdStoryDefinition(provider, queries, active, donor[3]);
        var session = _api!.CurrentSession!.Id;
        Require(provider.Activate(session, offered).Accepted, "Retained offered definition could not activate despite its valid saved target.");
        AssertColdStoryDefinition(provider, queries, offered, donor[3]);
        Save("qa-story-definition-restored", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-definition-restored")) yield return frame;
        AssertColdStoryDefinition(provider, queries, active, donor[3]);
        AssertColdStoryDefinition(provider, queries, offered, donor[3]);
        session = _api.CurrentSession!.Id;
        Require(provider.Retire(session, active, StoryOutcome.Abandoned).Accepted
            && provider.Retire(session, offered, StoryOutcome.Abandoned).Accepted, "Cold-start fixtures did not retire normally.");
        WriteAtomic("story-definition-cold.txt", new[] { "PASS", "changed-startup;active;offered;retained-target;retained-amount;reload;normal-retirement" });
        Passed("owned-story-definition-cold-start");
    }

    private void AssertColdStoryDefinition(IStoryProvider provider, IStoryObjectiveProvider queries, Guid occurrence, string target)
    {
        var id = new StoryContentId(provider.ProviderId, "observed-x");
        AssertNativeObjectiveObservation(provider, queries, occurrence,
            new StoryObjectiveId(id, occurrence, "credits"), new StoryObjectiveId(id, occurrence, "visit"));
        var mission = HeldStory(provider, occurrence, "observed-x");
        var steps = (IList)SpGet(mission, "steps")!;
        var travel = ((IList)SpGet(steps[1]!, "objectives")!)[0]!;
        Require((string)SpGet(travel, "targetPOI")! == target, "Startup replacement overwrote the saved travel target.");
    }
}
