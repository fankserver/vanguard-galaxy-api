using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VGModAPI;

namespace VGModAPI.Qualification;

/// <summary>
/// Pure receipt/phase evaluation for the ARCHIVED TravelJournal comparison (phase
/// <see cref="Phase"/>) and for the native dwell assertions it carries. It contains no Unity,
/// BepInEx or reflection dependency, so the legacy sidecar reader, the comparison classification and
/// the dwell rules are host regressions rather than prose.
///
/// <para>The archived plugin is an INDEPENDENT history owner. This phase reads the files it wrote by
/// itself, never its store, its API surface or any bridge, and never treats its log as ground truth:
/// the API's own facts are the ground truth, the legacy log is the compared artefact, and every
/// documented legacy divergence is recorded as its own row instead of being forced into equality.
/// The archive is never edited, rebuilt, reactivated, migrated or bridged.</para>
/// </summary>
internal static class TravelJournalReceipt
{
    /// <summary>Honest scope of the delivered phase; #12 stays open.</summary>
    internal const string Phase = "travel-journal-comparison-v1";

    internal const string BindingCase = "legacy-binding";
    internal const string InSystemCase = "in-system-arrival-compatible";
    internal const string ChainedCase = "chained-arrival-compatible";
    internal const string PrefixLeadCase = "jumpgate-prefix-lead";
    internal const string WormholeGapCase = "wormhole-transit-gap";
    internal const string StationCase = "station-interior-vs-physical";
    internal const string BlindCase = "legacy-blind-concepts";
    internal const string ContainmentCase = "journal-io-containment";
    internal const string DwellCase = "api-dwell-anchored";

    internal const string BindingDescription = "The pinned, source-attested archived TravelJournal build is installed unchanged and running: its assembly identity, its embedded source revision, its BepInEx plugin version and the sandbox-only journal configuration all match the provenance pin, and its own patches are installed. Nothing is edited, rebuilt, bridged or reactivated.";
    internal const string InSystemDescription = "COMPATIBLE PAIR: the qualified in-system route's public Arrived facts and the archived journal's own PoiArrival rows agree on POI identity, count and order. Identity comes from native guids; names are never compared.";
    internal const string ChainedDescription = "COMPATIBLE PAIR: the qualified two-hop chained route's public arrivals and the archived journal's PoiArrival rows agree on both hop guids in order.";
    internal const string PrefixLeadDescription = "DRIVEN legacy discrepancy: the archived journal records a jump-gate transit in its PREFIX, at coroutine entry. A real save taken while the native jump iterator is still running - the API has Requested and Departed for that leg and NO Arrived yet - already contains the legacy transit row, and its game time precedes the API's later Arrived. Requested-at-entry is not a completed arrival.";
    internal const string WormholeGapDescription = "DRIVEN legacy coverage gap: the archived journal hooks JumpToSystem(JumpGate) only, so a qualified native wormhole hop produces NO legacy jump-gate transit for the arrived system, while the API reports the wormhole arrival.";
    internal const string StationDescription = "DRIVEN station discrepancy: the archived journal's dock row comes from the interior scene toggle, not from the physical dock. Either it precedes the API's DockedPhysical game time, or a physical dock without interior entry produces no legacy row at all; both outcomes are recorded from the observed evidence, never assumed.";
    internal const string BlindDescription = "NON-COMPARABLE by construction: the archived journal has no session, request or cancellation concept, so the qualified early-cancel window adds no legacy row. That is recorded as legacy-blind, never as an equivalence.";
    internal const string ContainmentDescription = "Every file the archived journal created during the run lives under the audited sandbox save root and matches its own documented patterns; the audited roots are listed explicitly and no claim is made about unscanned locations.";
    internal const string DwellDescription = "NATIVE dwell assertion over the public facts of the owned window: every reported dwell is non-negative, at least one departure carries a strictly positive dwell that equals the game-time difference from its own same-session anchor within the declared tolerance, an unanchored departure reports no dwell, and no dwell is ever computed across a session replacement.";

    /// <summary>
    /// The phase passes only when EVERY one of these case identities has exactly one PASSED row: a
    /// missing, duplicated, not-run or failed required case is a phase failure. In particular a run
    /// without a compatible pair, without a driven legacy discrepancy or without a positive anchored
    /// dwell can never report PASS.
    /// </summary>
    internal static readonly string[] RequiredCases =
    {
        BindingCase, InSystemCase, ChainedCase, PrefixLeadCase, WormholeGapCase,
        StationCase, BlindCase, ContainmentCase, DwellCase
    };

    /// <summary>Cases that must classify as a genuine compatible pair (never a gap or a non-comparable row).</summary>
    internal static readonly string[] CompatiblePairCases = { InSystemCase, ChainedCase };

    /// <summary>Cases that must classify as an actually DRIVEN legacy discrepancy.</summary>
    internal static readonly string[] DrivenDiscrepancyCases = { PrefixLeadCase, WormholeGapCase, StationCase };

    // --- declared waits ---------------------------------------------------------------------

    internal const float ReadinessSeconds = 90;
    internal const float SettleSeconds = 2;
    internal const float QuiescenceSeconds = 20;
    internal const float PlacementSeconds = 30;
    /// <summary>Process time the launcher reserves for this phase (mirrors $TravelJournalBudgetSeconds).</summary>
    internal const float LauncherReservationSeconds = 900;
    /// <summary>The cross-system cases whose legacy evidence this phase compares.</summary>
    internal const int CrossSystemCases = 2;

    internal sealed class CallSitePlan
    {
        internal string Method { get; }
        internal int Invocations { get; }
        internal int Placements { get; }
        internal int Quiescences { get; }
        internal CallSitePlan(string method, int invocations, int placements = 0, int quiescences = 0)
        {
            Method = method; Invocations = invocations; Placements = placements; Quiescences = quiescences;
        }
    }

    /// <summary>
    /// Every waiting call site of the probe, per method. The phase drives NO route of its own and
    /// performs NO load: it reuses the qualified travel phases and only saves (which does not wait),
    /// so its budget is placement waits and callback-quiescence samples alone.
    /// </summary>
    internal static readonly CallSitePlan[] CallSites =
    {
        new("JournalInSystemReady", 1, placements: 1, quiescences: 1),
        new("JournalInSystemCompleted", 1, quiescences: 1),
        new("JournalCrossCaseReady", CrossSystemCases, placements: 1, quiescences: 1),
        new("JournalCrossInFlight", CrossSystemCases, quiescences: 1),
        new("JournalCrossCaseCompleted", CrossSystemCases, quiescences: 1),
        new("RecordClosingCases", 1, quiescences: 1)
    };

    internal static readonly int PlacementWaits = CallSites.Sum(site => site.Invocations * site.Placements);
    internal static readonly int QuiescenceSamples = CallSites.Sum(site => site.Invocations * site.Quiescences);

    internal sealed class PhaseWait
    {
        internal string Name { get; }
        internal float Seconds { get; }
        internal int Occurrences { get; }
        internal PhaseWait(string name, float seconds, int occurrences) { Name = name; Seconds = seconds; Occurrences = occurrences; }
    }

    internal static readonly PhaseWait[] PhaseWaits =
    {
        new("session-placement", PlacementSeconds, PlacementWaits),
        new("callback-quiescence", QuiescenceSeconds, QuiescenceSamples)
    };

    internal static readonly float PhaseBudgetSeconds = PhaseWaits.Sum(wait => wait.Seconds * wait.Occurrences);

    // --- the archived journal's own sidecar, read as a file ---------------------------------

    /// <summary>
    /// Hard read bound for one legacy sidecar. It is a REFUSAL limit, never a truncation: a file
    /// larger than this fails the case instead of being partially parsed, so no capacity or
    /// completeness claim is ever made about a document that was not read whole.
    /// </summary>
    internal const int MaxSidecarBytes = 8 * 1024 * 1024;

    /// <summary>Hard refusal limit on the number of events in one sidecar; never a silent cap.</summary>
    internal const int MaxSidecarEvents = 20000;

    /// <summary>
    /// One row of the archived journal's own event log, projected to the fields this comparison may
    /// use. Names are deliberately NOT carried: the archived plugin reads the game's lazy name
    /// getter, so its name fields are neither ground truth nor safely comparable.
    /// </summary>
    internal readonly struct LegacyEvent
    {
        /// <summary>Index in the sidecar's own `events` array, as written.</summary>
        internal int Index { get; }
        /// <summary>The `$kind` discriminator the archived converter writes.</summary>
        internal string Kind { get; }
        internal double GameSeconds { get; }
        internal string SystemGuid { get; }
        internal string PoiGuid { get; }
        internal string StationGuid { get; }
        internal LegacyEvent(int index, string kind, double gameSeconds, string systemGuid, string poiGuid, string stationGuid)
        {
            Index = index; Kind = kind; GameSeconds = gameSeconds;
            SystemGuid = systemGuid; PoiGuid = poiGuid; StationGuid = stationGuid;
        }
        internal string Describe() => "#" + Index.ToString(CultureInfo.InvariantCulture) + " " + Kind
            + "(t=" + GameSeconds.ToString("F3", CultureInfo.InvariantCulture)
            + ",system=" + SystemGuid + (PoiGuid.Length > 0 ? ",poi=" + PoiGuid : "")
            + (StationGuid.Length > 0 ? ",station=" + StationGuid : "") + ")";
    }

    internal const string JumpgateTransitKind = "JumpgateTransit";
    internal const string PoiArrivalKind = "PoiArrival";
    internal const string StationDockKind = "StationDock";
    internal const string StationUndockKind = "StationUndock";
    /// <summary>The schema version the archived writer emits; anything else is refused, never migrated.</summary>
    internal const int LegacySchemaVersion = 2;

    internal sealed class LegacySidecar
    {
        internal int Version { get; }
        internal IReadOnlyList<LegacyEvent> Events { get; }
        internal LegacySidecar(int version, IReadOnlyList<LegacyEvent> events) { Version = version; Events = events; }
    }

    /// <summary>
    /// Strict reader for the archived sidecar document. It is intentionally its own minimal parser:
    /// the probe never loads the archived assembly's types, never uses its converter and never
    /// migrates anything, and a document it cannot read whole is refused rather than guessed at.
    /// Throws <see cref="FormatException"/> with the exact reason on anything unexpected.
    /// </summary>
    internal static LegacySidecar ReadSidecar(string text)
    {
        if (text == null) throw new FormatException("The legacy sidecar text is missing.");
        if (text.Length > MaxSidecarBytes)
            throw new FormatException("The legacy sidecar exceeds the " + MaxSidecarBytes
                + " byte read bound; refusing a partial read rather than truncating it.");
        var reader = new Json(text);
        var root = reader.ReadValue();
        reader.SkipWhitespace();
        if (!reader.AtEnd) throw new FormatException("Trailing content after the legacy sidecar document.");
        if (root is not Dictionary<string, object?> document) throw new FormatException("The legacy sidecar root is not an object.");
        if (!document.TryGetValue("version", out var rawVersion) || rawVersion is not double version)
            throw new FormatException("The legacy sidecar declares no numeric version.");
        if (!document.TryGetValue("events", out var rawEvents) || rawEvents is not List<object?> events)
            throw new FormatException("The legacy sidecar declares no events array.");
        if (events.Count > MaxSidecarEvents)
            throw new FormatException("The legacy sidecar holds " + events.Count + " events, beyond the "
                + MaxSidecarEvents + " read bound; refusing rather than reading a prefix of it.");
        var rows = new List<LegacyEvent>(events.Count);
        for (int index = 0; index < events.Count; index++)
        {
            if (events[index] is not Dictionary<string, object?> row)
                throw new FormatException("Legacy event #" + index + " is not an object.");
            rows.Add(new LegacyEvent(index,
                Text(row, "$kind", index, required: true),
                Number(row, "gameSeconds", index),
                Text(row, "systemGuid", index, required: false),
                Text(row, "poiGuid", index, required: false),
                Text(row, "stationGuid", index, required: false)));
        }
        return new LegacySidecar((int)version, rows);
    }

    private static string Text(IReadOnlyDictionary<string, object?> row, string key, int index, bool required)
    {
        if (!row.TryGetValue(key, out var value) || value == null)
        {
            if (required) throw new FormatException("Legacy event #" + index + " has no '" + key + "'.");
            return string.Empty;
        }
        return value as string ?? throw new FormatException("Legacy event #" + index + " has a non-string '" + key + "'.");
    }

    private static double Number(IReadOnlyDictionary<string, object?> row, string key, int index)
        => row.TryGetValue(key, out var value) && value is double number
            ? number
            : throw new FormatException("Legacy event #" + index + " has no numeric '" + key + "'.");

    /// <summary>Minimal strict JSON scanner; objects, arrays, strings, numbers, true/false/null only.</summary>
    private sealed class Json
    {
        private readonly string _text;
        private int _at;
        internal Json(string text) { _text = text; }
        internal bool AtEnd => _at >= _text.Length;
        internal void SkipWhitespace() { while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++; }
        internal object? ReadValue()
        {
            SkipWhitespace();
            if (AtEnd) throw new FormatException("Unexpected end of the legacy sidecar document.");
            char c = _text[_at];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': Literal("true"); return true;
                case 'f': Literal("false"); return false;
                case 'n': Literal("null"); return null;
                default: return ReadNumber();
            }
        }
        private Dictionary<string, object?> ReadObject()
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _at++; SkipWhitespace();
            if (!AtEnd && _text[_at] == '}') { _at++; return result; }
            while (true)
            {
                SkipWhitespace();
                var key = ReadString();
                SkipWhitespace();
                Expect(':');
                result[key] = ReadValue();
                SkipWhitespace();
                if (AtEnd) throw new FormatException("Unterminated object in the legacy sidecar.");
                if (_text[_at] == ',') { _at++; continue; }
                Expect('}');
                return result;
            }
        }
        private List<object?> ReadArray()
        {
            var result = new List<object?>();
            _at++; SkipWhitespace();
            if (!AtEnd && _text[_at] == ']') { _at++; return result; }
            while (true)
            {
                result.Add(ReadValue());
                SkipWhitespace();
                if (AtEnd) throw new FormatException("Unterminated array in the legacy sidecar.");
                if (_text[_at] == ',') { _at++; continue; }
                Expect(']');
                return result;
            }
        }
        private string ReadString()
        {
            Expect('"');
            var text = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw new FormatException("Unterminated string in the legacy sidecar.");
                char c = _text[_at++];
                if (c == '"') return text.ToString();
                if (c != '\\') { text.Append(c); continue; }
                if (AtEnd) throw new FormatException("Unterminated escape in the legacy sidecar.");
                char escape = _text[_at++];
                switch (escape)
                {
                    case '"': text.Append('"'); break;
                    case '\\': text.Append('\\'); break;
                    case '/': text.Append('/'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'u':
                        if (_at + 4 > _text.Length) throw new FormatException("Truncated \\u escape in the legacy sidecar.");
                        text.Append((char)ushort.Parse(_text.Substring(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _at += 4;
                        break;
                    default: throw new FormatException("Unsupported escape '\\" + escape + "' in the legacy sidecar.");
                }
            }
        }
        private double ReadNumber()
        {
            int start = _at;
            while (!AtEnd && (char.IsDigit(_text[_at]) || "+-.eE".IndexOf(_text[_at]) >= 0)) _at++;
            var span = _text.Substring(start, _at - start);
            return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : throw new FormatException("Unparsable number '" + span + "' in the legacy sidecar.");
        }
        private void Literal(string literal)
        {
            if (_at + literal.Length > _text.Length || string.CompareOrdinal(_text, _at, literal, 0, literal.Length) != 0)
                throw new FormatException("Unexpected token in the legacy sidecar.");
            _at += literal.Length;
        }
        private void Expect(char expected)
        {
            SkipWhitespace();
            if (AtEnd || _text[_at] != expected) throw new FormatException("Expected '" + expected + "' in the legacy sidecar.");
            _at++;
        }
    }

    // --- comparison rules --------------------------------------------------------------------

    /// <summary>
    /// Legacy rows a window added, addressed by their own append offset relative to the baseline
    /// captured for that window. No global counter monotonicity is assumed across the archived
    /// store's load-time reset: the baseline is re-read per window.
    /// </summary>
    internal static IReadOnlyList<LegacyEvent> Appended(LegacySidecar sidecar, int baselineCount)
        => sidecar.Events.Where(row => row.Index >= baselineCount).ToArray();

    /// <summary>The baseline must be a prefix of the compared document; a shrunken log is refused.</summary>
    internal static string? CheckAppendBaseline(LegacySidecar sidecar, int baselineCount)
    {
        if (sidecar.Version != LegacySchemaVersion)
            return "The legacy sidecar declares schema version " + sidecar.Version + " instead of " + LegacySchemaVersion + ".";
        if (baselineCount < 0) return "A negative legacy baseline offset is not a valid window.";
        if (sidecar.Events.Count < baselineCount)
            return "The legacy log shrank from " + baselineCount + " to " + sidecar.Events.Count
                + " rows; the append offsets of this window cannot be trusted (FIFO eviction or a replaced store).";
        return null;
    }

    /// <summary>
    /// COMPATIBLE PAIR rule: the public arrivals of the window and the legacy POI rows it appended
    /// agree on native POI guid, count and order. Identity only - never names, never times.
    /// </summary>
    internal static string? CheckArrivalPair(IReadOnlyList<string> apiPoiGuids, IReadOnlyList<LegacyEvent> appended)
    {
        if (apiPoiGuids.Count == 0) return "The window carried no public in-system arrival, so a compatible pair would be vacuous.";
        var legacy = appended.Where(row => row.Kind == PoiArrivalKind).ToArray();
        if (legacy.Length != apiPoiGuids.Count)
            return "The archived journal appended " + legacy.Length + " PoiArrival row(s) for " + apiPoiGuids.Count
                + " public arrival(s): [" + string.Join("; ", appended.Select(row => row.Describe())) + "].";
        for (int index = 0; index < legacy.Length; index++)
            if (legacy[index].PoiGuid != apiPoiGuids[index])
                return "Arrival " + index + " differs: public POI " + apiPoiGuids[index]
                    + " vs legacy " + legacy[index].Describe() + ".";
        return null;
    }

    /// <summary>
    /// DRIVEN prefix-lead rule. The save was taken while the native jump iterator was running: the
    /// API had the leg's Requested and Departed and NO Arrived, yet the legacy log already carried
    /// the transit for the destination system. The later API arrival time must exceed the legacy
    /// row's own game time, so the lead is proven by two independent facts and never by source alone.
    /// </summary>
    internal static string? CheckPrefixLead(IReadOnlyList<LegacyEvent> appendedInFlight, string destinationSystemGuid,
        bool apiRequestedObserved, bool apiDepartedObserved, bool apiArrivedObservedAtSave,
        double? apiArrivedGameSecondsLater)
    {
        if (!apiRequestedObserved || !apiDepartedObserved)
            return "The in-flight window did not carry the public Requested and Departed facts of the jump leg.";
        if (apiArrivedObservedAtSave)
            return "The public arrival was already observed when the in-flight save was taken, so no lead is proven.";
        var transits = appendedInFlight.Where(row => row.Kind == JumpgateTransitKind
            && row.SystemGuid == destinationSystemGuid).ToArray();
        if (transits.Length == 0)
            return "The archived journal recorded no jump-gate transit for " + destinationSystemGuid
                + " while the jump was still running: [" + string.Join("; ", appendedInFlight.Select(row => row.Describe())) + "].";
        if (apiArrivedGameSecondsLater is not { } arrivedAt)
            return "The public arrival that must follow the legacy transit was never observed.";
        var earliest = transits.Min(row => row.GameSeconds);
        if (!(earliest < arrivedAt))
            return "The legacy transit game time " + earliest.ToString("F3", CultureInfo.InvariantCulture)
                + " does not precede the public arrival time " + arrivedAt.ToString("F3", CultureInfo.InvariantCulture) + ".";
        return null;
    }

    /// <summary>
    /// DRIVEN gap rule: a qualified wormhole hop produced a public arrival in the destination system
    /// while the archived journal, which hooks the jump-gate routine only, appended no transit for it.
    /// </summary>
    internal static string? CheckWormholeGap(IReadOnlyList<LegacyEvent> appended, string destinationSystemGuid,
        bool apiWormholeArrivalObserved)
    {
        if (!apiWormholeArrivalObserved)
            return "No public wormhole arrival was observed, so the legacy gap would be vacuous.";
        var transits = appended.Where(row => row.Kind == JumpgateTransitKind).ToArray();
        if (transits.Any(row => row.SystemGuid == destinationSystemGuid))
            return "The archived journal DID record a jump-gate transit for the wormhole destination "
                + destinationSystemGuid + "; the documented gap does not reproduce.";
        return null;
    }

    /// <summary>How the station dock comparison actually resolved at runtime.</summary>
    internal enum StationOutcome { InteriorPrecedesPhysical, NoInteriorNoLegacyRow }

    /// <summary>
    /// DRIVEN station rule. The archived dock row is the interior scene toggle, not the physical
    /// dock, so either it exists and its game time is at or before the public DockedPhysical time,
    /// or no interior was entered and the legacy log has no dock row for that station at all. A
    /// legacy dock row recorded AFTER the physical dock contradicts the documented shape and fails.
    /// </summary>
    internal static string? CheckStationDock(IReadOnlyList<LegacyEvent> appended, string stationGuid,
        double physicalDockGameSeconds, out StationOutcome outcome)
    {
        outcome = StationOutcome.NoInteriorNoLegacyRow;
        var docks = appended.Where(row => row.Kind == StationDockKind && row.StationGuid == stationGuid).ToArray();
        if (docks.Length == 0) return null;
        if (docks.Length > 1)
            return "The archived journal appended " + docks.Length + " dock rows for one physical dock: ["
                + string.Join("; ", docks.Select(row => row.Describe())) + "].";
        outcome = StationOutcome.InteriorPrecedesPhysical;
        if (docks[0].GameSeconds > physicalDockGameSeconds)
            return "The legacy interior-toggle dock row " + docks[0].Describe() + " is later than the public physical dock at "
                + physicalDockGameSeconds.ToString("F3", CultureInfo.InvariantCulture)
                + ", which contradicts the documented interior-before-physical shape.";
        return null;
    }

    /// <summary>
    /// NON-COMPARABLE rule: the archived journal has no request, cancellation or session concept, so
    /// a qualified cancel window must append nothing at all. Any appended row there is a real finding.
    /// </summary>
    internal static string? CheckLegacyBlind(IReadOnlyList<LegacyEvent> appended, bool apiCancellationObserved)
    {
        if (!apiCancellationObserved)
            return "No public cancellation was observed, so the legacy-blind claim would be vacuous.";
        if (appended.Count != 0)
            return "The archived journal appended " + appended.Count + " row(s) for a cancelled route: ["
                + string.Join("; ", appended.Select(row => row.Describe())) + "].";
        return null;
    }

    /// <summary>
    /// Containment rule: every file the archived journal created must live under an audited root and
    /// match one of its own documented patterns. Anything else fails BEFORE it can be explained away.
    /// </summary>
    internal static readonly string[] LegacyFilePatterns = { ".save.vgtraveljournal.json", ".vgtraveljournal.corrupt.", ".vgtraveljournal.json.tmp" };

    internal static string? CheckLegacyFile(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "An empty journal file path cannot be audited.";
        if (relativePath.IndexOf("..", StringComparison.Ordinal) >= 0) return "Refusing a traversing journal path: " + relativePath;
        foreach (var pattern in LegacyFilePatterns)
            if (relativePath.IndexOf(pattern, StringComparison.Ordinal) >= 0) return null;
        return "Unexpected file created beside the sandbox saves: " + relativePath;
    }

    // --- native dwell rules ------------------------------------------------------------------

    /// <summary>
    /// Tolerance for comparing a reported dwell against the game-time difference of its own anchor.
    /// Both values come from the SAME game clock and are captured at the same boundaries, so the
    /// only expected difference is double rounding through the receipt's own formatting; a
    /// millisecond is generous for that and still far below any real dwell.
    /// </summary>
    internal const double DwellToleranceSeconds = 0.001;

    /// <summary>
    /// Native dwell assertion over the public facts of one session window. Every reported dwell must
    /// be non-negative; a departure that follows a placement or arrival anchor in the same session
    /// must report exactly that game-time difference; a departure with no anchor in its session must
    /// report no dwell; and at least one strictly positive anchored dwell must exist, so the rule
    /// cannot be satisfied by zeros alone.
    /// </summary>
    internal static string? CheckDwellAnchoring(IReadOnlyList<TravelTransition> facts, out int anchoredPairs, out double largestDwell)
    {
        anchoredPairs = 0;
        largestDwell = 0;
        var anchors = new Dictionary<Guid, double>();
        var anchored = new Dictionary<Guid, bool>();
        foreach (var fact in facts.OrderBy(fact => fact.Sequence))
        {
            switch (fact.Kind)
            {
                case TravelTransitionKind.InitialPlacement:
                case TravelTransitionKind.RecoveredPlacement:
                case TravelTransitionKind.Arrived:
                    anchors[fact.SessionId] = fact.GameSeconds;
                    anchored[fact.SessionId] = true;
                    break;
                case TravelTransitionKind.Departed:
                    if (fact.DwellSeconds is { } dwell)
                    {
                        if (dwell < 0)
                            return "A departure reported a negative dwell: " + TravelStationReceipt.Describe(fact) + ".";
                        if (!anchored.TryGetValue(fact.SessionId, out bool hasAnchor) || !hasAnchor)
                            return "A departure reported a dwell with no anchor in its own session: "
                                + TravelStationReceipt.Describe(fact) + ".";
                        double expected = fact.GameSeconds - anchors[fact.SessionId];
                        if (Math.Abs(expected - dwell) > DwellToleranceSeconds)
                            return "A departure reported dwell " + dwell.ToString("F3", CultureInfo.InvariantCulture)
                                + " instead of its own anchor difference " + expected.ToString("F3", CultureInfo.InvariantCulture)
                                + ": " + TravelStationReceipt.Describe(fact) + ".";
                        anchoredPairs++;
                        if (dwell > largestDwell) largestDwell = dwell;
                    }
                    else if (anchored.TryGetValue(fact.SessionId, out bool present) && present)
                    {
                        return "A departure with a live same-session anchor reported no dwell: "
                            + TravelStationReceipt.Describe(fact) + ".";
                    }
                    // A departure whose session has no anchor legitimately reports no dwell.
                    anchors.Remove(fact.SessionId);
                    anchored[fact.SessionId] = false;
                    break;
            }
        }
        if (anchoredPairs == 0) return "No anchored departure was observed, so the dwell claim would be vacuous.";
        if (largestDwell <= 0)
            return "Every anchored dwell was zero; a strictly positive anchored dwell is required, not just a non-negative one.";
        return null;
    }

    // --- receipt plumbing ---------------------------------------------------------------------

    /// <summary>
    /// Evidence of a case is its public fact references PLUS the legacy row indices it compared, so
    /// a fabricated summary without either can never validate.
    /// </summary>
    internal static string LegacyIndices(IEnumerable<LegacyEvent> events)
        => "legacy:" + string.Join(",", events.Select(row => row.Index.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Cases that must carry a legacy index reference in their detail.</summary>
    internal static readonly string[] LegacyIndexedCases = { InSystemCase, ChainedCase, PrefixLeadCase };

    internal static string? CheckEvidence(IReadOnlyList<TravelStationReceipt.Row> rows, IReadOnlyList<string> eventRows)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in eventRows)
        {
            var columns = row.Split('\t');
            if (columns.Length != TravelStationReceipt.EventsHeader.Split('\t').Length) return "Malformed event row: " + row;
            observed.Add(columns[1] + ":" + columns[0] + ":" + columns[3]);
        }
        foreach (var row in rows.Where(candidate => candidate.Status == TravelStationReceipt.Passed))
        {
            var references = TravelStationReceipt.EvidenceReferences(row.Evidence);
            if (references.Count == 0)
            {
                if (RequiredCases.Contains(row.Case)) return "Required case has no observed public events: " + row.Case + ".";
                continue;
            }
            foreach (var reference in references)
                if (!observed.Contains(reference.Key + ":" + reference.Value + ":" + row.Session))
                    return "Case " + row.Case + " references an event that is not in the trace for its session: "
                        + reference.Key + ":" + reference.Value + ".";
        }
        foreach (var indexed in LegacyIndexedCases)
        {
            var match = rows.FirstOrDefault(row => row.Case == indexed && row.Status == TravelStationReceipt.Passed);
            if (match != null && match.Detail.IndexOf("legacy:", StringComparison.Ordinal) < 0)
                return "Case " + indexed + " passed without naming the legacy rows it compared.";
        }
        return null;
    }

    internal static string? Evaluate(IReadOnlyList<TravelStationReceipt.Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        if (rows.Count == 0) return "No case rows were recorded; empty coverage is not a pass.";
        var unknown = rows.FirstOrDefault(row => row.Status != TravelStationReceipt.Passed
            && row.Status != TravelStationReceipt.Failed && row.Status != TravelStationReceipt.NotRun);
        if (unknown != null) return "Unknown case status '" + unknown.Status + "' for " + unknown.Case + ".";
        var failed = rows.Where(row => row.Status == TravelStationReceipt.Failed).Select(row => row.Case).ToArray();
        if (failed.Length > 0) return "Failed cases: " + string.Join(", ", failed) + ".";
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            if (matches.Length == 0) return "Required case did not run: " + required + ".";
            if (matches.Length > 1) return "Required case recorded " + matches.Length + " rows: " + required + ".";
            if (matches[0].Status != TravelStationReceipt.Passed) return "Required case is " + matches[0].Status + ": " + required + ".";
        }
        var evidence = CheckEvidence(rows, eventRows);
        if (evidence != null) return evidence;
        if (!string.IsNullOrEmpty(fault)) return "Pilot fault: " + TravelStationReceipt.Clean(fault);
        return null;
    }

    internal static string SummarizeIncomplete(IReadOnlyList<TravelStationReceipt.Row> rows, string activeCase)
    {
        var text = new StringBuilder();
        text.AppendLine(TravelStationReceipt.Incomplete)
            .AppendLine("phase=" + Phase)
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("compatible-pairs=" + string.Join(",", CompatiblePairCases))
            .AppendLine("driven-discrepancies=" + string.Join(",", DrivenDiscrepancyCases))
            .AppendLine("activeCase=" + TravelStationReceipt.Clean(activeCase))
            .AppendLine("rows=" + rows.Count + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun))
            .AppendLine("result=pilot still running or externally terminated; this is not a pass.");
        return text.ToString();
    }

    internal static string Summarize(IReadOnlyList<TravelStationReceipt.Row> rows, string? fault, IReadOnlyList<string> eventRows)
    {
        var failure = Evaluate(rows, fault, eventRows);
        var text = new StringBuilder();
        text.AppendLine(failure == null ? "PASS" : "FAIL")
            .AppendLine("phase=" + Phase)
            .AppendLine("budgetSeconds=" + PhaseBudgetSeconds.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine("required=" + string.Join(",", RequiredCases))
            .AppendLine("compatible-pairs=" + string.Join(",", CompatiblePairCases))
            .AppendLine("driven-discrepancies=" + string.Join(",", DrivenDiscrepancyCases))
            .AppendLine("rows=" + rows.Count
                + " passed=" + rows.Count(row => row.Status == TravelStationReceipt.Passed)
                + " failed=" + rows.Count(row => row.Status == TravelStationReceipt.Failed)
                + " notRun=" + rows.Count(row => row.Status == TravelStationReceipt.NotRun));
        foreach (var required in RequiredCases)
        {
            var matches = rows.Where(row => row.Case == required).ToArray();
            text.AppendLine("required-case " + required + "=" + (matches.Length == 1 ? matches[0].Status : matches.Length == 0 ? "absent" : "duplicated"));
        }
        text.AppendLine("fault=" + (string.IsNullOrEmpty(fault) ? "none" : TravelStationReceipt.Clean(fault)));
        text.AppendLine("result=" + (failure ?? "phase satisfied"));
        text.AppendLine("The archived TravelJournal is an INDEPENDENT history owner, read only through the files it wrote itself; it is never edited, rebuilt, reactivated, migrated or bridged, its log is never ground truth, and its name fields are never compared because it reads the game's lazy name generator and is therefore not a passive observer.");
        text.AppendLine("RuntimeQualified=false; #12 stays open: recovered placement, post-gate chain continuation and the tutorial/teleport coverage decisions are NOT closed by this phase.");
        return text.ToString();
    }
}
