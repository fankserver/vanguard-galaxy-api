using System;
using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

/// <summary>Vanilla damage always runs; protection only restores the recorded condition afterwards.</summary>
internal static class UnitProtectionPatches
{
    internal static UnitProtectionRuntime? Runtime;
    internal static class Damage
    {
        internal static void Prefix(object __instance, out UnitProtectionRuntime.Condition? __state)
            => __state = Runtime?.Enter(__instance);
        internal static Exception? Finalizer(UnitProtectionRuntime.Condition? __state, Exception? __exception)
        {
            Runtime?.Exit(__state);
            return __exception; // Vanilla damage exceptions are never suppressed.
        }
    }
}
