using System;
using UnityEngine;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class GameplayUiPatches
{
    internal static GameplayUiRuntime? Runtime;
    internal static class Awake
    {
        internal static Exception? Finalizer(Exception? __exception)
        {
            // Awake sets the singleton. Reconcile even if its later initialization threw.
            Runtime?.Reconcile();
            return __exception;
        }
    }
    internal static class Start
    {
        internal static void Prefix(out GameplayUiRuntime.StartAttempt? __state) => __state = Runtime?.Starting();
        internal static void Postfix(GameplayUiRuntime.StartAttempt? __state, bool __runOriginal)
        {
            if (__state != null) __state.RanOriginal = __runOriginal;
        }
        internal static Exception? Finalizer(Component __instance, GameplayUiRuntime.StartAttempt? __state, Exception? __exception)
        {
            if (__exception == null && __state?.RanOriginal == true) __state.Owner.Started(__instance, __state);
            return __exception;
        }
    }
}
