using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

// Native cross-system travel qualification pilot, phase travel-cross-system-v1.
//
// It is a SEPARATE optional phase on top of travel-in-system-station-v1: that phase keeps its own
// six required cases and keeps recording the cross-system matrix cells as optional NOT-RUN rows,
// so nothing here turns an earlier NOT-RUN into a coverage claim.
//
// Like the in-system phase it ASSERTS only through the public ITravelEvents surface and DRIVES only
// actual vanilla entry points and Unity coroutines: it never invokes adapter callbacks, never
// teleports the ship and never fabricates a jump. The two required cases are the two native
// cross-system routines (JumpToSystem and JumpToWormhole); a fixture that cannot exercise one of
// them produces a recorded NOT-RUN, which is a phase FAILURE, never an empty PASS.
public sealed partial class Plugin
{
    private bool TravelCrossSystemSelected => File.Exists(Path.Combine(_root!, "travel-cross-system.enabled"));
    // Explicit, separately selected opt-in: without this marker the phase never creates native
    // content, so a fixture world without a wormhole keeps reporting an honest mandatory NOT-RUN.
    internal bool TravelWormholeFixtureSelected => File.Exists(Path.Combine(_root!, "travel-wormhole-fixture.enabled"));
    internal readonly List<TravelStationReceipt.Row> _xsRows = new();
    internal readonly List<string> _xsEvents = new();
    // Read-only native state sampled at the moment each public fact was delivered, keyed by the
    // API sequence of that fact. It is evidence about the loaded world, never a drive.
    internal readonly Dictionary<long, TravelCrossSystemReceipt.NativeSnapshot> _xsSnapshots = new();
    internal List<TravelTransition>? PendingCrossSystemTravel;
    // The immutable native owner of the case that is currently driving, captured at that case's own
    // fresh fixture load. Observation snapshots record whether the live world is still that owner.
    internal NativeCaseOwner? CrossSystemOwner;
    internal const string CrossSystemStartCase = "phase-start";
    internal string _xsCase = CrossSystemStartCase;
    internal string _xsDescription = "The phase is preparing its first case.";

    // The active label is only the observation context of the event trace; case rows always carry
    // their own identity, and the label is reset between cases so no row can claim another case's
    // facts.
    internal void XsCase(string id, string description) { _xsCase = id; _xsDescription = description; }
    internal void XsEndCase() => XsCase(TravelStationReceipt.NoActiveCase, "No case is driving.");
    internal void XsRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
        => _xsRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));

    // Incremental, atomic checkpoint after every case: an external termination (a launcher kill or
    // timeout) can then only leave INCOMPLETE evidence behind, never a stale PASS and never an
    // empty directory.
    internal void XsCheckpoint()
    {
        WriteAtomic("travel-cross-system-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_xsRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-cross-system-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_xsEvents));
        WriteAtomic("travel-cross-system.txt", new[] { TravelCrossSystemReceipt.SummarizeIncomplete(_xsRows, _xsCase) });
    }

    private IEnumerable<object?> CheckTravelCrossSystem()
    {
        if (!TravelCrossSystemSelected) yield break;
        var run = RunTravelCrossSystem().GetEnumerator();
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
            XsRecord(_xsCase, _xsDescription, TravelStationReceipt.Failed, CrossSystemFaultPosition(), _api?.CurrentSession?.Id, null, "",
                fault.Split('\n')[0].Trim());
        }
        WriteAtomic("travel-cross-system-receipt.tsv",
            new[] { TravelStationReceipt.ReceiptHeader }.Concat(_xsRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-cross-system-events.tsv",
            new[] { TravelStationReceipt.EventsHeader }.Concat(_xsEvents));
        var failure = TravelCrossSystemReceipt.Evaluate(_xsRows, fault, _xsEvents);
        WriteAtomic("travel-cross-system.txt", new[] { TravelCrossSystemReceipt.Summarize(_xsRows, fault, _xsEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "travel-cross-system-fault.txt"), fault);
        Require(failure == null, "Native cross-system travel phase " + TravelCrossSystemReceipt.Phase + " failed: " + failure);
        Passed("native-travel-" + TravelCrossSystemReceipt.Phase);
    }

    // Preserved with a failure: where the ship actually was and whether a native jump/travel was
    // still running when the phase faulted or timed out.
    private string CrossSystemFaultPosition()
    {
        try
        {
            return CrossSystemSnapshot().ToDetail() + "; " + CrossSystemOwnership();
        }
        catch (Exception error) { return "native position unavailable: " + error.GetType().Name; }
    }

    // Identity, liveness and session of the captured owner against the live world. This is the
    // diagnostic that distinguishes "the native call refused" from "the probe held a manager a
    // later fixture load destroyed".
    internal string CrossSystemOwnership()
    {
        var owner = CrossSystemOwner;
        if (owner == null) return "owner=<none captured>";
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        return owner.Describe(_api?.CurrentSession?.Id,
            travelType == null ? null : SpGet(travelType, "Instance"), SpGet(_player, "current"), TravelStationDriver.Alive);
    }

    // True only when the live native travel manager and player are still the exact instances the
    // driving case captured, in the same session. Never throws: it is read during callbacks.
    private bool CrossSystemOwned(object? liveManager)
    {
        try
        {
            var owner = CrossSystemOwner;
            return owner != null && owner.CheckCurrent("an observation", _api?.CurrentSession?.Id,
                liveManager, SpGet(_player, "current"), TravelStationDriver.Alive) == null;
        }
        catch { return false; }
    }

    // Read-only sample of the loaded world. A loaded POI and empty space (no POI) are both
    // supported; ship positions are deliberately not read, so a direct teleport can never be
    // mistaken for a native transition.
    internal TravelCrossSystemReceipt.NativeSnapshot CrossSystemSnapshot()
    {
        var travelType = AccessTools.TypeByName("Behaviour.Managers.TravelManager");
        var manager = travelType == null ? null : SpGet(travelType, "Instance");
        if (!TravelStationDriver.Alive(manager)) return new TravelCrossSystemReceipt.NativeSnapshot(false, false, "", "<no travel manager>", false, false);
        var local = SpGet(manager!, "localPoiManager");
        var alive = TravelStationDriver.Alive(local);
        var player = SpGet(_player, "current");
        var system = player == null ? null : SpGet(player, "currentSystem");
        var poi = player == null ? null : SpGet(player, "currentPointOfInterest");
        var location = system == null ? "<no player system>"
            : TravelStationReceipt.Location((string)SpGet(system, "guid")!, poi == null ? null : (string)SpGet(poi, "guid")!);
        return new TravelCrossSystemReceipt.NativeSnapshot(
            (bool)SpGet(manager!, "usingJumpgate")!,
            (bool)TravelStationDriver.CallExact(manager!, "TravelActive", typeof(bool))!,
            alive ? local!.GetType().FullName! : "",
            location,
            alive && SpGet(local!, "poi") != null && ReferenceEquals(SpGet(local!, "poi"), poi) && (bool)SpGet(local!, "initializedAndReady")!,
            CrossSystemOwned(manager));
    }

    private IEnumerable<object?> RunTravelCrossSystem()
    {
        Require(TravelStationSelected, "The cross-system phase requires the travel/station selection that enables the native travel capability.");
        var travel = ModApi.Travel;
        Require(travel != null, "Travel public service not exposed.");
        Require(_api!.Capabilities.Any(capability => capability.Name == "native-travel" && capability.Available), "native-travel capability not available.");
        Require(!travel!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        // The published phase budget is summed from the declared deadlines, and the two shared
        // harness waits are part of that sum: refuse to run if they no longer agree.
        Require(TravelCrossSystemReceipt.ReadinessSeconds == WaitDeadlineSeconds && TravelCrossSystemReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(TravelCrossSystemReceipt.PhaseBudgetSeconds <= TravelCrossSystemReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        var transitions = new List<TravelTransition>();
        using (travel.Subscribe("qualification.travel.cross-system", fact =>
        {
            transitions.Add(fact);
            _xsEvents.Add(TravelStationReceipt.TravelEventRow(_xsCase, fact));
            // Sampled inline, in the same native dispatch, so the snapshot describes the world at
            // the exact boundary that produced the fact.
            try { _xsSnapshots[fact.Sequence] = CrossSystemSnapshot(); }
            catch (Exception error)
            {
                _xsSnapshots[fact.Sequence] = new TravelCrossSystemReceipt.NativeSnapshot(false, false, "", "<snapshot failed: " + error.GetType().Name + ">", false, false);
            }
        }))
        {
            PendingCrossSystemTravel = transitions;
            try
            {
                XsCheckpoint();
                var driver = new TravelCrossSystemDriver(this);
                foreach (var step in driver.Run()) yield return step;
            }
            finally { PendingCrossSystemTravel = null; }
        }
    }
}
