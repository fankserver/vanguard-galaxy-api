using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonRefundPatches
{
    internal static DungeonRefundHooks? Hooks;
    internal static class Cancellation
    {
        internal static bool Prefix(object __instance, out VGModAPI.Core.DungeonPodPersistence.Cancellation? __state)
        {
            __state = null; if (Hooks == null) return true;
            if (!Hooks.ObserveOperation(__instance) || Hooks.OperationId(__instance) is not { } id) return false;
            __state = Hooks.State.BeginCancellation(id); return __state != null;
        }
        internal static System.Exception? Finalizer(object __instance, System.Exception? __exception, VGModAPI.Core.DungeonPodPersistence.Cancellation? __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) __state.Failed(); __state.Dispose();
            if (__exception == null && Hooks?.State.CanMutate == true && !Hooks.ObserveOperation(__instance)) Hooks.State.RejectTransferSnapshot();
            return __exception;
        }
    }
    internal static class DockedRefund
    {
        internal static bool Prefix(object __instance, System.Reflection.MethodBase __originalMethod, out DungeonPodReturnObserver.RefundScope? __state)
        {
            __state = null; if (Hooks == null) return true;
            if (Hooks.State.CanMutate && !Hooks.ObserveOperation(__instance)) return false;
            if (Hooks.ReturnObserver.BeginDockedRefunds(__instance, __originalMethod.Name == "TriggerPodReturn", out __state)) return true;
            Hooks.State.RejectTransferSnapshot(); return false;
        }
        internal static void Postfix(DungeonPodReturnObserver.RefundScope? __state)
        { if (__state != null && !__state.Complete()) Hooks?.State.RejectTransferSnapshot(); }
        internal static System.Exception? Finalizer(object __instance, System.Exception? __exception, DungeonPodReturnObserver.RefundScope? __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) Hooks?.State.RejectTransferSnapshot(); __state.Dispose();
            if (__exception == null && Hooks?.State.CanMutate == true && !Hooks.ObserveOperation(__instance)) Hooks.State.RejectTransferSnapshot();
            return __exception;
        }
    }
}
