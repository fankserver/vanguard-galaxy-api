using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
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

    internal void TsCase(string id, string description) { _tsCase = id; _tsDescription = description; }
    internal void TsRecord(string status, string nativeIdentity, Guid? session, Guid? operation, string detail)
        => _tsRows.Add(new TravelStationReceipt.Row(_tsCase, _tsDescription, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", detail));

    private IEnumerable<object?> CheckTravelStation()
    {
        if (!TravelStationSelected) yield break;
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
            TsRecord(TravelStationReceipt.Failed, "", _api?.CurrentSession?.Id, null,
                fault.Split('\n')[0].Trim());
        }
        File.WriteAllLines(Path.Combine(_root!, "travel-station-receipt.tsv"),
            new[] { TravelStationReceipt.ReceiptHeader }.Concat(_tsRows.Select(r => r.ToTsv())));
        File.WriteAllLines(Path.Combine(_root!, "travel-station-events.tsv"),
            new[] { TravelStationReceipt.EventsHeader }.Concat(_tsEvents));
        var failure = TravelStationReceipt.Evaluate(_tsRows, fault);
        File.WriteAllText(Path.Combine(_root!, "travel-station.txt"), TravelStationReceipt.Summarize(_tsRows, fault));
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
                foreach (var frame in SpLoad("fixture-a")) yield return frame;
                foreach (var frame in Settle()) yield return frame;
                var session = _api!.CurrentSession!.Id;
                // Service binding and actual native readiness, not GameplayInitialized alone:
                // the travel service must own this session and the live local manager must be the
                // initialized manager of the player's actual current POI.
                foreach (var frame in Wait(() => ModApi.Travel?.SessionId == session
                    && ModApi.Travel.CurrentLocation != null && NativeTravelReady(), "travel service binding and native POI readiness")) yield return frame;
                var driver = new TravelStationDriver(this, session);
                foreach (var step in driver.Run()) yield return step;
            }
            finally
            {
                PendingTravel = null;
                PendingStation = null;
            }
        }
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
