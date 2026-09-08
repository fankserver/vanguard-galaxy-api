using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonRecoveryCapturePatches
{
    internal static DungeonRecoveryRuntime? Runtime { get; set; }
    internal static class Serialization
    { internal static void Prefix() => Runtime?.State.EnsureSerializationAllowed(); }
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
        internal static void Postfix(object? __result) { if (__result != null) Runtime?.ObserveOperation(__result); }
    }
}
