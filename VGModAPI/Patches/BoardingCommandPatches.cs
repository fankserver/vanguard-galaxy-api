using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class BoardingCommandPatches
{
    internal static BoardingCommandAdapter? Adapter;
    internal static BoardingCommandService? Service;
    internal static class RemoveAssigned
    {
        internal static bool Prefix(object __instance, ref Dictionary<string, int> __result)
        {
            if (Adapter == null || Adapter.RemoveAssigned(__instance, out var manifest)) return true;
            __result = manifest!; return false;
        }
    }
    internal static class BeginWalk
    {
        internal static bool Prefix(object __instance, out object? __state)
        {
            __state = null; return Adapter?.BeginWalk(__instance, out __state) ?? true;
        }
        internal static Exception? Finalizer(object? __state, Exception? __exception) => Adapter?.EndWalk(__state, __exception) ?? __exception;
    }
    internal static class Serialization
    {
        internal static void Prefix(out BoardingCommandService? __state) { __state = Service; __state?.BeginSerialization(); }
        internal static Exception? Finalizer(BoardingCommandService? __state, Exception? __exception) { __state?.EndSerialization(); return __exception; }
    }
    internal static class Autonomous
    {
        internal static bool Prefix(object __instance, bool __0) => Adapter == null || Service == null || Adapter.AllowAutonomous(__instance, __0, Service);
    }
    internal static class Manual
    {
        internal static void Prefix(object __instance)
        {
            if (Adapter != null && Service != null) Adapter.ManualTakeover(__instance, Service);
        }
    }
}
