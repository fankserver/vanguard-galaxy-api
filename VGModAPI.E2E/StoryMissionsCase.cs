using System;
using System.Collections.Generic;
using System.Reflection;

namespace VGModAPI.E2E;

/// <summary>
/// Live E2E for the actual examples/StoryMissions plugin. It clicks the rendered HUD to offer and
/// activate the hand-authored campaign beat, advances its scripted objective through the real API,
/// declares the reply choice (which completes the mission and fires the follow-up), then exercises
/// the generated job and the abort path. Every assertion reads the plugin's live state or the
/// mission's public API surface.
/// </summary>
internal static class StoryMissionsCase
{
    internal const string Id = "story-missions";
    internal const string PluginId = "vgmodapi.example.story-missions";
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        object? Plugin() => NativeGameplay.PluginInstance(PluginId);
        var result = new List<TestStep>(LiveBoot.Steps(PluginId, lifecycle, events))
        {
            new TestStep("offer and activate the campaign", "StoryMissions.OnHud(offer-campaign) / IStoryMission.Activate", () =>
            {
                if (!NativeGameplay.ClickHudRow("Offer campaign")) return false;
                var active = NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeCampaign");
                return active != null && active.State == StoryMissionState.Active;
            }),
            new TestStep("advance talk objective to 3/3", "StoryMissions.OnHud(talk) / IStoryObjective.SetProgress", () =>
            {
                var active = NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeCampaign");
                if (active == null) throw new InvalidOperationException("Campaign no longer active before advancing.");
                if (!NativeGameplay.ClickHudRow("Hear the witness")) { /* still needs more clicks */ }
                var progress = active.GetObjective("talk").Snapshot.Progress;
                if (progress is not int p) return false;
                if (p < 3) return false;
                if (p > 3) throw new InvalidOperationException("Talk objective overshot 3: " + p);
                return true;
            }),
            new TestStep("answer the witness and complete the campaign", "StoryMissions.OnHud(answer) / DeclareChoices", () =>
            {
                if (!NativeGameplay.ClickHudRow("Answer the witness")) return false;
                if ((int)NativeGameplay.GetField(Plugin()!, "_followUpsOffered")! != 1) return false;
                var active = NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeCampaign");
                if (active == null || active.State != StoryMissionState.Completed)
                    throw new InvalidOperationException("Campaign did not complete after the declared answer.");
                return true;
            }),
            new TestStep("offer the generated job", "StoryMissions.OnHud(offer-job) / IStoryMission.Activate", () =>
            {
                if (!NativeGameplay.ClickHudRow("Offer generated job")) return false;
                return NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeJob") != null;
            }),
            new TestStep("abandon active missions", "StoryMissions.OnHud(abandon) / IStoryMission.Abandon", () =>
            {
                if (!NativeGameplay.ClickHudRow("Abandon active missions")) return false;
                var p = Plugin()!;
                return NativeGameplay.Field<IStoryMission>(p, "_activeCampaign") == null
                    && NativeGameplay.Field<IStoryMission>(p, "_activeJob") == null;
            }),
        };
        return result;
    }
}
