using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using VGModAPI;

namespace VGModAPI.Qualification;

// Native travel/station qualification pilot (phased). Uses ONLY the public ITravelEvents /
// IStationEvents surfaces to ASSERT outcomes, and actual vanilla methods / Unity coroutines to
// DRIVE. It never fakes OnArrived/OnDocked and never mutates native state to fabricate a result.
// Matrix cells the supplied fixtures cannot exercise deterministically are recorded as NOT-RUN and
// the run fails only on genuine failed cases (never an unconditional PASS). This is controlled
// evidence, not owner acceptance: RuntimeQualified stays false and #12 remains open until the owner
// qualifies in-game.
public sealed partial class Plugin
{
    private bool TravelStationSelected => File.Exists(Path.Combine(_root!, "travel-station.enabled"));
    internal readonly List<string> _tsReceipt = new();
    internal readonly List<string> _tsEvents = new();
    internal long _tsSequence;
    internal List<TravelTransition>? PendingTravel;
    internal List<StationTransition>? PendingStation;

    internal void TsRecord(string id, string description, string status, string identity, string detail)
        => _tsReceipt.Add(string.Join("\t", id, description, status, identity, (detail ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")));
    private void TsEvent(object seq, Guid session, string kind, string mode, string identity, string detail)
        => _tsEvents.Add(string.Join("\t", ++_tsSequence, session, kind, mode, identity, (detail ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")));

    private IEnumerable<object?> CheckTravelStation()
    {
        if (!TravelStationSelected) yield break;
        ITravelEvents? travel = ModApi.Travel;
        IStationEvents? station = ModApi.Station;
        Require(travel != null && station != null, "Travel/Station public services not exposed.");
        Require(_api!.Capabilities.Any(c => c.Name == "native-travel" && c.Available), "native-travel capability not available.");
        Require(!travel!.IsDispatchingCallbacks && !station!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        // TravelEvents is re-read so the fresh baseline session is the one bound when cases run.

        var transitions = new List<TravelTransition>();
        var stationFacts = new List<StationTransition>();
        using (travel!.Subscribe("qualification.travel", t =>
        {
            transitions.Add(t);
            TsEvent(t.Sequence, t.SessionId, t.Kind.ToString(), t.Mode.ToString(),
                t.Origin == null ? "" : t.Origin.SystemId + ":" + t.Origin.PoiId,
                (t.ActualLocation ?? t.RequestedDestination) == null ? "" : (t.ActualLocation ?? t.RequestedDestination)!.SystemId + ":" + (t.ActualLocation ?? t.RequestedDestination)!.PoiId);
        }))
        using (station!.Subscribe("qualification.station", s =>
        {
            stationFacts.Add(s);
            TsEvent(s.Sequence, s.SessionId, s.Kind.ToString(), "",
                s.Station == null ? "" : s.Station.SystemId + ":" + s.Station.PoiId, s.GameSeconds.ToString("F2"));
        }))
        {
            PendingTravel = transitions; PendingStation = stationFacts;
            try
            {
                foreach (var frame in SpLoad("fixture-a")) yield return frame;
                foreach (var frame in Settle()) yield return frame;
                var session = _api!.CurrentSession!.Id;
                transitions.Clear(); stationFacts.Clear(); _tsSequence = 0;
                // The fresh loaded session must be bound and positioned before any case runs.
                Require(ModApi.Travel!.SessionId == session, "Travel service did not bind the fresh session.");
                Require(ModApi.Travel.CurrentLocation != null, "Travel service has no current location after load.");

                // Case A: initial placement is observed and is NOT an arrival.
                Require(transitions.Any(t => t.SessionId == session && t.Kind == TravelTransitionKind.InitialPlacement), "Initial placement not observed.");
                Require(!transitions.Any(t => t.Kind is TravelTransitionKind.Arrived or TravelTransitionKind.RouteCompleted), "Initial placement fabricated an arrival.");
                TsRecord("initial-placement-not-arrival", "First verified location is InitialPlacement, not an arrival.", "passed", session.ToString(), "");

                var driver = new TravelStationDriver(this, session);
                foreach (var step in driver.Run()) yield return step;
            }
            finally
            {
                PendingTravel = null; PendingStation = null;
            }
        }

        File.WriteAllLines(Path.Combine(_root!, "travel-station-receipt.tsv"),
            new[] { "case\tdescription\tstatus\tnativeIdentity\tdetail" }.Concat(_tsReceipt));
        File.WriteAllLines(Path.Combine(_root!, "travel-station-events.tsv"),
            new[] { "sequence\tsession\tkind\tmode\tnativeIdentity\tdetail" }.Concat(_tsEvents));
        var failed = _tsReceipt.Count(r => r.Split('\t').Length == 5 && r.Split('\t')[2] == "failed");
        var notRun = _tsReceipt.Count(r => r.Split('\t').Length == 5 && r.Split('\t')[2] == "not-run");
        var summary = new StringBuilder();
        summary.AppendLine(failed == 0 ? "PASS" : "FAIL")
            .AppendLine("Controlled native travel/station pilot. Controlled evidence only; NOT owner acceptance.")
            .AppendLine($"rows={_tsReceipt.Count} failed={failed} notRun={notRun}")
            .AppendLine("Not-run matrix cells: " + string.Join(", ", _tsReceipt
                .Where(r => r.Split('\t').Length == 5 && r.Split('\t')[2] == "not-run").Select(r => r.Split('\t')[0])))
            .AppendLine("RuntimeQualified=false; #12 open pending owner in-game qualification.");
        File.WriteAllText(Path.Combine(_root!, "travel-station.txt"), summary.ToString());
        Passed("native-travel-station");
        Require(failed == 0, "Native travel/station pilot had failing cases.");
    }
}
