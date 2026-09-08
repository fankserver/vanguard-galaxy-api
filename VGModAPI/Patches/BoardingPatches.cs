using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class BoardingPatches
{
    internal static BoardingObserver? Observer;
    internal static class Target
    {
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        {
            if (__exception == null) Observer?.Guard(() => Observer.TargetReady(__instance));
            return __exception;
        }
    }
    internal static class Start
    {
        internal static Exception? Finalizer(object? __result, MethodBase __originalMethod, Exception? __exception)
        {
            if (__exception == null) Observer?.Guard(() => Observer.OperationReady(__result, __originalMethod.Name is "ResumeOperation" or "EnsureApproachOperation"));
            return __exception;
        }
    }
    internal static class Operation
    {
        internal static Exception? Finalizer(object __instance, MethodBase __originalMethod, Exception? __exception)
        {
            if (__exception == null) Observer?.Guard(() => Observer.OperationSignal(__instance, __originalMethod.Name));
            return __exception;
        }
    }
    internal static class Rewards
    {
        internal static void Prefix(object __instance, out object? __state)
        {
            object? state = null; Observer?.Guard(() => state = Observer.BeginRewards(__instance)); __state = state;
        }
        internal static Exception? Finalizer(object? __state, Exception? __exception)
        {
            // Successful nested resource applications remain facts even when a later batch step fails.
            Observer?.Guard(() => Observer.EndRewards(__state)); return __exception;
        }
    }
    internal static class Inventory
    {
        internal static Exception? Finalizer(object? __result, int __1, Exception? __exception)
        {
            if (__exception == null) Observer?.Guard(() => Observer.InventoryApplied(__result, __1));
            return __exception;
        }
    }
    internal static class Credits
    {
        internal static void Prefix(object __instance, out long? __state)
        {
            long? state = null; Observer?.Guard(() => state = Observer.CreditBalance(__instance)); __state = state;
        }
        internal static Exception? Finalizer(object __instance, long? __state, Exception? __exception)
        {
            Observer?.Guard(() => Observer.CreditsApplied(__instance, __state)); return __exception;
        }
    }
    internal static class WorldLoot
    {
        internal static Exception? Finalizer(object __instance, object __0, Exception? __exception)
        {
            if (__exception == null) Observer?.Guard(() => Observer.WorldLootApplied(__instance, __0)); return __exception;
        }
    }
    internal static class Capture
    {
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        {
            if (__exception == null) Observer?.Guard(() => Observer.Captured(__instance));
            return __exception;
        }
    }
}
