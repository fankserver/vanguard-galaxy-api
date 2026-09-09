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
    internal static class CancelTravel
    {
        internal sealed class Capture
        {
            internal readonly IWorldTravelCancellationHost Host;
            internal readonly object Token;
            internal Capture(IWorldTravelCancellationHost host, object token) { Host = host; Token = token; }
        }
        internal static void Prefix(object __instance, out Capture? __state)
        {
            __state = null;
            if (Host is IWorldTravelCancellationHost host) __state = new Capture(host, host.BeginCancellation(__instance));
        }
        internal static System.Exception? Finalizer(Capture? __state, bool __result, System.Exception? __exception)
        {
            if (__state == null) return __exception;
            try { __state.Host.EndCancellation(__state.Token, __result || __exception != null); }
            catch (System.Exception error) { return __exception ?? error; }
            return __exception;
        }
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
    internal static class PayloadAttachment
    {
        internal static void Prefix(object __instance, object payload) => (Host as IWorldPayloadLifetimeHost)?.RequirePayloadAttachment(__instance, payload);
    }
    internal static class Payload
    {
        internal static void Prefix(object __instance) => (Host as IWorldPayloadLifetimeHost)?.RequirePayload(__instance);
    }
    internal static class GenerateArgument
    {
        internal static void Prefix(object poi) => Generate.Prefix(poi);
    }
    internal static class Generate
    {
        internal static void Prefix(object __instance)
        {
            if (!(Host?.AllowUse(__instance) ?? true)) throw new System.IO.InvalidDataException("Owned world generation is quarantined.");
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
    internal static class Generation
    {
        internal sealed class Capture
        {
            internal readonly IWorldGenerationHost? Host;
            internal readonly Capture? Parent;
            internal WorldGenerationAttempts.Scope? Scope;
            internal bool Closed;
            internal Capture(IWorldGenerationHost? host, Capture? parent) { Host = host; Parent = parent; }
        }
        private static Capture? _active;
        internal static void Prefix(object __instance, out Capture? __state) => StaticPrefix(__instance, out __state);
        internal static void StaticPrefix(object __0, out Capture? __state)
        {
            __state = new Capture(Host as IWorldGenerationHost, _active);
            _active = __state;
            if (__state.Host != null) __state.Scope = __state.Host.BeginGeneration(__0);
        }
        internal static void BuilderPrefix()
        {
            if (_active != null) _active.Host?.ConsumeBuilder();
            else (Host as IWorldGenerationHost)?.ConsumeBuilder();
        }
        internal static System.Exception? Finalizer(Capture? __state, System.Exception? __exception)
        {
            try { return __state?.Scope == null ? __exception : __state.Host!.EndGeneration(__state.Scope, __exception); }
            finally
            {
                if (__state != null)
                {
                    __state.Closed = true;
                    if (ReferenceEquals(_active, __state))
                    {
                        var parent = __state.Parent; while (parent != null && parent.Closed) parent = parent.Parent;
                        _active = parent;
                    }
                }
            }
        }
    }
    internal static class ActorAwake
    {
        internal static void Prefix(object __instance) => (Host as IWorldActorLifetimeHost)?.CaptureActor(__instance);
    }
    internal static class PersistableActivity
    {
        internal static bool Prefix(object __instance) => (Host as IWorldActorLifetimeHost)?.AllowPersistable(__instance) ?? true;
    }
    internal static class ActorContinuation
    {
        internal static void Prefix(object __instance, out System.Func<bool>? __state)
            => __state = (Host as IWorldActorLifetimeHost)?.CaptureActivity(__instance);
        internal static void Postfix(ref System.Collections.IEnumerator __result, System.Func<bool>? __state)
        {
            if (__state != null && __result != null) __result = new WorldManagerEnumerator(__result, __state);
        }
    }
    internal static class ActorData
    {
        internal static void Prefix(object __instance, object __0)
        {
            if (!((Host as IWorldActorLifetimeHost)?.AllowActorData(__instance, __0) ?? true))
                throw new System.IO.InvalidDataException("Owned actor data does not match its spawn origin.");
        }
    }
    internal static class ActorMutation
    {
        internal static void Prefix(object __instance)
        {
            if (!((Host as IWorldActorLifetimeHost)?.AllowActor(__instance) ?? true))
                throw new System.IO.InvalidDataException("Quarantined world actor cannot initialize or take damage.");
        }
    }
    internal static class ActorActivity
    {
        internal static bool Prefix(object __instance) => (Host as IWorldActorLifetimeHost)?.AllowActor(__instance) ?? true;
    }
    internal static class Spawn
    {
        internal sealed class Capture
        {
            internal readonly IWorldActorLifetimeHost Host;
            internal readonly System.IDisposable Scope;
            internal Capture(IWorldActorLifetimeHost host, System.IDisposable scope) { Host = host; Scope = scope; }
        }
        internal static void CapturePrefix(object __instance, object __0, out Capture? __state)
        {
            __state = null; Prefix(__instance);
            if (Host is IWorldActorLifetimeHost host) __state = new Capture(host, host.BeginSpawn(__instance, __0));
        }
        internal static void Postfix(object? __result, Capture? __state)
        { if (__result != null) __state?.Host.CaptureActor(__result); }
        internal static System.Exception? Finalizer(Capture? __state, System.Exception? __exception)
        {
            try { __state?.Scope.Dispose(); }
            catch (System.Exception error) { return __exception ?? error; }
            return __exception;
        }
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
