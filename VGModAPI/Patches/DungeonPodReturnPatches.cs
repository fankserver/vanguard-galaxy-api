using System;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonPodReturnPatches
{
    internal static DungeonPodReturnObserver? Observer { get; set; }
    internal static Action<Exception>? Report { get; set; }
    private static void Guard(Action action)
    { try { action(); } catch (Exception error) { try { Report?.Invoke(error); } catch { } } }
    internal static class Return
    {
        internal static bool Prefix(object __instance, object __0, out DungeonPodReturnObserver.ReturnScope? __state)
        { __state = null; return Observer?.Begin(__instance, __0, out __state) ?? true; }
        internal static void Postfix(DungeonPodReturnObserver.ReturnScope? __state) => Guard(() => __state?.Complete());
        internal static Exception? Finalizer(Exception? __exception, DungeonPodReturnObserver.ReturnScope? __state)
        { Guard(() => __state?.Dispose()); return __exception; }
    }
    internal static class Crew
    {
        internal static void Postfix(object __instance, string __0, int __1, int __result) => Guard(() => Observer?.CrewAdded(__instance, __0, __1, __result));
    }
    internal static class Overflow
    {
        internal static void Prefix(string __0, int __1, object __2, out DungeonPodReturnObserver.OverflowScope? __state)
        { __state = Observer?.BeginOverflow(__2, __0, __1); }
        internal static void Postfix(DungeonPodReturnObserver.OverflowScope? __state) => Guard(() => __state?.Complete());
        internal static Exception? Finalizer(Exception? __exception, DungeonPodReturnObserver.OverflowScope? __state)
        { Guard(() => __state?.Dispose()); return __exception; }
    }
    internal static class Persisted
    {
        internal static void Prefix(object __instance, object __0) => Guard(() => Observer?.AddingPersistable(__instance, __0));
        internal static void Postfix(object __instance, object __0) => Guard(() => Observer?.AddedPersistable(__instance, __0));
    }
}
