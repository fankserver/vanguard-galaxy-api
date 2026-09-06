using System;
using System.Collections;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class TravelPatches
{
    internal static TravelNativeAdapter? Adapter;

    // SpaceStationInterior readiness requires an attributed, nonthrowing Awake followed by
    // a nonthrowing Start with the exact live instance; a quoted field carries that attribution.
    private static object? _awakeInterior;

    internal static class Arrival
    {
        // SpaceshipHasArrived is called for same-system POI arrival immediately before
        // TravelToNextWaypoint may start the next leg; scopes coalesce nested base/override
        // calls and suppress reentrant duplicates for one manager.
        internal static void Prefix(object __instance, out object? __state)
        {
            object? token = null;
            Adapter?.Guard(() => token = Adapter.OnArrivalEnter(__instance));
            __state = token;
        }
        internal static Exception? Finalizer(object __instance, object? __state, Exception? __exception)
        {
            var adapter = Adapter;
            if (adapter != null) adapter.Guard(() => adapter.OnArrivalExit(__state, __instance, __exception));
            return __exception;
        }
    }
    internal static class Route
    {
        // SetRouteToPOI success is an accepted request (first waypoint is in the departure
        // system), not departure or arrival evidence.
        internal static void Postfix(object __instance, bool __result)
        {
            var adapter = Adapter;
            if (adapter == null) return;
            adapter.Guard(() =>
            {
                if (__result && adapter.Bindings.Player != null)
                    adapter.OnRouteRequested(adapter.Bindings.Player, adapter.Bindings.Target(__instance)!);
            });
        }
    }
    internal static class Cancel
    {
        internal static void Postfix(bool __result)
        {
            var adapter = Adapter;
            adapter?.Guard(() => { if (__result) adapter.OnTravelCancelled(); });
        }
    }
    internal static class Departure
    {
        // UnloadCurrentScene clearing the origin POI is real departure, not a request/iterator.
        internal static void Postfix()
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.OnDeparture(adapter.Bindings.Player!));
        }
    }
    internal static class JumpGate
    {
        internal static void Postfix(ref IEnumerator __result, object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            var result = __result;
            adapter.Guard(() => result = adapter.WrapJump(result, TravelMode.JumpGate, __instance, adapter.Bindings.Player));
            __result = result;
        }
    }
    internal static class JumpWormhole
    {
        internal static void Postfix(ref IEnumerator __result, object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            var result = __result;
            adapter.Guard(() => result = adapter.WrapJump(result, TravelMode.Wormhole, __instance, adapter.Bindings.Player));
            __result = result;
        }
    }
    internal static class InteriorAwake
    {
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter;
            if (adapter == null) return;
            adapter.Guard(() =>
            {
                // Attribution: a nonthrowing Awake that actually claimed the live instance.
                if (ReferenceEquals(adapter.Bindings.InteriorInstance(), __instance)) _awakeInterior = __instance;
            });
        }
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        {
            if (__exception != null && ReferenceEquals(_awakeInterior, __instance)) _awakeInterior = null;
            return __exception;
        }
    }
    internal static class InteriorStart
    {
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        {
            var adapter = Adapter;
            if (adapter != null)
            {
                if (__exception != null) { if (ReferenceEquals(_awakeInterior, __instance)) _awakeInterior = null; }
                else adapter.Guard(() =>
                {
                    if (_awakeInterior != null && ReferenceEquals(_awakeInterior, __instance)
                        && ReferenceEquals(adapter.Bindings.InteriorInstance(), __instance))
                    {
                        var station = adapter.Bindings.InteriorStation(__instance);
                        adapter.OnInteriorReady(adapter.Bindings.Player!, station!, null);
                    }
                });
            }
            return __exception;
        }
    }
    internal static class InteriorDestroy
    {
        internal static void Prefix(object __instance)
        {
            var adapter = Adapter;
            if (adapter == null) return;
            adapter.Guard(() =>
            {
                // Only revoke the current lease; a stale older interior destroy must not
                // invalidate a replacement.
                if (ReferenceEquals(adapter.Bindings.InteriorInstance(), __instance))
                {
                    var station = adapter.Bindings.InteriorStation(__instance);
                    adapter.OnInteriorDestroyed(adapter.Bindings.Player!, station!);
                }
            });
        }
    }
}
