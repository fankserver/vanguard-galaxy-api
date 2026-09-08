using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldLifetimePatches
{
    internal static IWorldLifetimeHookHost? Host = null;
    internal static class Ambient
    {
        internal static bool Prefix(object __instance) => Host?.AllowAmbient(__instance) ?? true;
    }
    internal static class TravelLeg
    {
        internal static void Prefix(out IWorldTravelCaptureHost? __state) => __state = Host as IWorldTravelCaptureHost;
        internal static void Postfix(object __instance, object target, ref System.Collections.IEnumerator __result, IWorldTravelCaptureHost? __state)
        { if (__state != null) __result = __state.WrapLeg(__instance, target, __result); }
    }
    internal static class SceneUnload
    {
        internal static bool Prefix(object __instance, string sceneName)
        {
            var operation = (Host as IWorldTravelCaptureHost)?.CaptureSceneUnload(__instance, sceneName);
            if (operation == null) return true;
            Observe(operation); return false;
        }
        private static async void Observe(System.Threading.Tasks.Task<bool> operation) => await operation;
    }
    internal static class SceneTransition
    {
        internal static void Prefix(object __instance) => (Host as IWorldTravelCaptureHost)?.RequireSceneTransition(__instance);
    }
    internal static class TravelChild
    {
        internal static void Prefix(out IWorldTravelCaptureHost? __state) => __state = Host as IWorldTravelCaptureHost;
        internal static void Postfix(object __instance, ref System.Collections.IEnumerator __result, IWorldTravelCaptureHost? __state)
        { if (__state != null) __result = __state.WrapChild(__instance, __result); }
    }
    internal static class Waypoint
    {
        internal sealed class Capture
        {
            internal readonly IWorldTravelCaptureHost Host;
            internal readonly object Token;
            internal Capture(IWorldTravelCaptureHost host, object token) { Host = host; Token = token; }
        }
        internal static void Prefix(object __instance, out Capture? __state)
        {
            __state = null;
            if (Host is IWorldTravelCaptureHost host && host.BeginWaypoint(__instance) is { } token) __state = new Capture(host, token);
        }
        internal static System.Exception? Finalizer(Capture? __state, System.Exception? __exception)
        {
            if (__state == null) return __exception;
            try { __state.Host.EndWaypoint(__state.Token); }
            catch (System.Exception error) { return __exception ?? error; }
            return __exception;
        }
    }
    internal static class Awake
    {
        internal static bool Prefix(object __instance) => Host?.AllowManagerAwake(__instance) ?? true;
    }
    internal static class Initialization
    {
        internal static void Postfix(object __instance, ref System.Collections.IEnumerator __result)
        {
            var host = Host;
            if (host != null) __result = new WorldManagerEnumerator(__result, __instance, host);
        }
    }
    internal static class Arrival
    {
        internal static bool Prefix(object __instance) => Host?.AllowManager(__instance) ?? true;
    }
    internal static class Spawn
    {
        internal static void Prefix(object __instance)
        {
            if (!(Host?.AllowManager(__instance) ?? true))
                throw new System.IO.InvalidDataException("Quarantined world content cannot spawn native objects.");
        }
    }
    internal static class Active
    {
        internal static bool Prefix(object __instance) => Host?.AllowUse(__instance) ?? true;
    }
    internal static class CanTravel
    {
        internal static bool Prefix(object __instance, ref bool __result)
        {
            if (Host?.AllowUse(__instance) ?? true) return true;
            __result = false; return false;
        }
    }
    internal static class Route
    {
        internal sealed class Capture
        {
            internal readonly IWorldRouteCaptureHost Host;
            internal readonly object Token;
            internal Capture(IWorldRouteCaptureHost host, object token) { Host = host; Token = token; }
        }
        internal static bool CapturePrefix(object __instance, object poi, ref bool __result, out Capture? __state)
        {
            __state = null;
            if (!Prefix(poi, ref __result)) return false;
            if (Host is IWorldRouteCaptureHost host && host.BeginRoute(__instance, poi) is { } token) __state = new Capture(host, token);
            return true;
        }
        internal static System.Exception? Finalizer(Capture? __state, bool __result, System.Exception? __exception)
        {
            if (__state == null) return __exception;
            try { __state.Host.CompleteRoute(__state.Token, __exception == null && __result); }
            catch (System.Exception error) { return __exception ?? error; }
            return __exception;
        }
        internal static bool Prefix(object poi, ref bool __result)
        {
            if (Host?.AllowUse(poi) ?? true) return true;
            __result = false; return false;
        }
    }
    internal static class Remove
    {
        internal static bool Prefix(object poi) => Host?.AllowRemoval(poi) ?? true;
    }
}
