using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

// Native travel/station qualification pilot, phase travel-in-system-station-v1.
//
// It ASSERTS only through the public ITravelEvents / IStationEvents surfaces and DRIVES only
// actual vanilla entry points and Unity coroutines: it never invokes adapter callbacks, never
// teleports the ship and never fabricates an arrival, dock or undock. Every case owns a slice of
// the observed facts (an offset captured immediately before the case drives anything), so no
// earlier case's facts can satisfy a later wait or assertion.
//
// The phase passes only when every required case identity passed. Missing, not-run or failed
// required cases are a FAIL, and the receipt/event/diagnostic files are written on every path,
// including an exception. This is controlled evidence, not owner acceptance: RuntimeQualified
// stays false and #12 stays open until the owner qualifies in-game.
public sealed partial class Plugin
{
    private bool TravelStationSelected => File.Exists(Path.Combine(_root!, "travel-station.enabled"));
    // The phase runs exactly once. The actual-consumer probe owns its ordering when selected (it
    // has to observe these facts before the mission pilot disposes the consumer's observer), so the
    // later call site becomes a no-op instead of duplicating the phase and its receipt rows.
    private bool _travelStationPending = true;
    internal readonly List<TravelStationReceipt.Row> _tsRows = new();
    internal readonly List<string> _tsEvents = new();
    internal List<TravelTransition>? PendingTravel;
    internal List<StationTransition>? PendingStation;
    // The fixture load itself belongs to the initial-placement case, so its facts are traced under
    // that case identity instead of a startup label the receipt never mentions.
    internal const string InitialPlacementCase = "initial-placement";
    internal const string InitialPlacementDescription = "The fresh session's first public travel fact is InitialPlacement at the actual native location, with no fabricated arrival.";
    internal string _tsCase = InitialPlacementCase;
    internal string _tsDescription = InitialPlacementDescription;

    // The active label is only the observation context of the event trace; case rows always carry
    // their own identity, and the label is reset between cases so no optional cell can claim a
    // mandatory case's facts.
    internal void TsCase(string id, string description) { _tsCase = id; _tsDescription = description; }
    internal void TsEndCase() => TsCase(TravelStationReceipt.NoActiveCase, "No case is driving.");
    internal void TsRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
        => _tsRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));

    // Incremental, atomic checkpoint after every case: an external termination (a launcher kill or
    // timeout) can then only leave INCOMPLETE evidence behind, never a stale PASS and never an
    // empty directory.
    internal void TsCheckpoint()
    {
        WriteAtomic("travel-station-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_tsRows.Select(r => r.ToTsv())));
        WriteAtomic("travel-station-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_tsEvents));
        WriteAtomic("travel-station.txt", new[] { TravelStationReceipt.SummarizeIncomplete(_tsRows, _tsCase) });
    }

    private void WriteAtomic(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(_root!, name);
        var temp = path + ".tmp";
        File.WriteAllLines(temp, lines);
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    private IEnumerable<object?> CheckTravelStation()
    {
        if (!TravelStationSelected || !_travelStationPending) yield break;
        _travelStationPending = false;
        var run = RunTravelStation().GetEnumerator();
        string? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
            // Iterators cannot catch around a yield, so the pilot body is stepped explicitly: a
            // fault is attributed to the case that was running and never loses its diagnostics.
            try
            {
                moved = run.MoveNext();
                if (moved) current = run.Current;
            }
            catch (Exception error) { fault = error.ToString(); break; }
            if (!moved) break;
            yield return current;
        }
        run.Dispose();
        if (fault != null)
        {
            TsRecord(_tsCase, _tsDescription, TravelStationReceipt.Failed, "", _api?.CurrentSession?.Id, null, "",
                fault.Split('\n')[0].Trim());
        }
        WriteAtomic("travel-station-receipt.tsv",
            new[] { TravelStationReceipt.ReceiptHeader }.Concat(_tsRows.Select(r => r.ToTsv())));
        WriteAtomic("travel-station-events.tsv",
            new[] { TravelStationReceipt.EventsHeader }.Concat(_tsEvents));
        var failure = TravelStationReceipt.Evaluate(_tsRows, fault, _tsEvents);
        WriteAtomic("travel-station.txt", new[] { TravelStationReceipt.Summarize(_tsRows, fault, _tsEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "travel-station-fault.txt"), fault);
        Require(failure == null, "Native travel/station phase " + TravelStationReceipt.Phase + " failed: " + failure);
        Passed("native-travel-station-" + TravelStationReceipt.Phase);
    }

    private IEnumerable<object?> RunTravelStation()
    {
        var travel = ModApi.Travel;
        var station = ModApi.Station;
        Require(travel != null && station != null, "Travel/Station public services not exposed.");
        Require(_api!.Capabilities.Any(c => c.Name == "native-travel" && c.Available), "native-travel capability not available.");
        Require(!travel!.IsDispatchingCallbacks && !station!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        // The published phase budget is summed from the declared deadlines, and the two shared
        // harness waits are part of that sum: refuse to run if they no longer agree.
        Require(TravelStationReceipt.ReadinessSeconds == WaitDeadlineSeconds && TravelStationReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(TravelStationReceipt.PhaseBudgetSeconds <= TravelStationReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        var transitions = new List<TravelTransition>();
        var stationFacts = new List<StationTransition>();
        using (travel!.Subscribe("qualification.travel", t =>
        {
            transitions.Add(t);
            _tsEvents.Add(TravelStationReceipt.TravelEventRow(_tsCase, t));
        }))
        using (station!.Subscribe("qualification.station", s =>
        {
            stationFacts.Add(s);
            _tsEvents.Add(TravelStationReceipt.StationEventRow(_tsCase, s));
        }))
        {
            PendingTravel = transitions;
            PendingStation = stationFacts;
            try
            {
                // Cleared BEFORE the fixture load so the fresh session's own InitialPlacement is
                // inside the observed window; clearing after the load would erase the very fact the
                // first case asserts.
                TsCase(InitialPlacementCase, InitialPlacementDescription);
                transitions.Clear();
                stationFacts.Clear();
                TsCheckpoint();
                foreach (var frame in SpLoad("fixture-a")) yield return frame;
                foreach (var frame in Settle()) yield return frame;
                var session = _api!.CurrentSession!.Id;
                // Service binding and actual native readiness, not GameplayInitialized alone:
                // the travel service must own this session and the live local manager must be the
                // initialized manager of the player's actual current POI.
                foreach (var frame in Wait(() => ModApi.Travel?.SessionId == session
                    && ModApi.Travel.CurrentLocation != null && NativeTravelReady(), "travel service binding and native POI readiness")) yield return frame;
                // Optional actual-consumer observation boundary. It drives nothing and is inert
                // unless the consumer probe owns a live subscription.
                foreach (var frame in AnimaTravelInSystemReady(session)) yield return frame;
                var driver = new TravelStationDriver(this, session);
                foreach (var step in driver.Run()) yield return step;
                foreach (var frame in AnimaTravelInSystemCompleted()) yield return frame;
            }
            finally
            {
                PendingTravel = null;
                PendingStation = null;
            }
        }
    }

    // Authoritative safe in-system travel targets, shared by every travel phase, nearest first.
    //
    // qa-82 failed here: the previous selector excluded only stations, gates, wormholes, hidden and
    // dynamic POIs, so it picked the nearest `Source.Galaxy.POI.Combat` encounter. The native
    // hostiles there destroyed the player hull within seconds, and vanilla's own
    // SpaceShip.TryEmergencyJump -> TravelManager.TravelToClosestSpacestation started an
    // UNSOLICITED return route to the home station inside the next case's window (autosave-1 of
    // that run recorded emergencyJump=true, hull 0.1/10212 and waypoints=[home station]).
    //
    // This method only READS the native facts of each candidate; the decision itself is the pure,
    // host-tested rule TravelStationReceipt.RefuseTravelTarget. Every member read here is a plain
    // field/property, an exact native type test or a pure lookup, and the persisted
    // `guardDescriptors` list is read by COUNT only: MapPointOfInterest.activeEnemyCount /
    // totalEnemyCount are deliberately never touched, because their getters call
    // EnsureContentGenerated and would generate native content as a side effect of observation.
    internal object[] SafeInSystemTargets()
    {
        var poiType = NativeType("Source.Galaxy.MapPointOfInterest");
        var mining = NativeType("Source.Galaxy.POI.Mining");
        var salvage = NativeType("Source.Galaxy.POI.Salvage");
        var combat = NativeType("Source.Galaxy.POI.Combat");
        var station = NativeType("Source.Galaxy.POI.SpaceStation");
        var gate = NativeType("Source.Galaxy.POI.JumpGate");
        var wormhole = NativeType("Source.Galaxy.POI.Wormhole");
        var factionType = NativeType("Source.Galaxy.Faction");
        var isEnemy = TravelStationDriver.Bind(factionType, "IsEnemy", typeof(bool), factionType);
        var storyMission = TravelStationDriver.Bind(poiType, "IsStoryMissionPoi", typeof(bool));
        var playerFaction = SpGet(factionType, "player");
        var player = CurrentPlayer;
        var system = SpGet(player, "currentSystem");
        var current = SpGet(player, "currentPointOfInterest");
        var position = (Vector2)SpGet(player, "mapPosition")!;
        var map = SpGet(NativeType("Source.Galaxy.GalaxyMapData"), "current");
        if (map == null)
        {
            SafeTargetSelection = "no live galaxy map";
            return Array.Empty<object>();
        }
        var inSystem = ((IEnumerable)SpGet(map, "allPointsOfInterest")!).Cast<object>()
            .Where(poi => ReferenceEquals(SpGet(poi, "system"), system) && !ReferenceEquals(poi, current))
            .ToArray();
        var refusals = new Dictionary<string, int>(StringComparer.Ordinal);
        var targets = new List<object>();
        foreach (var poi in inSystem)
        {
            var candidate = new TravelStationReceipt.TravelTargetCandidate(
                (string)SpGet(poi, "guid")!,
                mining.IsInstanceOfType(poi) || salvage.IsInstanceOfType(poi),
                combat.IsInstanceOfType(poi),
                station.IsInstanceOfType(poi),
                gate.IsInstanceOfType(poi),
                wormhole.IsInstanceOfType(poi),
                (bool)SpGet(poi, "hidden")!,
                (bool)SpGet(poi, "isDynamicPoi")!,
                SpGet(poi, "faction") is { } faction && playerFaction != null
                    && (bool)isEnemy.Invoke(faction, new[] { playerFaction })!,
                (bool)storyMission.Invoke(poi, null)!,
                ((ICollection)SpGet(poi, "guardDescriptors")!).Count);
            var refusal = TravelStationReceipt.RefuseTravelTarget(candidate);
            if (refusal == null) targets.Add(poi);
            else refusals[refusal] = refusals.TryGetValue(refusal, out int count) ? count + 1 : 1;
        }
        var selected = targets
            .OrderBy(poi => Vector2.Distance((Vector2)SpGet(poi, "position")!, position))
            .ToArray();
        SafeTargetSelection = "inSystem=" + inSystem.Length + ", selected=" + selected.Length
            + ", refused=[" + string.Join(", ", refusals.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Key + ":" + entry.Value)) + "]";
        return selected;
    }

    private static Type NativeType(string name)
        => AccessTools.TypeByName(name) ?? throw new MissingMemberException(name, "type");

    /// <summary>Why the last <see cref="SafeInSystemTargets"/> call selected what it did; recorded with a NOT-RUN row.</summary>
    internal string SafeTargetSelection { get; private set; } = "<not selected>";

    // Read-only diagnostic for the source-grounded ways a native route can start without the pilot
    // asking for one: the emergency jump after a destroyed hull
    // (SpaceShip.TryEmergencyJump -> TravelToClosestSpacestation) and the autopilot's idle
    // activities (IdleManager, gated on GamePlayer.autoPlay). It never throws and never generates
    // content, so it can be recorded from a failing assertion.
    internal string NativeAutonomyDetail()
    {
        try
        {
            var player = SpGet(_player, "current");
            if (player == null) return "native autonomy state unavailable: no player";
            var shipData = SpGet(player, "currentSpaceShip");
            var waypoints = (ICollection)SpGet(player, "waypoints")!;
            var poi = SpGet(player, "currentPointOfInterest");
            // Singleton<T>.Current is the PURE static read; Instance would run FindAnyObjectByType
            // and write the static cache when it is null, which a diagnostic must never do.
            var manager = SpGet(NativeType("Behaviour.Managers.TravelManager"), "Current");
            string Identity(object? element) => element == null ? "<none>" : (string)SpGet(element, "guid")!;
            var detail = "emergencyJump=" + SpGet(player, "emergencyJump")
                + ",autoPlay=" + SpGet(player, "autoPlay")
                + ",hull=" + (shipData == null ? "<none>" : SpGet(shipData, "currentHullHP") + "/" + SpGet(shipData, "maxHullHP"))
                + ",shield=" + (shipData == null ? "<none>" : SpGet(shipData, "currentShieldHP") + "/" + SpGet(shipData, "maxShieldHP"))
                + ",currentPoi=" + Identity(poi)
                + ",waypoints=" + waypoints.Count;
            if (manager == null) return detail + ",travelManager=<none>";
            if (!TravelStationDriver.Alive(manager)) return detail + ",travelManager=<destroyed>";
            return detail
                + ",targetPoi=" + Identity(SpGet(manager!, "targetPoi"))
                + ",localTarget=" + Identity(SpGet(manager!, "localTarget"))
                + ",warping=" + SpGet(manager!, "isWarping")
                + ",travelActive=" + TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool));
        }
        catch (Exception error) { return "native autonomy state unavailable: " + error.GetType().Name; }
    }

    // Live, initialized local manager for the player's actual current POI, with no travel running.
    private bool NativeTravelReady()
    {
        var player = SpGet(_player, "current");
        if (player == null) return false;
        var manager = SpGet(AccessTools.TypeByName("Behaviour.Managers.TravelManager"), "Instance");
        if (!TravelStationDriver.Alive(manager)) return false;
        var local = SpGet(manager!, "localPoiManager");
        if (!TravelStationDriver.Alive(local)) return false;
        var poi = SpGet(player, "currentPointOfInterest");
        return poi != null && ReferenceEquals(SpGet(local!, "poi"), poi)
            && (bool)SpGet(local!, "initializedAndReady")!
            && !(bool)TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool))!;
    }
}
