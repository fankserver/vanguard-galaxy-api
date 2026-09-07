using HarmonyLib;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

/// <summary>
/// The authoritative boundary in front of every way an API-owned mission can advance or pay out.
/// Each prefix returns false — skip the original — only for a mission this API could have written
/// that nobody currently vouches for. Nothing is written back: the mission object, its objectives and
/// the bytes it will serialize to are untouched, so the save keeps the player's content exactly as it
/// was loaded and a provider that returns later finds it intact.
///
/// These are installed independently of the story module: an orphaned owned mission is dangerous
/// precisely when that module is absent, disabled or unbound.
/// </summary>
internal static class StoryProtectionPatches
{
    internal static StoryQuarantine? Quarantine;

    private static bool Allow(object mission) => Quarantine == null || !Quarantine.Blocks(mission);

    /// <summary>Per-frame progression, including the auto-complete route into CompleteMission.</summary>
    internal static class MissionUpdate
    {
        private static bool Prefix(object __instance) => Allow(__instance);
    }

    /// <summary>Where rewards are actually granted; the UI claim button and auto-complete both arrive here.</summary>
    internal static class ClaimRewards
    {
        private static bool Prefix(object __instance) => Allow(__instance);
    }

    /// <summary>The player-level completion route, guarded as well as the reward call it makes.</summary>
    internal static class CompleteMission
    {
        private static bool Prefix(object m) => Allow(m);
    }

    /// <summary>Failure would flip the mission's own state and hand it to the retry path.</summary>
    internal static class MissionFailed
    {
        private static bool Prefix(object __instance) => Allow(__instance);
    }

    /// <summary>
    /// Retry looks the story identifier up in the catalog, which throws when an orphan's entry is
    /// gone and would otherwise re-add a live mission nobody owns.
    /// </summary>
    internal static class RetryAsNextMission
    {
        private static bool Prefix(object __instance) => Allow(__instance);
    }

    /// <summary>
    /// Objective progression. The game dispatches triggers to every objective of every held mission,
    /// so the guard recognises the objectives of quarantined missions by identity.
    /// </summary>
    internal static class ProcessMissionTrigger
    {
        private static bool Prefix(object __instance) => Quarantine == null || !Quarantine.BlocksObjective(__instance);
    }
}
