using System;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonRewardPatches
{
    internal static DungeonRewardAdapter? Adapter { get; set; }
    internal static DungeonCrewObserver? Crew { get; set; }
    internal static class PrisonerScope
    {
        internal static void Prefix(object __instance, out IDisposable? __state) => __state = Crew?.Begin(__instance);
        internal static Exception? Finalizer(IDisposable? __state, Exception? __exception) { __state?.Dispose(); return __exception; }
    }
    internal static class Prisoners
    {
        internal static void Postfix(object __instance, string __0, int __1, int __result) => Crew?.PrisonersApplied(__instance, __0, __1, __result);
    }
    internal static class CrewSample
    {
        internal static void Postfix(object __instance) => Crew?.Sample(__instance);
    }
    internal static class Loot
    {
        internal static void Prefix(object __instance, object __1, out IDisposable? __state) => __state = Adapter?.Begin(__instance, __1, false);
        internal static Exception? Finalizer(IDisposable? __state, Exception? __exception) { __state?.Dispose(); return __exception; }
    }
    internal static class Count
    {
        internal static void Postfix(object __0, ref int __result) { if (Adapter != null) __result = Adapter.LootCount(__0, __result); }
    }
    internal static class MasteryScope
    {
        internal static void Prefix(object __instance, out IDisposable? __state) => __state = Adapter?.Begin(__instance, null, true);
        internal static Exception? Finalizer(IDisposable? __state, Exception? __exception) { __state?.Dispose(); return __exception; }
    }
    internal static class Mastery
    {
        internal static void Prefix(object __instance, ref float __0, object __1)
        { if (Adapter != null) __0 = Adapter.Mastery(__instance, __0, __1.ToString() ?? ""); }
    }
}
