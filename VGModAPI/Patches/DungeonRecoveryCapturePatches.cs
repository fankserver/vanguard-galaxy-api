using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonRecoveryCapturePatches
{
    private static DungeonRecoveryRuntime? _runtime;
    internal static DungeonRecoveryRuntime? Runtime
    { get => _runtime; set { _runtime = value; DungeonRefundPatches.Hooks = value?.RefundHooks; DungeonDonorPatches.Hooks = value?.DonorHooks; DungeonTerminalRecoveryPatches.Hooks = value?.RefundHooks; } }
    internal static class Retired
    { internal static void Postfix(object __instance) { if (Runtime?.State.CanMutate == true) Runtime.ObserveOperation(__instance); } }
    internal static class PendingExtraction
    { internal static bool Prefix(object __1) => Runtime == null || !Runtime.QueueRestore(__1, out _); }
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
