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
        // SetRouteToPOI success accepts a route. The actual first hop is waypoints[0] (an
        // in-system leg to a source gate or the destination), never targetPoi (the final goal).
        internal static void Postfix(object __instance, bool __result)
        {
            var adapter = Adapter;
            if (adapter == null) return;
            // A genuine new route supersedes any pending leg (truthful Cancelled + fresh Request).
            adapter.Guard(() => { if (__result) adapter.RequestWaypointLeg(replacePending: true); });
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
        internal static void Prefix(object __instance, out object? __state)
        {
            object? before = null;
            var adapter = Adapter;
            adapter?.Guard(() => before = adapter.Bindings.LocalManager(__instance));
            __state = before;
        }
        internal static void Postfix(object __instance, object? __state)
        {
            var adapter = Adapter; if (adapter == null) return;
            adapter.Guard(() =>
            {
                var after = adapter.Bindings.LocalManager(__instance);
                adapter.OnDeparture(adapter.Bindings.Player!, __state != null && after == null);
            });
        }
    }
    internal static class RouteBoundary
    {
        // Prefix: request the next in-system hop (TravelToNextWaypoint -> StartTravel) with the
        // real waypoint; a cross-system waypoint is owned by the jump iterator instead.
        // Postfix: the verified final-route boundary (no waypoints and TravelActive()==false).
        // Children carry no lifecycle callbacks, so the per-hop request here is exactly the
        // StartTravel a bound (non-handoff) leg about to run.
        internal static void Prefix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.RequestWaypointLeg());
        }
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.CheckRouteBoundary(__instance));
        }
    }
    internal static class InSystemWarp
    {
        // TravelInSystem only runs after departure preparation, so its FIRST actual step is the
        // true warp/transport start of the requested in-system leg (robust empty-origin/re-route
        // departure evidence, excluding preparation; TravelActive() would include it). The bound
        // leg is already requested (SetRouteToPOI/RouteBoundary prefix) before this runs. Children
        // carry no lifecycle callback.
        internal static void Postfix(ref IEnumerator __result)
        {
            var adapter = Adapter; if (adapter == null) return;
            var inner = __result;
            if (inner == null) return;
            IEnumerator? wrapped = null;
            adapter.Guard(() => wrapped = new CoroutineBoundaryObserver(inner, onFirst: () => adapter.OnInSystemWarpStart()));
            if (wrapped != null) __result = wrapped;
        }
    }
    internal static class JumpGate
    {
        internal static void Postfix(ref IEnumerator __result, object __instance, object jumpGatePoi)
        {
            var adapter = Adapter; if (adapter == null) return;
            var result = __result;
            adapter.Guard(() => result = adapter.WrapJump(result, TravelMode.JumpGate, __instance, adapter.Bindings.Player, jumpGatePoi));
            __result = result;
        }
    }
    internal static class JumpWormhole
    {
        internal static void Postfix(ref IEnumerator __result, object __instance, object fromWormhole)
        {
            var adapter = Adapter; if (adapter == null) return;
            var result = __result;
            adapter.Guard(() => result = adapter.WrapJump(result, TravelMode.Wormhole, __instance, adapter.Bindings.Player, fromWormhole));
            __result = result;
        }
    }
    internal static class Dock
    {
        // DockQuick (restore/relink only; never a physical transition) is deliberately not hooked.
        // A real Dock() coroutine's completion is eligible whenever the player ship has physically
        // reached Docked. The context (session/player/ship) is pinned at the FIRST actual step, not
        // at factory creation, so a session/player replacement before or during the coroutine can
        // never emit an old operation's fact into the new session.
        internal static void Postfix(ref IEnumerator __result, object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            var inner = __result;
            if (inner == null) return;
            // Factory time: pin immutable session+player ownership now (never adopt a later session).
            var owner = adapter.CreateDockOwner();
            object? dockContext = null;
            IEnumerator? wrapped = null;
            adapter.Guard(() =>
            {
                wrapped = new CoroutineBoundaryObserver(inner,
                    onFirst: () => adapter.Guard(() => dockContext = adapter.CaptureDock(__instance, owner)),
                    onDone: () => adapter.OnDockedPhysical(dockContext));
            });
            if (wrapped != null) __result = wrapped;
        }
    }
    internal static class Undock
    {
        internal static void Postfix(ref IEnumerator __result, object __instance)
        {
            var adapter = Adapter; if (adapter == null) return;
            var inner = __result;
            if (inner == null) return;
            // Factory time: pin immutable session+player ownership now (never adopt a later session).
            var owner = adapter.CreateDockOwner();
            object? dockContext = null;
            IEnumerator? wrapped = null;
            adapter.Guard(() =>
            {
                wrapped = new CoroutineBoundaryObserver(inner,
                    onFirst: () => adapter.Guard(() => { dockContext = adapter.CaptureDock(__instance, owner); adapter.OnUndocking(dockContext); }),
                    onDone: () => adapter.OnLeaving(dockContext));
            });
            if (wrapped != null) __result = wrapped;
        }
    }
    internal static class EmergencyUndock
    {
        // Synchronous (no coroutine): factory time and execution coincide, so capture owner+ship here.
        internal static void Postfix(object __instance)
        {
            var adapter = Adapter;
            adapter?.Guard(() => adapter.OnLeaving(adapter.CaptureDock(__instance, adapter.CreateDockOwner())));
        }
    }
    internal static class InteriorAwake
    {
        // Finalizer alone performs the nonthrowing attribution (note Postfix is deliberately
        // absent so the lease is registered exactly once per Awake; finding 9).
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
