using System;
using System.Collections;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class LifecyclePatches
{
    internal static GameAdapter? Adapter;

    internal static class Load
    {
        internal static void Prefix(object __instance, out GameAdapter.LoadRequest? __state)
        {
            GameAdapter.LoadRequest? state = null;
            Adapter?.Guard(() => state = Adapter.BeginLoad(__instance));
            __state = state;
        }
        internal static Exception? Finalizer(GameAdapter.LoadRequest? __state, Exception? __exception)
        {
            if (__state != null) Adapter?.Guard(() => Adapter.EndLoadRequest(__state, __exception));
            return __exception;
        }
    }
    internal static class LoadRoutine
    {
        internal static void Postfix(ref IEnumerator __result)
        {
            var result = __result;
            Adapter?.Guard(() => result = Adapter.ObserveLoad(result));
            __result = result;
        }
    }
    internal static class LoadFailure
    { internal static void Prefix() => Adapter?.Guard(() => Adapter.LoadFailed()); }
    internal static class NewPlayer
    {
        internal static void Prefix(out Guid? __state)
        {
            Guid? state = null;
            Adapter?.Guard(() => state = Adapter.BeginNewPlayer());
            __state = state;
        }
        internal static Exception? Finalizer(Guid? __state, Exception? __exception)
        {
            if (__state.HasValue)
                Adapter?.Guard(() => Adapter.EndNewPlayer(__state.Value, __exception));
            return __exception;
        }
    }
    internal static class Scenes
    {
        internal static void Prefix(out Guid? __state)
        {
            Guid? state = null;
            Adapter?.Guard(() => state = Adapter.PlayerReconstructed());
            __state = state;
        }
        internal static Exception? Finalizer(Guid? __state, Exception? __exception)
        {
            if (__state.HasValue && __exception != null)
                Adapter?.Guard(() => Adapter.Hub.Fail(__state.Value, "Scene initialization request threw " + __exception.GetType().Name));
            return __exception;
        }
    }
    internal static class Menu
    { internal static void Prefix() => Adapter?.Guard(() => Adapter.Invalidate("Menu or splash transition requested.")); }
    internal static class Gameplay
    {
        internal static void Prefix(out Guid? __state)
        {
            Guid? state = null;
            Adapter?.Guard(() => state = Adapter.CaptureGameplay());
            __state = state;
        }
        internal static Exception? Finalizer(object __instance, Guid? __state, Exception? __exception)
        {
            if (__state.HasValue) Adapter?.Guard(() => Adapter.GameplayCompleted(__state.Value, __instance, __exception));
            return __exception;
        }
    }
}
