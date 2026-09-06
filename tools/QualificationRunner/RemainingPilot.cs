using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private bool _delayedSignalsRan;
    private Exception? _delayedSignalError;
    private object CurrentPlayer => SpGet(_player, "current")!;
    private bool TransitState => SpGet(CurrentPlayer, "currentPointOfInterest") == null
        && (bool)SpGet(SpGet(CurrentPlayer, "currentSpaceShip")!, "travelling")!
        && ((IEnumerable)SpGet(CurrentPlayer, "waypoints")!).Cast<object>().Any();

    private IEnumerable<object?> LoadReady(string name)
    {
        var previous = _api!.CurrentSession?.Id;
        Load(name);
        foreach (var frame in Wait(() => _api.CurrentSession?.Phase == SessionPhase.GameplayInitialized
            && _api.CurrentSession.Id != previous, "load " + name)) yield return frame;
    }

    private IEnumerable<object?> RemainingLifecyclePilot()
    {
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        var travel = Instance(AccessTools.TypeByName("Behaviour.Managers.TravelManager"));
        var system = SpGet(CurrentPlayer, "currentSystem");
        var currentPoi = SpGet(CurrentPlayer, "currentPointOfInterest");
        var position = (Vector2)SpGet(CurrentPlayer, "mapPosition")!;
        var map = SpGet(AccessTools.TypeByName("Source.Galaxy.GalaxyMapData"), "current")!;
        var target = ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
            .Where(p => ReferenceEquals(SpGet(p, "system"), system) && !ReferenceEquals(p, currentPoi))
            .OrderByDescending(p => Mathf.Min(((Vector2)SpGet(p, "position")!).magnitude,
                Vector2.Distance((Vector2)SpGet(p, "position")!, position))).First();
        Require((bool)SpCall(travel, "CanWeTravel", target), "Vanilla travel eligibility gate refused fixture.");
        Require((bool)SpCall(travel, "SetRouteToPOI", target), "Native in-system route refused.");
        foreach (var frame in Wait(() => TransitState, "native in-system transit")) yield return frame;
        Save("qa-in-transit", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in LoadReady("qa-in-transit")) yield return frame;
        Require(TransitState, "Transit snapshot did not restore travelling state/waypoints.");
        Passed("native-in-system-transit-save-load");

        // Controlled parked-space fixture: stop the route and use vanilla completion on live ships.
        foreach (var frame in Settle()) yield return frame;
        travel = Instance(AccessTools.TypeByName("Behaviour.Managers.TravelManager"));
        Require(SpGet(CurrentPlayer, "currentPointOfInterest") == null, "Transit arrived before parked-space setup.");
        Require((bool)SpCall(travel, "CancelTravel", new object[] { null! }), "Native route cancellation refused.");
        var gameplay = SpGet(AccessTools.TypeByName("GameplayManager"), "Instance")!;
        SpCall(SpGet(gameplay, "spaceShip")!, "CompleteTravel");
        foreach (var ship in ((IEnumerable)SpGet(gameplay, "fleetSpaceShips")!).Cast<object>()) SpCall(ship, "CompleteTravel");
        Require(SpGet(CurrentPlayer, "currentPointOfInterest") == null
            && !(bool)SpGet(SpGet(CurrentPlayer, "currentSpaceShip")!, "travelling")!
            && !((IEnumerable)SpGet(CurrentPlayer, "waypoints")!).Cast<object>().Any(), "Parked-space fixture state invalid.");
        Save("qa-empty-space", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in LoadReady("qa-empty-space")) yield return frame;
        foreach (var frame in Wait(() => SceneManager.GetSceneByName("Space").isLoaded, "empty Space scene")) yield return frame;
        Require(SpGet(CurrentPlayer, "currentPointOfInterest") == null && !TransitState, "Empty-space load became a POI/transit load.");
        Passed("controlled-empty-space-save-load");

        // Inject delayed adapter signals through its real observed iterator, driven by Unity.
        // Replacement itself uses the normal game load; this does not emulate arbitrary scene failures.
        var adapter = SpGet(Chainloader.PluginInfos[ModApi.PluginId].Instance, "_adapter")!;
        var file = AccessTools.Method(_save, "GetSaveGame").Invoke(null, new object[] { "fixture-a" })!;
        var request = SpCall(adapter, "BeginLoad", file);
        var old = _api!.CurrentSession!.Id;
        var observed = (IEnumerator)SpCall(adapter, "ObserveLoad", DelayedAdapterSignals(adapter, old).GetEnumerator());
        SpCall(adapter, "EndLoadRequest", request, null!);
        foreach (var frame in LoadReady("fixture-b")) yield return frame;
        var replacement = _api.CurrentSession!.Id;
        var start = _events.Count;
        var coroutine = StartCoroutine(observed);
        foreach (var frame in Wait(() => _delayedSignalsRan, "delayed stale adapter signals")) yield return frame;
        StopCoroutine(coroutine);
        ((IDisposable)observed).Dispose();
        Require(_delayedSignalError == null, "Delayed callback injection failed: " + _delayedSignalError);
        Require(_api.CurrentSession?.Id == replacement && _api.CurrentSession.Phase == SessionPhase.GameplayInitialized,
            "Stale signals or disposal changed replacement state.");
        Require(!_events.Skip(start).Any(e => e.Session?.Id == old || e.Kind == LifecycleEventKind.SessionStartFailed
            || e.Kind == LifecycleEventKind.PlayerReady || e.Kind == LifecycleEventKind.GameplayInitialized), "Stale signal published readiness/failure.");
        Passed("Unity-driven-stale-adapter-signals-and-disposal");
    }

    private IEnumerable<object?> DelayedAdapterSignals(object adapter, Guid old)
    {
        yield return null;
        try
        {
            Require(Equals(SpGet(adapter, "_executing"), old), "Delayed signals lack the stale attempt context.");
            SpCall(adapter, "PlayerReconstructed");
            SpCall(adapter, "LoadFailed");
        }
        catch (Exception error) { _delayedSignalError = error; }
        _delayedSignalsRan = true;
        while (true) yield return null; // Explicit disposal must precede natural completion.
    }
}
