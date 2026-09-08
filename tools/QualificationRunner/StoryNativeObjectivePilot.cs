using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckNativeObjectiveObservation(object campaign, string faction)
    {
        const string local = "observed-x";
        var target = (string)SpGet(SpGet(CurrentPlayer, "currentPointOfInterest")!, "guid")!;
        var registration = (StoryRegistrationResult)campaign.GetType().GetMethod("RegisterObservedObjectives")!.Invoke(campaign, new object[] { faction, target })!;
        Require(registration.Succeeded, "Observed native objectives could not register.");
        var provider = (IStoryProvider)campaign.GetType().GetProperty("Provider")!.GetValue(campaign)!;
        var queries = (IStoryObjectiveProvider)provider;
        var session = _api!.CurrentSession!.Id;
        var occurrence = provider.Offer(session, local);
        Require(occurrence.Accepted, "Observed native occurrence could not be offered.");
        var credits = new StoryObjectiveId(new StoryContentId(provider.ProviderId, local), occurrence.OccurrenceId, "credits");
        var visit = new StoryObjectiveId(credits.Definition, occurrence.OccurrenceId, "visit");
        Require(queries.Query(session, credits).Knowledge == StoryKnowledge.Unavailable, "Offered native objective invented progress.");
        Require(provider.Activate(session, occurrence.OccurrenceId).Accepted, "Observed native occurrence could not activate.");
        AssertNativeObjectiveObservation(provider, queries, occurrence.OccurrenceId, credits, visit);
        long beforeDeniedWrite = Convert.ToInt64(SpGet(CurrentPlayer, "credits"));
        Require(!queries.SetProgress(session, credits, StoryObjective.MaxAmount).Accepted
            && Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == beforeDeniedWrite, "Native credit objective accepted a scripted resource write.");
        var previous = HeldStory(provider, occurrence.OccurrenceId, local);
        var coldOffered = provider.Offer(session, local);
        Require(coldOffered.Accepted, "Cold-start donor could not retain an offered occurrence.");
        Save("qa-story-definition-cold", LifecycleEventKind.SaveSucceeded);
        WriteAtomic("story-definition-donor.txt", new[] { "PASS", provider.ProviderId, faction, target,
            occurrence.OccurrenceId.ToString("D"), coldOffered.OccurrenceId.ToString("D") });
        Require(provider.Retire(session, coldOffered.OccurrenceId, StoryOutcome.Abandoned).Accepted,
            "Cold-start donor offered fixture could not retire normally.");
        Save("qa-native-objective-observation", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-native-objective-observation")) yield return frame;
        Require(!ReferenceEquals(previous, HeldStory(provider, occurrence.OccurrenceId, local)), "Native observation reused a pre-load object.");
        Require(queries.Query(session, credits).Knowledge == StoryKnowledge.Unavailable, "Stale native observation session was accepted.");
        AssertNativeObjectiveObservation(provider, queries, occurrence.OccurrenceId, credits, visit);
        session = _api.CurrentSession!.Id;
        Require(provider.Retire(session, occurrence.OccurrenceId, StoryOutcome.Abandoned).Accepted, "Observed fixture could not be removed normally.");
        Require(queries.Query(session, credits).Knowledge == StoryKnowledge.Unavailable, "Retired native objective invented a live resource answer.");
        WriteAtomic("story-native-objectives.txt", new[] { "PASS", "credit-resource;travel-completion;read-only;reload;stale-session;no-scripted-write;no-invented-progress" });
    }

    private void AssertNativeObjectiveObservation(IStoryProvider provider, IStoryObjectiveProvider queries, Guid occurrence,
        StoryObjectiveId creditId, StoryObjectiveId visitId)
    {
        var mission = HeldStory(provider, occurrence, "observed-x");
        var serialized = mission.GetType().GetMethod("ToJson")!.Invoke(mission, null)!.ToString();
        long balance = Convert.ToInt64(SpGet(CurrentPlayer, "credits"));
        var steps = (IList)SpGet(mission, "steps")!;
        var travel = ((IList)SpGet(steps[1]!, "objectives")!)[0]!;
        var expectedVisit = (bool)travel.GetType().GetMethod("IsComplete")!.Invoke(travel, null)! ? 1 : 0;
        var session = _api!.CurrentSession!.Id;
        var credit = queries.Query(session, creditId);
        var visit = queries.Query(session, visitId);
        Require(credit.Knowledge == StoryKnowledge.Known && credit.Progress == Math.Min(StoryObjective.MaxAmount, Math.Max(0L, balance))
            && credit.Required == StoryObjective.MaxAmount, "Credit observation differs from current vanilla resources.");
        Require(visit.Knowledge == StoryKnowledge.Known && visit.Progress == expectedVisit && visit.Required == 1,
            "Travel observation differs from native completion.");
        Require(Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == balance
            && serialized == mission.GetType().GetMethod("ToJson")!.Invoke(mission, null)!.ToString(), "Read-only objective queries changed native state.");
    }
}
