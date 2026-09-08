using VGModAPI.Runtime;
namespace VGModAPI.Patches;
internal static class DungeonTerminalRecoveryPatches
{
    internal static DungeonRefundHooks? Hooks { get; set; }
    internal static class WalkComplete
    {
        internal static bool Prefix(object __instance, out DungeonPodReturnObserver.WalkScope? __state)
        {
            __state = null; if (Hooks == null) return true;
            return Hooks.ObserveOperation(__instance) && Hooks.ReturnObserver.BeginWalk(__instance, out __state);
        }
        internal static void Postfix(DungeonPodReturnObserver.WalkScope? __state) => __state?.Complete();
        internal static System.Exception? Finalizer(System.Exception? __exception, DungeonPodReturnObserver.WalkScope? __state)
        { __state?.Dispose(); return __exception; }
    }
    internal static class Terminal
    {
        internal static bool Prefix(object __instance, out VGModAPI.Core.DungeonPodPersistence.TerminalAttempt? __state)
        {
            __state = null; if (Hooks == null) return true;
            if (!Hooks.ObserveOperation(__instance) || Hooks.OperationId(__instance) is not { } id) return false;
            __state = Hooks.State.BeginTerminal(id); return __state != null;
        }
        internal static void Postfix(VGModAPI.Core.DungeonPodPersistence.TerminalAttempt? __state) => __state?.Completed();
        internal static System.Exception? Finalizer(System.Exception? __exception, VGModAPI.Core.DungeonPodPersistence.TerminalAttempt? __state)
        { __state?.Dispose(); return __exception; }
    }
}
