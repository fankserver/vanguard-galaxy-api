using System;
using System.Collections;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class TravelPatches
{
    internal static TravelNativeAdapter? Adapter;

    internal static class Arrival
    {
        // SpaceshipHasArrived is called for same-system POI arrival immediately before
        // TravelToNextWaypoint may start the next leg; scopes coalesce nested base/override
        // calls and suppress reentrant duplicates for one manager. The scope token is immutable
        // per call and is closed only by the matching Exit on this adapter's main thread.
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
        // UnloadCurrentScene clears the origin after the delegate did real work. It is a NOOP
        // returning immediately when the local manager is already null (no origin to unload);
        // only a real manager->null transition is departure, never a method return alone.
        private static object? _before;
        internal static void Prefix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => _before = adapter.Bindings.LocalManager(__instance));
        }
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter; if (adapter == null) { _before = null; return; }
            object? before = null;
            adapter.Guard(() =>
            {
                before = _before; _before = null;
                var after = adapter.Bindings.LocalManager(__instance);
                adapter.OnDeparture(adapter.Bindings.Player!, before != null && after == null);
            });
        }
    }
    internal static class RouteBoundary
    {
        // Verified final-route boundary: after TravelToNextWaypoint finishes, only a route with
        // no remaining waypoints and TravelActive()==false has actually completed. Attribution is
        // to the leg recorded by the last arrival; once-only via the adapter's completed set.
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.CheckRouteBoundary(__instance));
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
    internal static class DockQuick
    {
        // Immediate dock: DockQuick sets dockingState=Docked synchronously; postfix is the
        // physical boundary. Emitting only from these native boundaries never misreports the
        // initial loaded-docked state as a transition.
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.OnDockedPhysical(__instance));
        }
    }
    internal static class Dock
    {
        internal static void Postfix(ref IEnumerator __result, object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            var result = __result; var option = __instance;
            adapter.Guard(() =>
            {
                if (result != null) result = new CoroutineBoundaryObserver(result, onDone: () => adapter.OnDockedPhysical(option));
            });
            __result = result;
        }
    }
    internal static class Undock
    {
        internal static void Postfix(ref IEnumerator __result, object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            var result = __result; var option = __instance;
            adapter.Guard(() =>
            {
                if (result != null) result = new CoroutineBoundaryObserver(result,
                    onFirst: () => adapter.OnUndocking(option), onDone: () => adapter.OnLeaving(option));
            });
            __result = result;
        }
    }
    internal static class EmergencyUndock
    {
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.OnLeaving(__instance));
        }
    }
    internal static class InteriorAwake
    {
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            adapter.Guard(() => adapter.OnInteriorAwake(__instance, adapter.Bindings.Player!, null));
        }
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        {
            var adapter = Adapter; if (adapter != null)
                adapter.Guard(() => adapter.OnInteriorAwake(__instance, adapter.Bindings.Player!, __exception));
            return __exception;
        }
    }
    internal static class InteriorStart
    {
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            adapter.Guard(() => adapter.OnInteriorStart(__instance, adapter.Bindings.Player!, null));
        }
        internal static Exception? Finalizer(object __instance, Exception? __exception)
        {
            var adapter = Adapter; if (adapter != null)
                adapter.Guard(() => adapter.OnInteriorStart(__instance, adapter.Bindings.Player!, __exception));
            return __exception;
        }
    }
    internal static class InteriorDestroy
    {
        // Prefix so revocation is attributed while the live instance still points at it; a stale
        // older interior (instance already replaced / OnDestroy already cleared it) is skipped.
        internal static void Prefix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.OnInteriorDestroyed(__instance, adapter.Bindings.Player!));
        }
    }
}
