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
        var answeredOnce = false;
        var result = new List<TestStep>(LiveBoot.Steps(PluginId, lifecycle, events))
        {
            new TestStep("offer and activate the campaign", "StoryMissions.OnHud(offer-campaign) / IStoryMission.Activate", () =>
            {
                // Activate() is queued, so the mission sits at Offered until the next safe execution
                // window. Click the button once (its label changes to "Campaign: ..." afterwards, so
                // it cannot be re-matched), then wait for the queued activation to reach Active.
                var campaign = NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeCampaign");
                if (campaign == null) return NativeGameplay.ClickHudRow("Offer campaign");
                return campaign.State == StoryMissionState.Active;
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
            new TestStep("answer the witness and complete the campaign", "StoryMissions.OnHud(answer) / DeclareChoices / GamePlayer.CompleteMission", () =>
            {
                // SetProgress and DeclareChoices are queued one-shot mutations: re-invoking the answer
                // every tick re-queues them without letting the campaign settle. Click the button once,
                // then wait for the queued completion to reach Completed.
                if (!answeredOnce)
                {
                    answeredOnce = NativeGameplay.ClickHudRow("Answer the witness");
                    return false;
                }
                var active = NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeCampaign");
                if (active == null) throw new InvalidOperationException("Campaign ended before it completed after the answer.");
                if (active.State != StoryMissionState.Completed)
                {
                    NativeGameplay.ClaimMissionRewards(active);
                    return false;
                }
                if ((int)NativeGameplay.GetField(Plugin()!, "_followUpsOffered")! != 1)
                    throw new InvalidOperationException("Completed without offering the follow-up.");
                return true;
            }),
            new TestStep("offer the generated job", "StoryMissions.OnHud(offer-job) / IStoryMission.Activate", () =>
            {
                var job = NativeGameplay.Field<IStoryMission>(Plugin()!, "_activeJob");
                if (job == null) return NativeGameplay.ClickHudRow("Offer generated job");
                return true;
            }),
            new TestStep("abandon active missions", "StoryMissions.OnHud(abandon) / IStoryMission.Abandon", () =>
            {
                var p = Plugin()!;
                var campaign = NativeGameplay.Field<IStoryMission>(p, "_activeCampaign");
                var job = NativeGameplay.Field<IStoryMission>(p, "_activeJob");
                if (campaign != null || job != null)
                {
                    // Only click while there is something still active to abandon.
                    if (!NativeGameplay.ClickHudRow("Abandon active missions")) return false;
                }
                return campaign == null && job == null;
            }),
        };
        return result;
    }
}
