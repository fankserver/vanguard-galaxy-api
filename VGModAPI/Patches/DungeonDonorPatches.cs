using VGModAPI.Runtime;
namespace VGModAPI.Patches;
internal static class DungeonDonorPatches
{
    internal static DungeonDonorRecoveryHooks? Hooks { get; set; }
    internal static class DonorUpdate
    {
        internal static bool Prefix(object __instance, out VGModAPI.Core.DungeonMutationFence.Lease? __state)
        { __state = null; return Hooks == null || Hooks.BeginDonorUpdate(__instance, out __state); }
        internal static System.Exception? Finalizer(object __instance, System.Exception? __exception, VGModAPI.Core.DungeonMutationFence.Lease? __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) __state.Failed();
            __state.Dispose();
            if (__exception == null) Hooks?.DonorAborted(__instance);
            return __exception;
        }
    }
}
