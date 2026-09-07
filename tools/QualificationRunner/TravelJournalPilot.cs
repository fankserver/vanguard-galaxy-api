using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

// Archived TravelJournal comparison, phase travel-journal-comparison-v1.
//
// The archived plugin is installed UNCHANGED and owns its own history. This phase never edits,
// rebuilds, reactivates, migrates or bridges it, never calls its IVgTravelJournal surface, never
// reflects into its store and never invokes its patches. It reads only the sidecar FILES the plugin
// wrote by itself in response to real vanilla saves the harness already performs, and it compares
// those files against the PUBLIC API facts of the same window. The API facts are the ground truth;
// the legacy log is the compared artefact.
//
// It also carries the native dwell assertions over the public facts of the same owned window.
public sealed partial class Plugin
{
    internal bool TravelJournalComparisonSelected => File.Exists(Path.Combine(_root!, "travel-journal.enabled"));
    private readonly List<TravelStationReceipt.Row> _tjRows = new();
    private readonly List<string> _tjEvents = new();
    private List<TravelTransition>? _tjFacts;
    private List<StationTransition>? _tjStationFacts;
    private string _tjCase = "phase-start";
    private string _tjDescription = "The archived-journal comparison is preparing its first case.";
    private Guid _tjWindowSession;
    private int _tjWindowOffset;
    private string? _tjOpenCase;
    private readonly List<string> _tjLegacyFiles = new();
    private readonly List<string> _tjAuditedRoots = new();
    /// <summary>Unity frame in which each public fact's callback was delivered, read on the main thread.</summary>
    private readonly Dictionary<long, int> _tjFactFrames = new();
    /// <summary>The REAL baseline of the window that is currently open; never assumed to be empty.</summary>
    private TravelJournalReceipt.LegacyBaseline? _tjBaseline;

    private void TjCase(string id, string description) { _tjCase = id; _tjDescription = description; }
    private void TjEndCase() { TjCase(TravelStationReceipt.NoActiveCase, "No archived-journal case is observing."); TjCheckpoint(); }
    private void TjRecord(string caseId, string description, string status, string nativeIdentity,
        Guid? session, Guid? operation, string evidence, string detail)
    {
        _tjRows.Add(new TravelStationReceipt.Row(caseId, description, status, nativeIdentity,
            session?.ToString() ?? "", operation?.ToString() ?? "", evidence, detail));
        TjCheckpoint();
    }

    private void TjCheckpoint()
    {
        WriteAtomic("travel-journal-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_tjRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-journal-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_tjEvents));
        WriteAtomic("travel-journal.txt", new[] { TravelJournalReceipt.SummarizeIncomplete(_tjRows, _tjCase) });
    }

    private IEnumerable<object?> CheckTravelJournalComparison()
    {
        if (!TravelJournalComparisonSelected) yield break;
        var run = RunTravelJournalComparison().GetEnumerator();
        string? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
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
            _tjRows.Add(new TravelStationReceipt.Row(_tjCase, _tjDescription, TravelStationReceipt.Failed, "",
                _api?.CurrentSession?.Id.ToString() ?? "", "", "", fault.Split('\n')[0].Trim()));
        }
        WriteAtomic("travel-journal-receipt.tsv", new[] { TravelStationReceipt.ReceiptHeader }.Concat(_tjRows.Select(row => row.ToTsv())));
        WriteAtomic("travel-journal-events.tsv", new[] { TravelStationReceipt.EventsHeader }.Concat(_tjEvents));
        var failure = TravelJournalReceipt.Evaluate(_tjRows, fault, _tjEvents);
        WriteAtomic("travel-journal.txt", new[] { TravelJournalReceipt.Summarize(_tjRows, fault, _tjEvents) });
        if (fault != null) File.WriteAllText(Path.Combine(_root!, "travel-journal-fault.txt"), fault);
        Require(failure == null, "Archived TravelJournal comparison phase " + TravelJournalReceipt.Phase + " failed: " + failure);
        Passed(TravelJournalReceipt.Phase);
    }

    private IEnumerable<object?> RunTravelJournalComparison()
    {
        Require(!AnimaTravelProbeSelected && !EchoTravelProbeSelected,
            "The archived-journal comparison owns the reused travel phases; it is refused together with a consumer travel probe at Prepare.");
        Require(TravelStationSelected && TravelCrossSystemSelected && TravelWormholeFixtureSelected,
            "The archived-journal comparison requires both qualified native travel phases and the wormhole fixture selection.");
        Require(ModApi.Travel != null, "Travel public service not exposed.");
        Require(_api!.Capabilities.Any(capability => capability.Name == "native-travel" && capability.Available), "native-travel capability not available.");
        Require(!ModApi.Travel!.IsDispatchingCallbacks, "Cannot subscribe during callback dispatch.");
        Require(TravelJournalReceipt.ReadinessSeconds == WaitDeadlineSeconds && TravelJournalReceipt.SettleSeconds == SettleSeconds,
            "Shared harness wait/settle deadlines no longer match the declared phase budget terms.");
        Require(TravelJournalReceipt.PhaseBudgetSeconds <= TravelJournalReceipt.LauncherReservationSeconds,
            "Declared phase budget exceeds the launcher reservation.");
        Require(_travelStationPending && _travelCrossSystemPending,
            "The reused native travel phases already ran; the archived-journal comparison must own their ordering.");
        // The audited roots this phase can speak about at all. Nothing outside them is claimed.
        _tjAuditedRoots.Clear();
        _tjAuditedRoots.Add(_saveRoot!);
        _tjLegacyFiles.Clear();
        _tjLegacyFiles.AddRange(LegacyFilesUnder(_saveRoot!));

        Require(ModApi.Station != null && !ModApi.Station.IsDispatchingCallbacks, "Station public service not exposed.");
        var facts = new List<TravelTransition>();
        var stationFacts = new List<StationTransition>();
        using (ModApi.Travel!.Subscribe("qualification.travel-journal", fact =>
        {
            facts.Add(fact);
            _tjFactFrames[fact.Sequence] = Time.frameCount;
            _tjEvents.Add(TravelStationReceipt.TravelEventRow(_tjCase, fact));
        }))
        using (ModApi.Station!.Subscribe("qualification.travel-journal.station", fact =>
        {
            stationFacts.Add(fact);
            _tjEvents.Add(TravelStationReceipt.StationEventRow(_tjCase, fact));
        }))
        {
            _tjFacts = facts;
            _tjStationFacts = stationFacts;
            try
            {
                TjCheckpoint();
                foreach (var frame in CheckTravelStation()) yield return frame;
                foreach (var frame in CheckTravelCrossSystem()) yield return frame;
                foreach (var frame in RecordClosingCases()) yield return frame;
            }
            finally { _tjFacts = null; _tjStationFacts = null; }
        }
    }

    // --- archived plugin reads (files and BepInEx metadata only) ------------------------------

    private const string LegacyPluginId = "vgtraveljournal";
    private const string LegacyPluginVersion = "0.2.0";
    private const string LegacyAssemblyVersion = "0.1.0.0";

    private BaseUnityPlugin LegacyPlugin
    {
        get
        {
            Require(Chainloader.PluginInfos.TryGetValue(LegacyPluginId, out var info) && info.Instance != null,
                "The archived TravelJournal plugin is not loaded in this sandbox; its startup did not run.");
            return Chainloader.PluginInfos[LegacyPluginId].Instance;
        }
    }

    /// <summary>The sidecar the archived plugin writes beside a save slot, by its own documented rule.</summary>
    private string LegacySidecarPath(string slot) => Path.Combine(_saveRoot!, slot + ".save.vgtraveljournal.json");

    private static IEnumerable<string> LegacyFilesUnder(string root)
        => Directory.Exists(root)
            ? Directory.GetFiles(root).Select(Path.GetFileName)
                .Where(name => name!.IndexOf("vgtraveljournal", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(name => name, StringComparer.Ordinal)!
            : Array.Empty<string>();

    /// <summary>
    /// Reads one legacy sidecar as a FILE, with the phase's own strict reader. The archived
    /// assembly's types, converter and store are never touched.
    /// </summary>
    private TravelJournalReceipt.LegacySidecar ReadLegacySidecar(string slot)
    {
        var path = LegacySidecarPath(slot);
        Require(File.Exists(path), "The archived journal wrote no sidecar beside the saved slot " + slot + ".");
        var info = new FileInfo(path);
        Require(info.Length <= TravelJournalReceipt.MaxSidecarBytes,
            "The legacy sidecar " + slot + " is " + info.Length + " bytes, beyond the declared read bound; refusing a partial read.");
        var sidecar = TravelJournalReceipt.ReadSidecar(File.ReadAllText(path));
        // Private evidence copy, named by the probe; the plugin's own file stays where it wrote it.
        File.Copy(path, Path.Combine(_root!, "travel-journal-" + slot + ".json"), overwrite: true);
        return sidecar;
    }

    /// <summary>
    /// Takes a real vanilla save through the harness helper; the archived plugin's own postfix writes
    /// its sidecar. The result is validated against the REAL baseline of the open window, so a fresh
    /// load's own rows can never sit inside a compared append set and a reset store is refused.
    /// </summary>
    private TravelJournalReceipt.LegacySidecar SaveAndReadLegacy(string slot)
    {
        Save(slot, LifecycleEventKind.SaveSucceeded);
        var sidecar = ReadLegacySidecar(slot);
        Require(_tjBaseline != null, "No legacy baseline was captured for this window; a zero baseline is never assumed.");
        var failure = TravelJournalReceipt.CheckAppendBaseline(sidecar, _tjBaseline!);
        Require(failure == null, failure!);
        return sidecar;
    }

    /// <summary>
    /// Captures the window's own baseline with a real save AFTER placement and quiescence, so every
    /// later comparison in that window subtracts exactly what the archived plugin had already
    /// written - including anything its load-time patches appended.
    /// </summary>
    private TravelJournalReceipt.LegacyBaseline CaptureLegacyBaseline(string slot)
    {
        Save(slot, LifecycleEventKind.SaveSucceeded);
        var sidecar = ReadLegacySidecar(slot);
        Require(sidecar.Version == TravelJournalReceipt.LegacySchemaVersion,
            "The legacy baseline sidecar declares schema version " + sidecar.Version + ".");
        var baseline = new TravelJournalReceipt.LegacyBaseline(slot, sidecar);
        _tjBaseline = baseline;
        return baseline;
    }

    // --- hooks called by the reused native travel phases ---------------------------------------

    private IEnumerable<object?> TjGuarded(string caseId, string description, IEnumerable<object?> body)
    {
        var run = body.GetEnumerator();
        Exception? fault = null;
        while (true)
        {
            object? current = null;
            bool moved;
            try
            {
                moved = run.MoveNext();
                if (moved) current = run.Current;
            }
            catch (Exception error) { fault = error; break; }
            if (!moved) break;
            yield return current;
        }
        run.Dispose();
        if (fault == null) yield break;
        TjRecord(caseId, description, TravelStationReceipt.Failed, "", _api?.CurrentSession?.Id, null, "",
            fault.Message.Split('\n')[0].Trim());
        File.WriteAllText(Path.Combine(_root!, "travel-journal-fault.txt"), fault.ToString());
    }

    internal IEnumerable<object?> TravelJournalInSystemReady(Guid session)
    {
        if (_tjFacts == null) yield break;
        foreach (var frame in TjGuarded(TravelJournalReceipt.BindingCase, TravelJournalReceipt.BindingDescription,
            JournalInSystemReady(session))) yield return frame;
    }

    private IEnumerable<object?> JournalInSystemReady(Guid session)
    {
        TjCase(TravelJournalReceipt.BindingCase, TravelJournalReceipt.BindingDescription);
        foreach (var frame in AwaitJournalPlacement(session)) yield return frame;
        foreach (var frame in JournalQuiesce()) yield return frame;
        _tjWindowSession = session;
        _tjWindowOffset = JournalSessionOffset(session);
        var legacy = LegacyPlugin;
        Require(legacy.enabled, "The archived TravelJournal plugin is disabled.");
        Require(legacy.Info.Metadata.GUID == LegacyPluginId, "Unexpected archived plugin identity.");
        Require(legacy.Info.Metadata.Version.ToString(3) == LegacyPluginVersion,
            "The archived plugin reports " + legacy.Info.Metadata.Version + ", not the pinned " + LegacyPluginVersion + ".");
        var assembly = legacy.GetType().Assembly.GetName();
        Require(assembly.Name == "VGTravelJournal" && assembly.Version!.ToString() == LegacyAssemblyVersion,
            "The archived assembly identity is " + assembly.Name + " " + assembly.Version
            + ", not the pinned VGTravelJournal " + LegacyAssemblyVersion + " (plugin version 0.2.0 is deliberately different).");
        var informational = legacy.GetType().Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault();
        var pinnedRevision = File.ReadAllText(Path.Combine(_root!, "travel-journal-revision.txt")).Trim();
        Require(informational != null && informational.InformationalVersion == "0.1.0+" + pinnedRevision,
            "The archived assembly's embedded source revision is '" + (informational?.InformationalVersion ?? "<none>")
            + "', not the pinned 0.1.0+" + pinnedRevision + ".");
        // Its own patches are installed; the probe never invokes them.
        var owned = Harmony.GetAllPatchedMethods()
            .Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(LegacyPluginId) == true)
            .Select(method => (method.DeclaringType?.FullName ?? "") + "." + method.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Require(owned.Length > 0, "The archived plugin installed no patches; its startup bindings did not resolve.");
        // The window's REAL baseline: whatever the archived plugin already wrote for this freshly
        // loaded session, including anything its load-time patches appended.
        var baseline = CaptureLegacyBaseline("qa-journal-baseline-in-system");
        var placements = JournalWindow(_tjWindowOffset).Where(fact => fact.Kind == TravelTransitionKind.InitialPlacement).ToArray();
        TjRecord(TravelJournalReceipt.BindingCase, TravelJournalReceipt.BindingDescription, TravelStationReceipt.Passed,
            "archive=" + LegacyPluginId + " " + legacy.Info.Metadata.Version, session, null,
            TravelStationReceipt.Evidence(placements, null),
            "assembly=" + assembly.Name + " " + assembly.Version + "; informational=" + informational!.InformationalVersion
            + "; ownedPatches=[" + string.Join(" ", owned) + "]; unchanged=true (no edit/rebuild/bridge/migration); "
            + baseline.Describe());
        TjCase(TravelJournalReceipt.InSystemCase, TravelJournalReceipt.InSystemDescription);
        _tjOpenCase = TravelJournalReceipt.InSystemCase;
    }

    internal IEnumerable<object?> TravelJournalInSystemCompleted()
    {
        if (_tjFacts == null || _tjOpenCase != TravelJournalReceipt.InSystemCase) yield break;
        _tjOpenCase = null;
        foreach (var frame in TjGuarded(TravelJournalReceipt.InSystemCase, TravelJournalReceipt.InSystemDescription,
            JournalInSystemCompleted())) yield return frame;
    }

    /// <summary>
    /// The whole qualified in-system phase in one window: its POI arrivals are the compatible pair,
    /// its cancellation is the legacy-blind row, and its physical dock is the station discrepancy.
    /// </summary>
    private IEnumerable<object?> JournalInSystemCompleted()
    {
        foreach (var frame in JournalQuiesce()) yield return frame;
        var window = JournalWindow(_tjWindowOffset).Where(fact => fact.SessionId == _tjWindowSession).ToArray();
        var sidecar = SaveAndReadLegacy("qa-journal-in-system");
        var appended = TravelJournalReceipt.Appended(sidecar, _tjBaseline!);

        // Compatible pair: every in-system arrival of the window, in order, by native POI guid.
        var arrivals = window.Where(fact => fact.Kind == TravelTransitionKind.Arrived && fact.Mode == TravelMode.InSystem
            && fact.ActualLocation?.PoiId != null).ToArray();
        var poiGuids = arrivals.Select(fact => fact.ActualLocation!.PoiId!).ToArray();
        var failure = TravelJournalReceipt.CheckArrivalPair(poiGuids, appended);
        Require(failure == null, failure!);
        var legacyArrivals = appended.Where(row => row.Kind == TravelJournalReceipt.PoiArrivalKind).ToArray();
        TjRecord(TravelJournalReceipt.InSystemCase, TravelJournalReceipt.InSystemDescription, TravelStationReceipt.Passed,
            "pairs=" + poiGuids.Length, _tjWindowSession, null,
            TravelStationReceipt.Evidence(arrivals, null),
            "comparison=compatible; " + TravelJournalReceipt.LegacyIndices(legacyArrivals)
            + "; apiPoiGuids=[" + string.Join(" ", poiGuids) + "]; namesNotCompared=true");

        // The chained route's two hops are the second compatible pair, taken from the same window's
        // last two arrivals in order.
        Require(poiGuids.Length >= 2, "The qualified in-system phase produced fewer than two arrivals to pair in order.");
        var chained = arrivals.Skip(arrivals.Length - 2).ToArray();
        var chainedLegacy = legacyArrivals.Skip(legacyArrivals.Length - 2).ToArray();
        failure = TravelJournalReceipt.CheckArrivalPair(
            chained.Select(fact => fact.ActualLocation!.PoiId!).ToArray(), chainedLegacy);
        Require(failure == null, failure!);
        TjRecord(TravelJournalReceipt.ChainedCase, TravelJournalReceipt.ChainedDescription, TravelStationReceipt.Passed,
            "pairs=2", _tjWindowSession, null, TravelStationReceipt.Evidence(chained, null),
            "comparison=compatible; " + TravelJournalReceipt.LegacyIndices(chainedLegacy)
            + "; orderedHops=[" + string.Join(" ", chained.Select(fact => fact.ActualLocation!.PoiId)) + "]");

        // Legacy-blind: the cancel window is bounded by its OWN two real samples, so the empty
        // append set is the cancel's own, not an inference from the whole phase.
        Require(_tjCancelBefore != null && _tjCancelAfter != null,
            "The qualified cancel case was never sampled at its own boundaries; an empty append set would prove nothing.");
        var cancelFacts = window.Where(fact => fact.Kind == TravelTransitionKind.Cancelled).ToArray();
        var cancelAppended = TravelJournalReceipt.Appended(_tjCancelAfter!, _tjCancelBefore!);
        failure = TravelJournalReceipt.CheckLegacyBlind(cancelAppended, cancelFacts.Length > 0, boundarySampled: true);
        Require(failure == null, failure!);
        TjRecord(TravelJournalReceipt.BlindCase, TravelJournalReceipt.BlindDescription, TravelStationReceipt.Passed,
            "legacyRows=0", _tjWindowSession, cancelFacts.FirstOrDefault()?.OperationId,
            TravelStationReceipt.Evidence(cancelFacts, null),
            "comparison=non-comparable; " + _tjCancelBefore!.Describe()
            + "; afterRows=" + _tjCancelAfter!.Events.Count + "; appendedAcrossCancel=0"
            + "; the archived journal has no session, request or cancellation concept, so this is legacy-blind, not an equivalence");

        // Station: the legacy dock row is the interior toggle, compared against the PUBLIC physical
        // dock fact of the same session. The physical fact comes from the public station surface,
        // never from a driver hook or the archived plugin.
        var physical = _tjStationFacts!.FirstOrDefault(fact => fact.SessionId == _tjWindowSession
            && fact.Kind == StationTransitionKind.DockedPhysical && fact.Station?.PoiId != null);
        Require(physical != null, "The qualified in-system phase reported no public physical dock to compare against.");
        var outcome = TravelJournalReceipt.StationOutcome.NoInteriorNoLegacyRow;
        failure = TravelJournalReceipt.CheckStationDock(appended, physical!.Station!.PoiId!, physical.GameSeconds, out outcome);
        Require(failure == null, failure!);
        var dockRows = appended.Where(row => row.Kind == TravelJournalReceipt.StationDockKind).ToArray();
        TjRecord(TravelJournalReceipt.StationCase, TravelJournalReceipt.StationDescription, TravelStationReceipt.Passed,
            "station=" + physical.Station.PoiId, _tjWindowSession, null,
            TravelStationReceipt.Evidence(null, new[] { physical }),
            "comparison=" + (outcome == TravelJournalReceipt.StationOutcome.InteriorPrecedesPhysical ? "legacy-timing" : "legacy-gap")
            + "; outcome=" + outcome + "; physicalDockGameSeconds="
            + physical.GameSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + "; " + TravelJournalReceipt.LegacyIndices(dockRows));
        TjEndCase();
    }

    private TravelJournalReceipt.LegacyBaseline? _tjCancelBefore;
    private TravelJournalReceipt.LegacySidecar? _tjCancelAfter;

    /// <summary>
    /// Inert unless this phase owns a live subscription. Sampled by the qualified in-system phase
    /// immediately BEFORE it drives its cancel case and immediately AFTER the case completed, so the
    /// legacy-blind claim is bounded by the cancel's own boundaries instead of the whole phase.
    /// It performs one real save per boundary and no load.
    /// </summary>
    internal IEnumerable<object?> TravelJournalCancelBoundary(string boundary)
    {
        if (_tjFacts == null || _tjOpenCase != TravelJournalReceipt.InSystemCase) yield break;
        foreach (var frame in TjGuarded(TravelJournalReceipt.BlindCase, TravelJournalReceipt.BlindDescription,
            JournalCancelBoundary(boundary))) yield return frame;
    }

    private IEnumerable<object?> JournalCancelBoundary(string boundary)
    {
        foreach (var frame in JournalQuiesce()) yield return frame;
        if (boundary == "before")
        {
            // A boundary baseline of its own: the cancel window subtracts exactly this.
            _tjCancelBefore = CaptureLegacyBaseline("qa-journal-cancel-before");
        }
        else
        {
            Require(_tjCancelBefore != null, "The cancel window was closed without an opening sample.");
            var previous = _tjBaseline;
            _tjBaseline = _tjCancelBefore;
            _tjCancelAfter = SaveAndReadLegacy("qa-journal-cancel-after");
            // The enclosing window keeps its own baseline; the cancel sample never replaces it.
            _tjBaseline = previous;
        }
    }

    internal IEnumerable<object?> TravelJournalCrossCaseReady(string crossCase, Guid session)
    {
        if (_tjFacts == null) yield break;
        foreach (var frame in TjGuarded(JournalCaseFor(crossCase), JournalDescriptionFor(crossCase),
            JournalCrossCaseReady(crossCase, session))) yield return frame;
    }

    private IEnumerable<object?> JournalCrossCaseReady(string crossCase, Guid session)
    {
        TjCase(JournalCaseFor(crossCase), JournalDescriptionFor(crossCase));
        foreach (var frame in AwaitJournalPlacement(session)) yield return frame;
        foreach (var frame in JournalQuiesce()) yield return frame;
        _tjWindowSession = session;
        _tjWindowOffset = JournalSessionOffset(session);
        _tjInFlight = null;
        _tjInFlightCapture = null;
        // Each cross-system case loads its own fixture, so it captures its own REAL baseline.
        CaptureLegacyBaseline("qa-journal-baseline-" + crossCase);
        _tjOpenCase = crossCase;
    }

    private (IReadOnlyList<TravelJournalReceipt.LegacyEvent> Appended, string Slot)? _tjInFlight;
    private TravelJournalReceipt.InFlightCapture? _tjInFlightCapture;

    /// <summary>
    /// Called by the cross-system driver once the native jump routine is RUNNING and before the
    /// arrival is awaited. A real vanilla save here captures the archived journal's own log at a
    /// moment when the API has only Requested and Departed for the leg: that file is the driven
    /// proof that the legacy prefix records the REQUESTED destination ahead of any arrival.
    /// </summary>
    internal IEnumerable<object?> TravelJournalCrossInFlight(string crossCase)
    {
        if (_tjFacts == null || _tjOpenCase != crossCase) yield break;
        if (crossCase != TravelCrossSystemReceipt.JumpGateCase) yield break;
        foreach (var frame in TjGuarded(TravelJournalReceipt.PrefixLeadCase, TravelJournalReceipt.PrefixLeadDescription,
            JournalCrossInFlight())) yield return frame;
    }

    private IEnumerable<object?> JournalCrossInFlight()
    {
        TjCase(TravelJournalReceipt.PrefixLeadCase, TravelJournalReceipt.PrefixLeadDescription);
        foreach (var frame in JournalQuiesce()) yield return frame;
        // The driver's handoff wait is satisfied by the leg's Requested fact, so wait - bounded - for
        // the leg's own Departed while no Arrived exists and the native jump is still running. If
        // that interval never occurs, the case fails honestly rather than inventing a lead.
        bool Observed(TravelTransitionKind kind) => JournalWindow(_tjWindowOffset).Any(fact => fact.SessionId == _tjWindowSession
            && fact.Mode == TravelMode.JumpGate && fact.Kind == kind);
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + TravelJournalReceipt.InFlightDepartureSeconds;
        while (!Observed(TravelTransitionKind.Departed))
        {
            Require(!Observed(TravelTransitionKind.Arrived),
                "The jump leg arrived before its departure could be observed in flight; no prefix lead can be captured.");
            Require(Time.realtimeSinceStartup < until,
                "Timed out waiting for the jump leg's own public departure while the native jump was running.");
            yield return null;
        }
        // Everything below is captured AT the save, on the main thread, and never recomputed later.
        var capture = new TravelJournalReceipt.InFlightCapture(
            requestedObserved: Observed(TravelTransitionKind.Requested),
            departedObserved: Observed(TravelTransitionKind.Departed),
            arrivedObserved: Observed(TravelTransitionKind.Arrived),
            savedFrame: Time.frameCount,
            nativeJumpRunning: CrossSystemSnapshot().JumpIteratorRunning);
        _tjInFlightCapture = capture;
        var sidecar = SaveAndReadLegacy("qa-journal-in-flight");
        _tjInFlight = (TravelJournalReceipt.Appended(sidecar, _tjBaseline!), "qa-journal-in-flight");
    }

    internal IEnumerable<object?> TravelJournalCrossCaseCompleted(string crossCase)
    {
        if (_tjFacts == null || _tjOpenCase != crossCase) yield break;
        _tjOpenCase = null;
        foreach (var frame in TjGuarded(JournalCaseFor(crossCase), JournalDescriptionFor(crossCase),
            JournalCrossCaseCompleted(crossCase))) yield return frame;
    }

    private IEnumerable<object?> JournalCrossCaseCompleted(string crossCase)
    {
        foreach (var frame in JournalQuiesce()) yield return frame;
        var mode = crossCase == TravelCrossSystemReceipt.JumpGateCase ? TravelMode.JumpGate : TravelMode.Wormhole;
        var window = JournalWindow(_tjWindowOffset).Where(fact => fact.SessionId == _tjWindowSession).ToArray();
        var arrival = window.FirstOrDefault(fact => fact.Kind == TravelTransitionKind.Arrived && fact.Mode == mode);
        Require(arrival != null, "The reused cross-system case produced no " + mode + " arrival to compare against.");
        var destination = arrival!.ActualLocation!.SystemId;
        if (crossCase == TravelCrossSystemReceipt.JumpGateCase)
        {
            Require(_tjInFlight != null && _tjInFlightCapture != null,
                "No in-flight legacy snapshot was taken for the jump-gate case.");
            var inFlight = _tjInFlight!.Value;
            var capture = _tjInFlightCapture!.Value;
            var arrivalFrame = _tjFactFrames.TryGetValue(arrival.Sequence, out int frame) ? frame : 0;
            var failure = TravelJournalReceipt.CheckPrefixLead(inFlight.Appended, destination, capture,
                apiArrivedFrame: arrivalFrame, apiArrivedGameSecondsLater: arrival.GameSeconds);
            Require(failure == null, failure!);
            var transits = inFlight.Appended.Where(row => row.Kind == TravelJournalReceipt.JumpgateTransitKind
                && row.SystemGuid == destination).ToArray();
            TjRecord(TravelJournalReceipt.PrefixLeadCase, TravelJournalReceipt.PrefixLeadDescription, TravelStationReceipt.Passed,
                "system=" + destination, _tjWindowSession, arrival.OperationId,
                TravelStationReceipt.Evidence(window.Where(fact => fact.Mode == mode
                    && fact.Kind is TravelTransitionKind.Requested or TravelTransitionKind.Departed or TravelTransitionKind.Arrived), null),
                "comparison=legacy-prefix-requested; slot=" + inFlight.Slot + "; " + TravelJournalReceipt.LegacyIndices(transits)
                + "; legacyGameSeconds=" + TravelJournalReceipt.Exact(transits.Min(row => row.GameSeconds))
                + "; apiArrivedGameSeconds=" + TravelJournalReceipt.Exact(arrival.GameSeconds)
                + "; apiArrivedFrame=" + arrivalFrame + "; " + capture.Describe()
                + "; lead=observed event ordering (frame capture), not clock advance");
        }
        else
        {
            var sidecar = SaveAndReadLegacy("qa-journal-wormhole");
            var appended = TravelJournalReceipt.Appended(sidecar, _tjBaseline!);
            var failure = TravelJournalReceipt.CheckWormholeGap(appended, destination, apiWormholeArrivalObserved: true);
            Require(failure == null, failure!);
            TjRecord(TravelJournalReceipt.WormholeGapCase, TravelJournalReceipt.WormholeGapDescription, TravelStationReceipt.Passed,
                "system=" + destination, _tjWindowSession, arrival.OperationId,
                TravelStationReceipt.Evidence(new[] { arrival }, null),
                "comparison=legacy-gap; the archived journal hooks JumpToSystem(JumpGate) only; legacyJumpgateTransits="
                + appended.Count(row => row.Kind == TravelJournalReceipt.JumpgateTransitKind)
                + " none for the arrived system");
        }
        TjEndCase();
    }

    // --- closing cases -------------------------------------------------------------------------

    private IEnumerable<object?> RecordClosingCases()
    {
        TjCase(TravelJournalReceipt.ContainmentCase, TravelJournalReceipt.ContainmentDescription);
        foreach (var frame in JournalQuiesce()) yield return frame;
        var after = LegacyFilesUnder(_saveRoot!).ToArray();
        var created = after.Where(name => !_tjLegacyFiles.Contains(name)).ToArray();
        foreach (var name in created)
        {
            var failure = TravelJournalReceipt.CheckLegacyFile(name);
            Require(failure == null, failure!);
        }
        TjRecord(TravelJournalReceipt.ContainmentCase, TravelJournalReceipt.ContainmentDescription, TravelStationReceipt.Passed,
            "createdFiles=" + created.Length, _tjWindowSession, null,
            TravelStationReceipt.Evidence(_tjFacts!.Where(fact => fact.SessionId == _tjWindowSession
                && fact.Kind == TravelTransitionKind.InitialPlacement), null),
            "auditedRoots=[" + string.Join(" ", _tjAuditedRoots) + "]; created=[" + string.Join(" ", created)
            + "]; patterns=[" + string.Join(" ", TravelJournalReceipt.LegacyFilePatterns)
            + "]; scope=as of this phase boundary only - the archived plugin still flushes at quit, so the"
            + " launcher's post-exit audit is the final location evidence; no claim is made about locations"
            + " outside the audited roots");

        TjCase(TravelJournalReceipt.DwellCase, TravelJournalReceipt.DwellDescription);
        var failureDwell = TravelJournalReceipt.CheckDwellAnchoring(_tjFacts!, out int pairs, out double largest);
        Require(failureDwell == null, failureDwell!);
        var departures = _tjFacts!.Where(fact => fact.Kind == TravelTransitionKind.Departed && fact.DwellSeconds.HasValue).ToArray();
        // The positive pair the row publishes, with its own raw anchor and departure times.
        var departure = departures.First(fact => fact.DwellSeconds!.Value > 0);
        double anchor = departure.GameSeconds - departure.DwellSeconds!.Value;
        TjRecord(TravelJournalReceipt.DwellCase, TravelJournalReceipt.DwellDescription, TravelStationReceipt.Passed,
            "anchoredPairs=" + pairs, departure.SessionId, null,
            TravelStationReceipt.Evidence(departures.Where(fact => fact.SessionId == departure.SessionId), null),
            "largestDwellSeconds=" + TravelJournalReceipt.Exact(largest)
            + "; toleranceSeconds=" + TravelJournalReceipt.Exact(TravelJournalReceipt.DwellToleranceSeconds)
            + " (exact: the adapter subtracts the same doubles the two facts carry)"
            + "; anchorGameSeconds=" + TravelJournalReceipt.Exact(anchor)
            + "; departureGameSeconds=" + TravelJournalReceipt.Exact(departure.GameSeconds)
            + "; dwellSeconds=" + TravelJournalReceipt.Exact(departure.DwellSeconds!.Value)
            + "; legacy dwell is NOT compared: its anchors are the archived prefix, not the API's verified boundaries");
        TjEndCase();
    }

    // --- helpers --------------------------------------------------------------------------------

    private static string JournalCaseFor(string crossCase)
        => crossCase == TravelCrossSystemReceipt.JumpGateCase ? TravelJournalReceipt.PrefixLeadCase : TravelJournalReceipt.WormholeGapCase;

    private static string JournalDescriptionFor(string crossCase)
        => crossCase == TravelCrossSystemReceipt.JumpGateCase ? TravelJournalReceipt.PrefixLeadDescription : TravelJournalReceipt.WormholeGapDescription;

    private List<TravelTransition> JournalWindow(int offset) => TravelStationReceipt.Window(_tjFacts!, offset);

    private int JournalSessionOffset(Guid session)
    {
        for (int index = 0; index < _tjFacts!.Count; index++)
            if (_tjFacts[index].SessionId == session) return index;
        return _tjFacts.Count;
    }

    private IEnumerable<object?> AwaitJournalPlacement(Guid session)
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + TravelJournalReceipt.PlacementSeconds;
        while (!_tjFacts!.Any(fact => fact.SessionId == session && fact.Kind == TravelTransitionKind.InitialPlacement))
        {
            Require(_api!.CurrentSession?.Phase != SessionPhase.Failed, "Session failed while waiting for the journal window's placement fact.");
            Require(Time.realtimeSinceStartup < until, "Timed out waiting for the freshly loaded session's public placement fact.");
            yield return null;
        }
    }

    private IEnumerable<object?> JournalQuiesce()
    {
        Time.timeScale = 1;
        float until = Time.realtimeSinceStartup + TravelJournalReceipt.QuiescenceSeconds;
        int observed = _tjFacts!.Count;
        int stable = 0;
        while (stable < 3)
        {
            Require(Time.realtimeSinceStartup < until, "Timed out waiting for public travel callback quiescence.");
            yield return null;
            if (_tjFacts.Count != observed) { observed = _tjFacts.Count; stable = 0; }
            else if (ModApi.Travel?.IsDispatchingCallbacks == true) stable = 0;
            else stable++;
        }
    }
}
