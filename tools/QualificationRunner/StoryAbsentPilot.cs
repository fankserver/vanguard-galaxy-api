using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckAbsentStoryAuthors()
    {
        if (!File.Exists(Path.Combine(_root!, "story-absent.enabled"))) yield break;
        Require(!Chainloader.PluginInfos.ContainsKey("vg-story-campaign") && !Chainloader.PluginInfos.ContainsKey("vg-story-job"),
            "Absent-author probe loaded an author plugin.");
        Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "OwnedStoryCampaign" or "OwnedStoryJob"),
            "Absent-author assembly remains loaded.");
        foreach (var frame in Wait(NativeTravelReady, "absent-author world ready")) yield return frame;
        AssertAbsentStoryProtection();
        Save("qa-story-absent", VGModAPI.LifecycleEventKind.SaveSucceeded);
        foreach (var frame in StoryLoadReady("qa-story-absent")) yield return frame;
        AssertAbsentStoryProtection();
        WriteAtomic("story-absent.txt", new[] { "PASS", "owned-story-absent-assemblies-v1" });
        Passed("owned-story-absent-assemblies-v1");
    }

    private void AssertAbsentStoryProtection()
    {
        var missions = ((IEnumerable)SpGet(CurrentPlayer, "missions")!).Cast<object>().Where(m =>
            ((string?)SpGet(m, "storyId"))?.StartsWith("vgmodapi.story.", StringComparison.Ordinal) == true).ToArray();
        Require(missions.Length == 2, "Absent-author fixture must contain exactly two held owned missions.");
        foreach (var mission in missions)
        {
            var before = mission.GetType().GetMethod("ToJson")!.Invoke(mission, null)!.ToString();
            long credits = Convert.ToInt64(SpGet(CurrentPlayer, "credits"));
            Invoke(mission, "Update", 1f);
            CompleteStoryNative(mission, true);
            Invoke(mission, "ClaimRewards", true);
            Require(Convert.ToInt64(SpGet(CurrentPlayer, "credits")) == credits, "Absent author mission paid rewards.");
            Require(((IEnumerable)SpGet(CurrentPlayer, "missions")!).Cast<object>().Any(m => ReferenceEquals(m, mission)),
                "Absent author mission was removed.");
            Require(before == mission.GetType().GetMethod("ToJson")!.Invoke(mission, null)!.ToString(),
                "Absent author mission serialized state changed.");
        }
    }
}
