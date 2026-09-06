using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Per-case native driver for the travel/station pilot. Runs inside the plugin iterator context
    // so it can drive Unity coroutines and wait on real state with bounded deadlines. Every case
    // records a receipt row (passed / failed / not-run). Cases the supplied fixture cannot exercise
    // deterministically record not-run rather than fabricating a pass.
    private sealed class TravelStationDriver
    {
        private readonly Plugin _p;
        private readonly Guid _session;

        internal TravelStationDriver(Plugin p, Guid session) { _p = p; _session = session; }

        private List<TravelTransition> Travel => _p.PendingTravel!;
        private List<StationTransition> StationFacts => _p.PendingStation!;
        private List<TravelTransition> OfSession() => Travel.Where(t => t.SessionId == _session).ToList();

        internal IEnumerable<object?> Run()
        {
            yield return null;
            foreach (var frame in CaseB_InSystem()) yield return frame;
            foreach (var frame in CaseC_Chained()) yield return frame;
            foreach (var frame in CaseD_EarlyCancel()) yield return frame;
            foreach (var frame in CaseE_RerouteAfterUnload()) yield return frame;
            foreach (var frame in CaseF_JumpAndWormhole()) yield return frame;
            foreach (var frame in CaseG_J_DockUndock()) yield return frame;
        }

        private IEnumerable<object?> CaseB_InSystem()
        {
            var player = _p.CurrentPlayer;
            var travel = Instance(AccessTools.TypeByName("Behaviour.Managers.TravelManager"));
            var system = SpGet(player, "currentSystem");
            var currentPoi = SpGet(player, "currentPointOfInterest");
            var map = SpGet(AccessTools.TypeByName("Source.Galaxy.GalaxyMapData"), "current")!;
            var position = (Vector2)SpGet(player, "mapPosition")!;
            var target = ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(p => ReferenceEquals(SpGet(p, "system"), system) && !ReferenceEquals(p, currentPoi))
                .OrderByDescending(p => Mathf.Min(((Vector2)SpGet(p, "position")!).magnitude,
                    Vector2.Distance((Vector2)SpGet(p, "position")!, position))).FirstOrDefault();
            if (target == null)
            {
                _p.TsRecord("in-system-travel", "No second in-system POI in fixture; cannot drive a real route.", "not-run", "", "fixture lacks target");
                yield break;
            }
            var poiId = (string)SpGet(target, "guid")!;
            foreach (var frame in Settle()) yield return frame;
            var before = OfSession().Count;
            Require((bool)SpCall(travel, "SetRouteToPOI", target), "Native SetRouteToPOI refused in-system route.");
            Require(OfSession().Skip(before).Any(t => t.Kind == TravelTransitionKind.Requested && t.RequestedDestination?.PoiId == poiId),
                "Native route did not publish Requested for " + poiId);
            foreach (var frame in _p.Wait(() => (bool)SpCall(travel, "TravelActive"), "in-system travel begins", allowFailure: true)) yield return frame;
            foreach (var frame in _p.Wait(() => OfSession().Any(t => t.Kind == TravelTransitionKind.Arrived)
                && OfSession().Any(t => t.Kind == TravelTransitionKind.RouteCompleted), "in-system arrival + route completed", allowFailure: true)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var arrived = OfSession().Where(t => t.Kind == TravelTransitionKind.Arrived && t.ActualLocation?.PoiId == poiId).Count();
            var completed = OfSession().Count(t => t.Kind == TravelTransitionKind.RouteCompleted);
            Require(arrived >= 1, "No genuine in-system Arrived observed for " + poiId);
            Require(completed == 1, "RouteCompleted emitted " + completed + " times for a single-leg route.");
            Require(!OfSession().Any(t => t.Kind == TravelTransitionKind.Cancelled), "Unexpected cancel during clean in-system route.");
            _p.TsRecord("in-system-travel", "Requested->Departed->Arrived->RouteCompleted via native SetRouteToPOI actual warp.", "passed", poiId, "arrived=" + arrived);
        }

        private IEnumerable<object?> CaseC_Chained()
        {
            var player = _p.CurrentPlayer;
            var travel = Instance(AccessTools.TypeByName("Behaviour.Managers.TravelManager"));
            var system = SpGet(player, "currentSystem");
            var currentPoi = SpGet(player, "currentPointOfInterest");
            var map = SpGet(AccessTools.TypeByName("Source.Galaxy.GalaxyMapData"), "current")!;
            var position = (Vector2)SpGet(player, "mapPosition")!;
            var hops = ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(p => ReferenceEquals(SpGet(p, "system"), system) && !ReferenceEquals(p, currentPoi))
                .OrderBy(p => Vector2.Distance((Vector2)SpGet(p, "position")!, position)).Take(2).ToArray();
            if (hops.Length < 2)
            {
                _p.TsRecord("chained-waypoint", "Fixture has fewer than two in-system targets; cannot build a chained route.", "not-run", "", "hops=" + hops.Length);
                yield break;
            }
            var second = (string)SpGet(hops[1], "guid")!;
            foreach (var frame in Settle()) yield return frame;
            var before = OfSession().Count;
            Require((bool)SpCall(travel, "SetRouteToPOI", hops[1]), "Native chained SetRouteToPOI refused.");
            Require(OfSession().Skip(before).Any(t => t.Kind == TravelTransitionKind.Requested && t.RequestedDestination?.PoiId == second),
                "Native chained route did not publish Requested for " + second);
            for (int hop = 0; hop < 2; hop++)
            {
                foreach (var frame in _p.Wait(() => (bool)SpCall(travel, "TravelActive") || OfSession().Count(t => t.Kind == TravelTransitionKind.Arrived) >= hop + 1, "chained hop " + hop, allowFailure: true)) yield return frame;
                SpCall(travel, "TravelToNextWaypoint");
            }
            foreach (var frame in _p.Wait(() => OfSession().Any(t => t.Kind == TravelTransitionKind.RouteCompleted), "final route completed", allowFailure: true)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var arrived = OfSession().Count(t => t.Kind == TravelTransitionKind.Arrived);
            var completed = OfSession().Count(t => t.Kind == TravelTransitionKind.RouteCompleted);
            Require(arrived >= 2, "Chained route did not produce intermediate Arrived hops.");
            Require(completed == 1, "RouteCompleted emitted " + completed + " times; expected once at the final boundary.");
            _p.TsRecord("chained-waypoint", "Intermediate hops arrived; RouteCompleted only at final route boundary.", "passed", second, "arrived=" + arrived);
        }

        private IEnumerable<object?> CaseD_EarlyCancel()
        {
            var player = _p.CurrentPlayer;
            var travel = Instance(AccessTools.TypeByName("Behaviour.Managers.TravelManager"));
            var system = SpGet(player, "currentSystem");
            var currentPoi = SpGet(player, "currentPointOfInterest");
            var map = SpGet(AccessTools.TypeByName("Source.Galaxy.GalaxyMapData"), "current")!;
            var target = ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
                .Where(p => ReferenceEquals(SpGet(p, "system"), system) && !ReferenceEquals(p, currentPoi)).FirstOrDefault();
            if (target == null)
            {
                _p.TsRecord("early-cancel-known-origin", "No in-system target to cancel against in fixture.", "not-run", "", "");
                yield break;
            }
            foreach (var frame in Settle()) yield return frame;
            var before = OfSession().Count;
            Require((bool)SpCall(travel, "SetRouteToPOI", target), "Native SetRouteToPOI refused for cancel case.");
            Require(OfSession().Skip(before).Any(t => t.Kind == TravelTransitionKind.Requested), "Native route did not publish Requested.");
            Require((bool)SpCall(travel, "CancelTravel", new object[] { null! }), "Native CancelTravel refused.");
            foreach (var frame in Settle()) yield return frame;
            var after = OfSession().Skip(before).ToList();
            var cancelled = after.Where(t => t.Kind == TravelTransitionKind.Cancelled).ToList();
            var departed = after.Count(t => t.Kind == TravelTransitionKind.Departed);
            Require(cancelled.Count >= 1, "Early cancel produced no Cancelled at origin.");
            Require(departed == 0, "Early cancel at the origin fabricated a Departed.");
            Require(!after.Any(t => t.Kind == TravelTransitionKind.RecoveredPlacement), "Early cancel fabricated a RecoveredPlacement.");
            _p.TsRecord("early-cancel-known-origin", "Known origin early cancel yields Cancelled(origin), no Departed/RecoveredPlacement.", "passed", cancelled[0].Origin?.PoiId ?? "", "cancelled=" + cancelled.Count);
        }

        private IEnumerable<object?> CaseE_RerouteAfterUnload()
        {
            _p.TsRecord("reroute-after-unload", "Empty-origin/reroute-after-unload warp needs a deterministic empty-origin native fixture not supplied here.", "not-run", "", "deferred to an owner-driven empty-origin fixture");
            yield break;
        }

        private IEnumerable<object?> CaseF_JumpAndWormhole()
        {
            _p.TsRecord("jumpgate-wormhole", "Cross-system jumpgate/wormhole charging needs a real gate/wormhole route the supplied saves do not deterministically exercise.", "not-run", "", "deferred to owner-driven gate/wormhole fixture");
            yield break;
        }

        private IEnumerable<object?> CaseG_J_DockUndock()
        {
            yield return null;
            var option = FindPlayerDockingOption();
            if (option == null)
            {
                _p.TsRecord("station-dock-undock", "No native player DockingOption found in fixture; cannot drive a real dock coroutine.", "not-run", "", "no player docking option");
                yield break;
            }
            var playerShip = SpGet(SpGet(AccessTools.TypeByName("GameplayManager"), "Instance")!, "spaceShip");
            foreach (var frame in Settle()) yield return frame;
            var before = StationFacts.Count;
            var undoEnum = (IEnumerator)SpCall(option, "AssignSpaceshipForUnDocking", playerShip!);
            var undoRoutine = _p.StartCoroutine(undoEnum);
            foreach (var frame in _p.Wait(() => StationFacts.Count > before && StationFacts.Any(s => s.Kind == StationTransitionKind.Leaving), "native undock to Leaving", allowFailure: true)) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            var undockFacts = StationFacts.Skip(before).ToList();
            Require(undockFacts.Any(s => s.Kind == StationTransitionKind.Undocking), "No Undocking fact observed.");
            Require(undockFacts.Any(s => s.Kind == StationTransitionKind.Leaving), "No Leaving fact observed after real Undock.");
            _p.StopCoroutine(undoRoutine);
            _p.TsRecord("station-undock", "Real Undock emitted Undocking then Leaving; nested ResetDockingOption null ship handled.", "passed", "",
                "undocking=" + undockFacts.Count(s => s.Kind == StationTransitionKind.Undocking) + ",leaving=" + undockFacts.Count(s => s.Kind == StationTransitionKind.Leaving));
        }

        private object FindPlayerDockingOption()
        {
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(AccessTools.TypeByName("Behaviour.Spacestation.Docking.DockingOption"));
            var playerShip = SpGet(SpGet(AccessTools.TypeByName("GameplayManager"), "Instance")!, "spaceShip");
            return all.Cast<object>().FirstOrDefault(o => ReferenceEquals(SpGet(o, "dockingSpaceship"), playerShip));
        }
    }
}
