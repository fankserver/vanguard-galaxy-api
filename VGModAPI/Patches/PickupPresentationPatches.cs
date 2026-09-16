using System;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class PickupPresentationPatches
{
    internal static PickupPresentationRuntime? Runtime;
    internal static class Notify
    {
        internal static void Prefix(object item, int count, out IDisposable? __state)
            => __state = Runtime?.Begin(item, count);
        internal static Exception? Finalizer(Exception? __exception, IDisposable? __state)
        { __state?.Dispose(); return __exception; }
    }
    internal static class Show
    {
        internal static void Postfix(object __instance, object type, string postfix)
            => Runtime?.Apply(__instance, type, postfix);
    }
}
