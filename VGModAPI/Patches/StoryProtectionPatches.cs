using System;
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
    /// The private follow-up route, which only runs for a mission carrying a follow-up identifier.
    /// API content never carries one, so this guard is defensive: it is NOT the button the player
    /// presses, which is handled by <see cref="AbandonMission"/>.
    /// </summary>
    internal static class RetryAsNextMission
    {
        private static bool Prefix(object __instance) => Allow(__instance);
    }

    /// <summary>
    /// The route the game's own abandon/retry button actually takes: it removes the mission and, for a
    /// retryable story mission, re-adds the same identifier from the catalog. Both halves are one
    /// operation, so the guard wraps the whole method.
    ///
    /// A quarantined mission is refused before anything happens, so the raw object stays in the
    /// player's list exactly as the save had it and the catalog is never asked for an entry that may
    /// be gone. An admitted one is handed to the owning module, which suspends the outcome the
    /// removal would otherwise record and holds the catalog entry, and is told afterwards what the
    /// game actually ended up holding.
    /// </summary>
    internal static class AbandonMission
    {
        private static bool Prefix(object mission, out StoryAbandonState? __state)
        {
            __state = null;
            if (Quarantine == null) return true;
            if (!Quarantine.AllowAbandon(mission, out var state)) return false;
            __state = state;
            return true;
        }

        /// <summary>
        /// Runs whether the original returned or threw, and settles the transaction exactly once. It
        /// returns nothing, so the original's exception is passed on unchanged.
        /// </summary>
        private static void Finalizer(StoryAbandonState? __state)
        {
            if (__state != null) Quarantine?.EndAbandon(__state);
        }
    }

    /// <summary>
    /// Objective progression. The game dispatches triggers to every objective of every held mission,
    /// so the guard recognises the objectives of quarantined missions by identity.
    /// </summary>
    internal static class ProcessMissionTrigger
    {
        internal static Action? ObjectiveActivity { get; set; }
        private static bool Prefix(object __instance) => Quarantine == null || !Quarantine.BlocksObjective(__instance);
        private static void Postfix() { try { ObjectiveActivity?.Invoke(); } catch { } }
    }
}
