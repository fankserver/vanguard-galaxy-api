using System;
using System.Collections.Generic;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckOwnedObjectives(object campaign, object job, string faction)
    {
        const string local = "objective-x";
        var registered = (StoryRegistrationResult)campaign.GetType().GetMethod("RegisterObjectives")!.Invoke(campaign, new object[] { faction, 1 })!;
        var generated = (StoryRegistrationResult)job.GetType().GetMethod("RegisterObjectives")!.Invoke(job, new object[] { faction, "Collect two field reports", 2 })!;
        Require(registered.Succeeded && generated.Succeeded, "Objective authors could not register.");
        var a = (IStoryProvider)campaign.GetType().GetProperty("Provider")!.GetValue(campaign)!;
        var b = (IStoryProvider)job.GetType().GetProperty("Provider")!.GetValue(job)!;
        var qa = (IStoryObjectiveProvider)a;
        var qb = (IStoryObjectiveProvider)b;
        var session = _api!.CurrentSession!.Id;
        var first = a.Offer(session, local);
        var second = b.Offer(session, local);
        Require(first.Accepted && second.Accepted && a.Activate(session, first.OccurrenceId).Accepted && b.Activate(session, second.OccurrenceId).Accepted,
            "Objective occurrences could not activate.");
        var talk = new StoryObjectiveId(new StoryContentId(a.ProviderId, local), first.OccurrenceId, "talk");
        var report = new StoryObjectiveId(new StoryContentId(b.ProviderId, local), second.OccurrenceId, "talk");
        Require(!qb.SetProgress(session, talk, 1).Accepted, "Cross-provider objective write succeeded.");
        Require(qa.SetProgress(session, talk, 1).Accepted && qb.SetProgress(session, report, 1).Accepted, "Partial progress failed.");
        var inactive = (StoryTransitionResult)campaign.GetType().GetMethod("AnswerWitness")!.Invoke(campaign, new object[] { session, first.OccurrenceId })!;
        Require(!inactive.Accepted, "Inactive conversation beat advanced.");
        var oldMission = HeldStory(a, first.OccurrenceId, local);
        Save("qa-objective-partial", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-objective-partial")) yield return frame;
        Require(!ReferenceEquals(oldMission, HeldStory(a, first.OccurrenceId, local)), "Objective reload retained the pre-load mission object.");
        Require(!qa.SetProgress(session, talk, 3).Accepted, "A stale session advanced the reloaded objective.");
        session = _api.CurrentSession!.Id;
        Require(qa.Query(session, talk).Progress == 1 && qb.Query(session, report).Progress == 1, "Partial progress did not restore.");
        registered.Registration!.Dispose();
        var revised = (StoryRegistrationResult)campaign.GetType().GetMethod("RegisterObjectives")!.Invoke(campaign, new object[] { faction, 2 })!;
        Require(revised.Succeeded, "Revised authored definition could not register.");
        foreach (var frame in StoryLoadReady("qa-objective-partial")) yield return frame;
        session = _api.CurrentSession!.Id;
        Require(qa.Query(session, talk).ContentRevision == 2 && qa.Query(session, talk).Progress == 1,
            "Revision migration lost partial progress or revision identity.");
        Require(!qa.SetProgress(session, talk, 3).Accepted, "Reordered inactive objective advanced before the preceding beat.");
        var answer = (StoryTransitionResult)campaign.GetType().GetMethod("AnswerWitness")!.Invoke(campaign, new object[] { session, first.OccurrenceId })!;
        Require(answer.Accepted, "Authored answer did not complete its live objective.");
        Require(qa.SetProgress(session, talk, 3).Accepted, "Current reordered authored objective did not complete.");
        var done = (StoryTransitionResult)job.GetType().GetMethod("ReportProgress")!.Invoke(job, new object[] { session, second.OccurrenceId, 2 })!;
        Require(done.Accepted && qb.SetProgress(session, report, 2).Accepted, "Generated objective or duplicate completion failed.");
        foreach (var frame in ClaimStory(a, first.OccurrenceId, 7, local)) yield return frame;
        foreach (var frame in ClaimStory(b, second.OccurrenceId, 3, local)) yield return frame;
        var repeated = b.Offer(session, local);
        Require(repeated.Accepted && repeated.OccurrenceId != second.OccurrenceId && b.Activate(session, repeated.OccurrenceId).Accepted,
            "Repeated generated objective did not receive an independent occurrence.");
        var repeatKey = new StoryObjectiveId(new StoryContentId(b.ProviderId, local), repeated.OccurrenceId, "talk");
        Require(qb.SetProgress(session, repeatKey, 1).Accepted && qb.Query(session, report).Progress == 2,
            "Repeated instance changed the completed instance's progress.");
        Require(qb.SetProgress(session, repeatKey, 2).Accepted, "Repeated generated objective could not complete.");
        foreach (var frame in ClaimStory(b, repeated.OccurrenceId, 3, local)) yield return frame;
        Save("qa-objective-completed", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-objective-partial")) yield return frame;
        session = _api.CurrentSession!.Id;
        Require(qa.Query(session, talk).Progress == 1 && qa.Query(session, talk).ContentRevision == 2
            && qb.Query(session, report).Progress == 1, "Older objective save inherited newer completion or failed revision migration.");
        foreach (var frame in StoryLoadReady("qa-objective-completed")) yield return frame;
        session = _api.CurrentSession!.Id;
        var restoredCampaign = qa.Query(session, talk);
        var restoredJob = qb.Query(session, report);
        var restoredRepeat = qb.Query(session, repeatKey);
        Require(a.IsCompleted(local).Completed == true && restoredCampaign.Knowledge == StoryKnowledge.Known
            && restoredCampaign.ContentRevision == 2 && restoredCampaign.Progress == 3 && restoredCampaign.Outcome == StoryOutcome.Completed,
            "Completed campaign objective state did not restore.");
        Require(restoredJob.Knowledge == StoryKnowledge.Known && restoredRepeat.Knowledge == StoryKnowledge.Known
            && restoredJob.Progress == 2 && restoredRepeat.Progress == 2
            && restoredJob.ContentRevision == 1 && restoredRepeat.ContentRevision == 1
            && restoredJob.Outcome == StoryOutcome.Completed && restoredRepeat.Outcome == StoryOutcome.Completed,
            "Completed generated objective occurrences did not independently restore.");
        WriteAtomic("story-objectives.txt", new[] { "PASS", "owners;partial-reload;stale-session;inactive-step;authored-beat;generated-objective;duplicate;native-claim;revision-reorder;repeated-instance;rollback" });
    }
}
