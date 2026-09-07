using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private readonly List<string> _storyCases = new();
    private void StoryCase(string name)
    {
        _storyCases.Add(name);
        WriteAtomic("story-cases.txt", _storyCases);
    }

    private IEnumerable<object?> CheckOwnedStories()
    {
        if (!File.Exists(Path.Combine(_root!, "story.enabled"))) yield break;
        Require(ModApi.Story != null, "Story module unavailable.");
        foreach (var frame in Wait(NativeTravelReady, "initial story world ready")) yield return frame;
        Require(!Chainloader.PluginInfos.ContainsKey("vgmissionjournal"), "Story campaign must be demonstrated without MissionJournal.");
        var campaign = Chainloader.PluginInfos["vg-story-campaign"].Instance;
        var job = Chainloader.PluginInfos["vg-story-job"].Instance;
        Require(campaign.GetType().Assembly != job.GetType().Assembly, "Authors must be independently loaded assemblies.");
        var poi = SpGet(CurrentPlayer, "currentPointOfInterest");
        Require(poi != null, "Story demonstration requires a real current POI.");
        var target = (string)SpGet(poi!, "guid")!;
        var faction = (string)SpGet(SpGet(NativeType("Source.Galaxy.Faction"), "player")!, "identifier")!;
        var campaignResult = (StoryRegistrationResult)campaign.GetType().GetMethod("Register")!.Invoke(campaign, new object[] { target, faction })!;
        var jobResult = (StoryRegistrationResult)job.GetType().GetMethod("Register")!.Invoke(job, new object[] { "Generated courier job",
            "Deliver the generated dispatch to the selected rendezvous.", target, faction, 11 })!;
        Require(campaignResult.Succeeded && jobResult.Succeeded, "Both actual authors must register: " + campaignResult.Diagnostic + "; " + jobResult.Diagnostic);
        var a = (IStoryProvider)campaign.GetType().GetProperty("Provider")!.GetValue(campaign)!;
        var b = (IStoryProvider)job.GetType().GetProperty("Provider")!.GetValue(job)!;
        Require(a.ProviderId != b.ProviderId && campaignResult.Registration!.Id.LocalId == jobResult.Registration!.Id.LocalId,
            "Same local ID must resolve to independent owners.");
        var duplicate = (StoryRegistrationResult)campaign.GetType().GetMethod("Register")!.Invoke(campaign, new object[] { target, faction })!;
        Require(duplicate.Status == StoryRegistrationStatus.DuplicateLocalId, "Duplicate registration was not diagnosed.");
        Require(a.Unresolved("mission-x").Knowledge == StoryKnowledge.Known && a.Unresolved("mission-x").Occurrences.Count == 0,
            "New-game occurrences leaked into the pre-existing fixture slot.");
        StoryCase("independent-authors");
        var session = _api!.CurrentSession!.Id;
        var offered = a.Offer(session, "mission-x");
        var generated = b.Offer(session, "mission-x");
        Require(offered.Accepted && generated.Accepted && offered.OccurrenceId != generated.OccurrenceId, "Independent offers failed.");
        Require(!b.Withdraw(session, offered.OccurrenceId).Accepted, "A foreign author withdrew campaign content.");
        Save("qa-story-offered", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-offered")) yield return frame;
        Require(a.Unresolved("mission-x").Occurrences.Any(x => x.OccurrenceId == offered.OccurrenceId && x.Stage == StoryOccurrenceStage.Offered)
            && b.Unresolved("mission-x").Occurrences.Any(x => x.OccurrenceId == generated.OccurrenceId), "Offered definitions did not reconstruct automatically.");
        Require(a.Activate(session, offered.OccurrenceId).Status == StoryTransitionStatus.StaleSession, "Pre-load callback crossed session boundary.");
        StoryCase("offered-roundtrip");
        session = _api.CurrentSession!.Id;
        Require(a.DeclareChoices(session, offered.OccurrenceId, new Dictionary<string, string> { ["witness"] = "protected" }).Accepted,
            "Campaign decision staging failed.");
        Require(a.Activate(session, offered.OccurrenceId).Accepted && b.Activate(session, generated.OccurrenceId).Accepted, "Native acceptance failed.");
        Save("qa-story-active", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-active")) yield return frame;
        Require(a.Unresolved("mission-x").Occurrences.Any(x => x.OccurrenceId == offered.OccurrenceId && x.Stage == StoryOccurrenceStage.Active)
            && b.Unresolved("mission-x").Occurrences.Any(x => x.OccurrenceId == generated.OccurrenceId && x.Stage == StoryOccurrenceStage.Active),
            "Active occurrences failed native save/load reconstruction.");
        StoryCase("active-roundtrip");
        foreach (var frame in ClaimStory(a, offered.OccurrenceId, 17)) yield return frame;
        foreach (var frame in ClaimStory(b, generated.OccurrenceId, 11)) yield return frame;
        Require(a.IsCompleted("mission-x").Completed == true, "Campaign completion is not authoritative without a journal.");
        var outcome = a.Occurrences("mission-x").Records.Single(x => x.OccurrenceId == offered.OccurrenceId);
        Require(outcome.Outcome == StoryOutcome.Completed && outcome.Choices["witness"] == "protected", "Persisted pending choice was lost on native completion.");
        Require(b.IsCompleted("mission-x").Completed == false, "Temporary tombstone became campaign completion.");
        StoryCase("native-completion");
        var player = CurrentPlayer;
        var ephemeral = player.GetType().GetField("isEphemeral")!;
        bool wasEphemeral = (bool)ephemeral.GetValue(player)!;
        try
        {
            ephemeral.SetValue(player, true);
            Save("qa-story-skipped", LifecycleEventKind.SaveSkipped);
        }
        finally { ephemeral.SetValue(player, wasEphemeral); }
        Require(!File.Exists(Path.Combine(_saveRoot!, "qa-story-skipped.save")), "Skipped story save wrote a file.");
        var blockedMetadata = Path.Combine(_saveRoot!, "qa-story-failed.meta");
        Directory.CreateDirectory(blockedMetadata);
        try { Save("qa-story-failed", LifecycleEventKind.SaveFailed); }
        finally { Directory.Delete(blockedMetadata); }
        Require(a.IsCompleted("mission-x").Completed == true, "Failed save corrupted live campaign state.");
        StoryCase("save-refusals");
        Save("qa-story-completed", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-offered")) yield return frame;
        Require(a.IsCompleted("mission-x").Completed == false && a.Occurrences("mission-x").Records.Count == 0,
            "Newer completion leaked into the older save.");
        Require(a.Unresolved("mission-x").Occurrences.Single().Stage == StoryOccurrenceStage.Offered, "Rollback did not restore the offered stage.");
        StoryCase("older-save-rollback");
        foreach (var frame in StoryLoadReady("qa-story-completed")) yield return frame;
        Require(a.IsCompleted("mission-x").Completed == true, "Cross-slot return lost the saved completion.");
        session = _api.CurrentSession!.Id;
        StoryCase("cross-slot-return");
        var repeat = b.Offer(session, "mission-x");
        Require(repeat.Accepted && repeat.OccurrenceId != generated.OccurrenceId && b.Activate(session, repeat.OccurrenceId).Accepted,
            "Archived definition blocked a distinct repeated job.");
        foreach (var frame in ClaimStory(b, repeat.OccurrenceId, 11)) yield return frame;
        StoryCase("repeat-job");
        foreach (var frame in CheckOwnedObjectives(campaign, job, faction)) yield return frame;
        foreach (var frame in StoryLoadReady("qa-story-active")) yield return frame;
        Invoke(campaign, "ReleaseProvider");
        Save("qa-story-provider-unregistered", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-provider-unregistered")) yield return frame;
        var held = HeldStory(a, offered.OccurrenceId);
        var serialized = held.GetType().GetMethod("ToJson")!.Invoke(held, null)!.ToString();
        var balance = Convert.ToInt64(SpGet(CurrentPlayer, "credits"));
        Invoke(held, "Update", 1f);
        CompleteStoryNative(held, true);
        Invoke(held, "ClaimRewards", true);
        Require(Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == balance, "Unregistered provider's held mission paid rewards.");
        Require(ReferenceEquals(held, HeldStory(a, offered.OccurrenceId)), "Quarantine deleted the held native mission.");
        Require(serialized == held.GetType().GetMethod("ToJson")!.Invoke(held, null)!.ToString(), "Quarantine changed native serialized mission state.");
        StoryCase("provider-unregistered-first-reload");
        Save("qa-story-provider-unregistered", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-provider-unregistered")) yield return frame;
        held = HeldStory(a, offered.OccurrenceId);
        balance = Convert.ToInt64(SpGet(CurrentPlayer, "credits"));
        CompleteStoryNative(held, true);
        Require(Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == balance, "Reload released orphan payout protection.");
        Require(!a.Active && a.IsCompleted("mission-x").Knowledge == StoryKnowledge.Unavailable, "Disposed author lease answered current completion.");
        StoryCase("provider-unregistered-second-reload");
        Require(StoryReceipt.Evaluate(_storyCases) == null, "Incomplete story receipt.");
        WriteAtomic("story-result.txt", new[] { "PASS", StoryReceipt.Phase });
        Passed(StoryReceipt.Phase);
    }

    private IEnumerable<object?> StoryLoadReady(string name)
    {
        foreach (var frame in LoadReady(name)) yield return frame;
        // GameplayInitialized precedes native POI initialization. Starting another load or save
        // there raced the old manager's initialization coroutine in qa97.
        foreach (var frame in Wait(NativeTravelReady, "story world ready after " + name)) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        Require(NativeTravelReady(), "Story world lost readiness before the next operation.");
    }

    private void CompleteStoryNative(object mission, bool force)
    {
        var method = StoryNativeCalls.CompleteMission(CurrentPlayer.GetType(), NativeType("Source.MissionSystem.Mission"));
        method.Invoke(CurrentPlayer, new object[] { mission, force });
    }

    private object HeldStory(IStoryProvider provider, Guid occurrence, string localId = "mission-x")
    {
        var identifier = "vgmodapi.story." + provider.ProviderId + "." + localId + "." + occurrence.ToString("N");
        return ((IEnumerable)SpGet(CurrentPlayer, "missions")!).Cast<object>()
            .Single(mission => (string?)SpGet(mission, "storyId") == identifier);
    }

    private IEnumerable<object?> ClaimStory(IStoryProvider provider, Guid occurrence, long expectedCredits, string localId = "mission-x")
    {
        var mission = HeldStory(provider, occurrence, localId);
        foreach (var frame in Wait(() => (bool)mission.GetType().GetMethod("CanClaimRewards")!.Invoke(mission, null)!,
            "native story objectives ready")) yield return frame;
        long before = Convert.ToInt64(SpGet(CurrentPlayer, "credits"));
        // The real native claim path evaluates objectives and pays rewards; no forced completion,
        // objective field writes or synthetic observer events are used.
        CompleteStoryNative(mission, false);
        Require(Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == before + expectedCredits, "Native story payout differs from the author reward.");
        Require(provider.Unresolved(localId).Occurrences.All(x => x.OccurrenceId != occurrence), "Native claim did not automatically retire the occurrence.");
        CompleteStoryNative(mission, false);
        Require(Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == before + expectedCredits, "Repeated native claim paid an owned occurrence twice.");
    }
}
