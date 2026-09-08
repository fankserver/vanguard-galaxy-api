using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonRecoveryCapturePatches
{
    internal static DungeonRecoveryRuntime? Runtime { get; set; }
    internal static class PendingExtraction
    { internal static bool Prefix(object __1) => Runtime == null || !Runtime.QueueRestore(__1, out _); }
    internal static class WalkComplete
    {
        internal static bool Prefix(object __instance, out DungeonPodReturnObserver.WalkScope? __state)
        {
            __state = null; if (Runtime == null) return true;
            return Runtime.ObserveOperation(__instance) && Runtime.ReturnObserver.BeginWalk(__instance, out __state);
        }
        internal static void Postfix(DungeonPodReturnObserver.WalkScope? __state) => __state?.Complete();
        internal static System.Exception? Finalizer(System.Exception? __exception, DungeonPodReturnObserver.WalkScope? __state)
        { __state?.Dispose(); return __exception; }
    }
    internal static class DonorUpdate
    {
        internal static bool Prefix(object __instance, out VGModAPI.Core.DungeonMutationFence.Lease? __state)
        { __state = null; return Runtime == null || Runtime.BeginDonorUpdate(__instance, out __state); }
        internal static System.Exception? Finalizer(object __instance, System.Exception? __exception, VGModAPI.Core.DungeonMutationFence.Lease? __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) __state.Failed();
            __state.Dispose();
            if (__exception == null) Runtime?.DonorAborted(__instance);
            return __exception;
        }
    }
    internal static class Transfer
    {
        internal static bool Prefix(object __instance, out VGModAPI.Core.DungeonMutationFence.Lease? __state)
        {
            __state = null; if (Runtime == null) return true;
            if (!Runtime.OperationReady(__instance)) return false;
            __state = Runtime.State.BeginTransfer(); return __state != null;
        }
        internal static System.Exception? Finalizer(object __instance, System.Exception? __exception, VGModAPI.Core.DungeonMutationFence.Lease? __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) __state.Failed();
            __state.Dispose();
            if (__exception == null && Runtime?.State.CanMutate == true && !Runtime.ObserveOperation(__instance)) Runtime.State.RejectTransferSnapshot();
            return __exception;
        }
    }
    internal static class ResumeShip
    {
        internal static bool Prefix(object __0, ref object? __result)
        { if (Runtime == null || !Runtime.QueueRestore(__0, out var existing)) return true; __result = existing; return false; }
    }
    internal static class ResumeLocation
    {
        internal static bool Prefix(object __1, ref object? __result)
        { if (Runtime == null || !Runtime.QueueRestore(__1, out var existing)) return true; __result = existing; return false; }
    }
    internal static class Reconstruct
    { internal static bool Prefix(object __0) => Runtime == null || !Runtime.QueueRestore(__0, out _); }
    internal static class Attach
    { internal static bool Prefix(object __instance) => Runtime?.CanAttach(__instance) ?? true; }
    internal static class Arrival
    { internal static bool Prefix(object __instance) => Runtime?.CanArrive(__instance) ?? true; }
    internal static class Serialization
    { internal static void Prefix() => Runtime?.Checkpoint(); }
    internal static class Terminal
    {
        internal static bool Prefix(object __instance, out VGModAPI.Core.DungeonPodPersistence.TerminalAttempt? __state)
        {
            __state = null; if (Runtime == null) return true;
            if (!Runtime.ObserveOperation(__instance) || Runtime.Operations.OperationId(__instance) is not { } id) return false;
            __state = Runtime.State.BeginTerminal(id); return __state != null;
        }
        internal static void Postfix(VGModAPI.Core.DungeonPodPersistence.TerminalAttempt? __state) => __state?.Completed();
        internal static System.Exception? Finalizer(System.Exception? __exception, VGModAPI.Core.DungeonPodPersistence.TerminalAttempt? __state)
        { __state?.Dispose(); return __exception; }
    }
    internal static class Tick
    {
        internal static bool Prefix(object __instance) => Runtime?.ObserveOperation(__instance) ?? true;
        internal static void Postfix(object __instance) => Runtime?.ObserveOperation(__instance);
    }
    internal static class Started
    {
        internal static void Prefix(object? __1, out object? __state) => __state = Runtime?.ExistingOperation(__1);
        internal static void Postfix(object? __result, object? __state)
        { if (__result != null) Runtime?.ObserveOperation(__result, !object.ReferenceEquals(__result, __state)); }
    }
}
