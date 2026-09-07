using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckNewGameStories()
    {
        if (!File.Exists(Path.Combine(_root!, "story.enabled"))) yield break;
        Require(_api!.CurrentSession!.Origin == SessionOrigin.NewGame, "Owned new-game proof requires an actual new game.");
        foreach (var frame in Wait(NativeTravelReady, "new-game story world")) yield return frame;
        var author = Chainloader.PluginInfos["vg-story-campaign"].Instance;
        var target = (string)SpGet(SpGet(CurrentPlayer, "currentPointOfInterest")!, "guid")!;
        var faction = (string)SpGet(SpGet(NativeType("Source.Galaxy.Faction"), "player")!, "identifier")!;
        var registration = (StoryRegistrationResult)author.GetType().GetMethod("Register")!.Invoke(author, new object[] { target, faction })!;
        Require(registration.Succeeded, "New-game campaign registration refused: " + registration.Diagnostic);
        var provider = (IStoryProvider)author.GetType().GetProperty("Provider")!.GetValue(author)!;
        Require(provider.Unresolved("mission-x").Knowledge == StoryKnowledge.Known && provider.Unresolved("mission-x").Occurrences.Count == 0,
            "New-game owner state unavailable or inherited.");
        var session = _api.CurrentSession.Id;
        var offered = provider.Offer(session, "mission-x");
        var active = provider.Offer(session, "mission-x");
        Require(offered.Accepted && active.Accepted && provider.Activate(session, active.OccurrenceId).Accepted, "New-game owned content admission failed.");
        Save("qa-story-new-game", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-new-game")) yield return frame;
        var restored = provider.Unresolved("mission-x");
        Require(restored.Knowledge == StoryKnowledge.Known && restored.Occurrences.Count == 2
            && restored.Occurrences.Single(x => x.OccurrenceId == offered.OccurrenceId).Stage == StoryOccurrenceStage.Offered
            && restored.Occurrences.Single(x => x.OccurrenceId == active.OccurrenceId).Stage == StoryOccurrenceStage.Active,
            "New-game offered/active state did not round-trip.");
        Require(provider.Withdraw(session, offered.OccurrenceId).Status == StoryTransitionStatus.StaleSession, "New-game pre-load token remained valid.");
        Invoke(author, "ReleaseProvider");
        WriteAtomic("story-new-game.txt", new[] { "PASS", "owned-new-game-roundtrip-v1" });
        Passed("owned-new-game-roundtrip-v1");
    }
}
