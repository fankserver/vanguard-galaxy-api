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
    private IEnumerable<object?> CheckLinkedStoryBars()
    {
        Require(File.Exists(Path.Combine(_root!, "bar-linked.enabled")), "Linked bar probe not armed.");
        WriteAtomic("bar-linked.txt", new[] { "INCOMPLETE" });
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "linked bar readiness")) yield return frame;
        var author = Chainloader.PluginInfos["vg-bar-author-a"].Instance;
        var station = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        Require(NativeType("Source.Galaxy.POI.SpaceStation").IsInstanceOfType(station), "Linked bar fixture must be a station.");
        var stationId = (string)SpGet(station, "guid")!;
        var faction = (string)SpGet(SpGet(station, "faction")!, "identifier")!;
        var registered = (StoryRegistrationResult)SpCall(author, "RegisterStory", faction);
        Require(registered.Succeeded, "Linked story registration failed.");
        var story = (IStoryProvider)SpGet(author, "StoryProvider")!;
        const string mission = "linked-mission", local = "linked-contact", seed = "bar-story-retained-seed";
        Require(!File.Exists(Path.Combine(_saveRoot!, "qa-bar-linked-empty.save")) && !File.Exists(Path.Combine(_saveRoot!, "qa-owned-bars.save")), "Linked slots must be unused.");
        Save("qa-bar-linked-empty", LifecycleEventKind.SaveSucceeded);
        var session = _api!.CurrentSession!.Id;
        var offered = story.Offer(session, mission);
        Require(offered.Accepted && offered.OccurrenceId != Guid.Empty && story.Activate(session, offered.OccurrenceId).Accepted, "Linked mission did not activate.");
        Require(((BarResult)SpCall(author, "RegisterLinked", stationId, local, seed, offered.OccurrenceId)).Succeeded, "Linked contact definition refused.");
        var provider = (IBarProvider)SpGet(author, "Provider")!;
        Require(provider.ProviderId == story.ProviderId, "Story and bar owner identity diverged.");
        Require(provider.ConfigureStation(stationId, BarRosterOwnership.Exclusive).Succeeded && provider.Place(session, local).Succeeded, "Linked contact placement refused.");
        BarRosterFinalized? latest = null;
        using var observation = ModApi.Bars!.Subscribe(Id, value => latest = value);
        void Refresh(int count, bool unavailable = false)
        {
            station = SpGet(CurrentPlayer, "currentPointOfInterest")!;
            Require((string)SpGet(station, "guid")! == stationId, "Linked station identity changed.");
            var bar = SpGet(station, "bar")!;
            latest = null; SpCall(bar, "CheckUpdatePatrons", false);
            var native = ((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>().ToArray();
            Require(native.Count(value => (string)SpGet(value, "seed")! == seed) == count, "Linked native presentation missing or duplicated.");
            if (unavailable) Require(latest == null, "Unready mission admitted a finalized plan.");
            else
            {
                Require(latest != null && latest.SessionId == _api!.CurrentSession!.Id && latest.StationId == stationId
                    && latest.Members.Count(value => value.OwnedId.HasValue) == count
                    && latest.Members.Count(value => value.OwnedId == new BarPatronId(provider.ProviderId, local) && value.Seed == seed) == count,
                    "Linked finalized identity mismatch.");
                Require(native.Length == latest!.Members.Count && native.Select(value => (int)SpGet(value, "seat")!).Distinct().Count() == native.Length,
                    "Linked roster differs from native membership or duplicates seats.");
            }
        }
        void Active()
        {
            var rows = story.Unresolved(mission);
            Require(rows.Knowledge == StoryKnowledge.Known && rows.Occurrences.Count == 1
                && rows.Occurrences[0].OccurrenceId == offered.OccurrenceId && rows.Occurrences[0].Stage == StoryOccurrenceStage.Active,
                "Linked mission occurrence did not restore exactly once.");
            Require(HeldStory(story, offered.OccurrenceId, mission) != null, "Restored linked native mission is missing.");
        }
        Active(); Refresh(1);
        Save("qa-owned-bars", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in Settle()) yield return frame;
        WriteAtomic("bar-linked-generation.txt", ReadBarGeneration());
        foreach (var frame in LoadReady("qa-owned-bars")) yield return frame;
        Active(); Refresh(1);
        Require(provider.Place(session, local).Status == BarStatus.StaleSession, "Linked placement accepted a stale session.");
        SpCall(author, "ReleaseStory");
        Refresh(0, unavailable: true);
        Require(((StoryRegistrationResult)SpCall(author, "RegisterStory", faction)).Succeeded, "Returning story provider refused.");
        story = (IStoryProvider)SpGet(author, "StoryProvider")!;
        // Register behavior before loading; neither the author nor this probe rebuilds saved rows.
        foreach (var frame in LoadReady("qa-owned-bars")) yield return frame;
        Active(); Refresh(1);
        foreach (var frame in LoadReady("qa-bar-linked-empty")) yield return frame;
        var empty = story.Unresolved(mission);
        Require(empty.Knowledge == StoryKnowledge.Known && empty.Occurrences.Count == 0, "Rollback leaked a future mission.");
        Refresh(0);
        foreach (var frame in LoadReady("qa-owned-bars")) yield return frame;
        Active(); Refresh(1);
        WriteAtomic("bar-linked.txt", new[] { "PASS", "active-mission;automatic-linked-restore;stale-session;provider-unavailable;registered-before-reload;rollback;no-replacement-placement" });
        Passed("linked-story-bar-restoration");
    }
}
