using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal enum StoryOccurrenceState { Offered, Active, Retired }

/// <summary>Why a ledger operation was refused. A refusal changes nothing.</summary>
internal enum StoryLedgerStatus
{
    Accepted,
    UnknownOccurrence,
    /// <summary>Another provider's occurrence: content is owner-scoped, so this never mutates it.</summary>
    ForeignOwner,
    /// <summary>The transition does not follow the recorded state (for example a second terminal outcome).</summary>
    InvalidTransition,
    /// <summary>A bounded limit would be exceeded. Nothing is truncated and no progression is dropped.</summary>
    LimitExceeded
}

internal sealed class StoryOccurrenceEntry
{
    internal StoryContentId Id { get; }
    internal Guid OccurrenceId { get; }
    internal StoryOccurrenceState State { get; private set; }
    internal StoryOutcome? Outcome { get; private set; }
    internal StoryRetention Retention { get; }
    internal long Sequence { get; }
    private readonly Dictionary<string, string> _choices = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, string> Choices => _choices;

    internal StoryOccurrenceEntry(StoryContentId id, Guid occurrenceId, StoryRetention retention, long sequence,
        StoryOccurrenceState state = StoryOccurrenceState.Offered, StoryOutcome? outcome = null,
        IEnumerable<KeyValuePair<string, string>>? choices = null)
    {
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence requires its own identity.", nameof(occurrenceId));
        Id = id; OccurrenceId = occurrenceId; Retention = retention; Sequence = sequence; State = state; Outcome = outcome;
        if (choices != null) foreach (var pair in choices) _choices[pair.Key] = pair.Value;
    }

    internal void Activate() => State = StoryOccurrenceState.Active;
    internal void Retire(StoryOutcome outcome, IReadOnlyDictionary<string, string>? choices)
    {
        State = StoryOccurrenceState.Retired;
        Outcome = outcome;
        // A temporary definition keeps only the bounded idempotency record, never declared choices.
        if (Retention == StoryRetention.Campaign && choices != null)
            foreach (var pair in choices) _choices[pair.Key] = pair.Value;
    }
    internal void Forget() => _choices.Clear();
}

/// <summary>
/// Pure occurrence ledger: the API-owned state that a reload must reconstruct. It holds offered and
/// active occurrences, authoritative outcomes for campaign-retained definitions and bounded
/// idempotency tombstones for temporary ones. It stores no narrative history, no provider payloads
/// and no vanilla objects.
///
/// Repeated occurrences of one definition are separate entries with separate identity, because the
/// inspected <c>AddMissionWithLog</c> refuses a duplicate story ID while it is active or archived:
/// a second run is a later occurrence, never the same one resurrected.
/// </summary>
internal sealed class StoryLedger
{
    /// <summary>Global safety cap. It is a backstop: the per-provider quota below is what a provider may actually use.</summary>
    internal const int MaxOccurrences = 2048;
    /// <summary>
    /// Every bound provider owns this share of the ledger outright, so one provider's occurrences can
    /// never make another provider's <see cref="Offer"/> fail. It is exactly
    /// <see cref="MaxOccurrences"/> / <see cref="StoryProviderBindings.MaxProviders"/>, so the global
    /// cap can never be reached before every provider has spent its own quota, and the provider count
    /// is bounded for the same reason: a new provider is refused rather than handed a share that
    /// would have to come out of another provider's retained history.
    /// </summary>
    internal const int MaxOccurrencesPerProvider = MaxOccurrences / StoryProviderBindings.MaxProviders;
    /// <summary>
    /// Highest sequence the codec and the ledger accept. The reserved headroom means a restored
    /// ledger can never wrap: <see cref="Offer"/> refuses at the bound BEFORE mutating anything, so a
    /// capture can never fail on a sequence its own decode accepted and block every owner's saves.
    /// </summary>
    internal const long MaxSequence = long.MaxValue - MaxOccurrences;
    /// <summary>
    /// Campaign outcomes are never pruned; reaching this bound refuses instead of dropping
    /// progression. It stays BELOW <see cref="MaxOccurrencesPerProvider"/> so one definition's
    /// history cannot consume its owner's whole share before this bound is reached.
    /// </summary>
    internal const int MaxRetainedPerDefinition = 48;
    /// <summary>
    /// The idempotency horizon for TEMPORARY definitions: the newest terminal tombstones per
    /// definition are retained and older ones are pruned. A pruned occurrence reports
    /// <see cref="StoryLedgerStatus.UnknownOccurrence"/>; it is never re-offered or re-accepted,
    /// because occurrence identities are API-generated and never reused.
    /// </summary>
    internal const int TemporaryTombstoneHorizon = 32;
    internal const int MaxChoices = 16;
    internal const int MaxChoiceKeyLength = 64;
    internal const int MaxChoiceValueLength = 256;

    private readonly Dictionary<Guid, StoryOccurrenceEntry> _byOccurrence = new();
    private long _sequence;

    internal int Count => _byOccurrence.Count;

    internal IReadOnlyList<StoryOccurrenceEntry> Entries =>
        _byOccurrence.Values.OrderBy(entry => entry.Sequence).ToArray();

    internal bool TryGet(Guid occurrenceId, out StoryOccurrenceEntry entry) => _byOccurrence.TryGetValue(occurrenceId, out entry!);

    /// <summary>Records a new offered occurrence and mints its own identity. Never reuses a retired identity.</summary>
    internal StoryLedgerStatus Offer(StoryContentId id, StoryRetention retention, Guid occurrenceId, out string diagnostic)
    {
        diagnostic = "";
        if (occurrenceId == Guid.Empty) { diagnostic = "An occurrence requires its own identity."; return StoryLedgerStatus.InvalidTransition; }
        if (_byOccurrence.ContainsKey(occurrenceId)) { diagnostic = "That occurrence identity already exists."; return StoryLedgerStatus.InvalidTransition; }
        if (_byOccurrence.Values.Count(entry => entry.Id.Provider == id.Provider) >= MaxOccurrencesPerProvider)
        {
            diagnostic = "Provider '" + id.Provider + "' holds its quota of " + MaxOccurrencesPerProvider
                + " occurrences; nothing was truncated and no other provider is affected.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (_byOccurrence.Count >= MaxOccurrences)
        {
            diagnostic = "The ledger holds its maximum of " + MaxOccurrences + " occurrences; nothing was truncated.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (_sequence >= MaxSequence)
        {
            // Refused BEFORE any mutation, so the ledger stays capturable instead of overflowing.
            diagnostic = "The occurrence sequence reached its bound; refusing rather than wrapping the timeline.";
            return StoryLedgerStatus.LimitExceeded;
        }
        var candidate = new StoryOccurrenceEntry(id, occurrenceId, retention, _sequence + 1);
        if (EncodedSize() + StoryStateCodec.EncodedSize(candidate) > StoryStateCodec.MaxBytes)
        {
            diagnostic = "The persisted story state would exceed its bounded payload; refusing rather than failing a later capture.";
            return StoryLedgerStatus.LimitExceeded;
        }
        _sequence++;
        _byOccurrence.Add(occurrenceId, candidate);
        return StoryLedgerStatus.Accepted;
    }

    internal StoryLedgerStatus Activate(StoryContentId caller, Guid occurrenceId, out string diagnostic)
    {
        var status = Resolve(caller, occurrenceId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State != StoryOccurrenceState.Offered)
        {
            diagnostic = "Only an offered occurrence becomes active; this one is " + entry.State + ".";
            return StoryLedgerStatus.InvalidTransition;
        }
        entry.Activate();
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Removes an OFFERED occurrence that was never accepted. Nothing was ever reconstructed for it,
    /// so it leaves no tombstone; an active or retired occurrence is refused.
    /// </summary>
    internal StoryLedgerStatus Withdraw(StoryContentId caller, Guid occurrenceId, out string diagnostic)
    {
        var status = Resolve(caller, occurrenceId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State != StoryOccurrenceState.Offered)
        {
            diagnostic = "Only an offered occurrence can be withdrawn; this one is " + entry.State + ".";
            return StoryLedgerStatus.InvalidTransition;
        }
        _byOccurrence.Remove(occurrenceId);
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Records the terminal outcome once. A second terminal call is refused rather than rewriting an
    /// authoritative result, and a temporary definition retains only the tombstone.
    /// </summary>
    internal StoryLedgerStatus Retire(StoryContentId caller, Guid occurrenceId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices, out string diagnostic)
    {
        var status = Resolve(caller, occurrenceId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (!Enum.IsDefined(typeof(StoryOutcome), outcome)) { diagnostic = "Unknown outcome."; return StoryLedgerStatus.InvalidTransition; }
        if (entry!.State == StoryOccurrenceState.Retired)
        {
            diagnostic = "This occurrence already reported " + entry.Outcome + "; an outcome is recorded once.";
            return StoryLedgerStatus.InvalidTransition;
        }
        var refusal = CheckChoices(entry, choices);
        if (refusal != null) { diagnostic = refusal; return StoryLedgerStatus.LimitExceeded; }
        if (entry.Retention == StoryRetention.Campaign && RetainedFor(entry.Id) >= MaxRetainedPerDefinition)
        {
            diagnostic = "Definition '" + entry.Id + "' already retains " + MaxRetainedPerDefinition
                + " outcomes; refusing rather than dropping campaign progression.";
            return StoryLedgerStatus.LimitExceeded;
        }
        // A capture must never fail on state this ledger accepted: the cost of the declared choices
        // is checked against the payload bound BEFORE the outcome is recorded.
        var projected = new StoryOccurrenceEntry(entry.Id, entry.OccurrenceId, entry.Retention, entry.Sequence,
            StoryOccurrenceState.Retired, outcome, entry.Retention == StoryRetention.Campaign ? choices : null);
        if (EncodedSize() - StoryStateCodec.EncodedSize(entry) + StoryStateCodec.EncodedSize(projected) > StoryStateCodec.MaxBytes)
        {
            diagnostic = "The persisted story state would exceed its bounded payload; refusing rather than failing a later capture.";
            return StoryLedgerStatus.LimitExceeded;
        }
        entry.Retire(outcome, choices);
        if (entry.Retention == StoryRetention.Temporary) PruneTemporary(entry.Id);
        return StoryLedgerStatus.Accepted;
    }

    private string? CheckChoices(StoryOccurrenceEntry entry, IReadOnlyDictionary<string, string>? choices)
    {
        if (choices == null || choices.Count == 0) return null;
        if (entry.Retention != StoryRetention.Campaign)
            return "Declared choices are retained for campaign definitions only; '" + entry.Id + "' is temporary.";
        if (choices.Count > MaxChoices) return "At most " + MaxChoices + " declared choices per occurrence.";
        foreach (var pair in choices)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > MaxChoiceKeyLength) return "A choice key must be 1-" + MaxChoiceKeyLength + " characters.";
            if (pair.Value == null || pair.Value.Length > MaxChoiceValueLength) return "A choice value must be at most " + MaxChoiceValueLength + " characters.";
            // Refused here so the ledger never accepts text its own capture could not write.
            if (!StoryStateCodec.IsEncodable(pair.Key) || !StoryStateCodec.IsEncodable(pair.Value)) return "A declared choice must be valid text.";
        }
        return null;
    }

    /// <summary>
    /// Keeps the newest <see cref="TemporaryTombstoneHorizon"/> terminal tombstones of a temporary
    /// definition. Only TERMINAL TEMPORARY entries are pruned: offered and active occurrences are
    /// still needed to reconstruct live content, and campaign outcomes/choices are never removed.
    /// This is a bounded horizon, not a time-based purge.
    /// </summary>
    private void PruneTemporary(StoryContentId id)
    {
        var terminal = _byOccurrence.Values
            .Where(entry => entry.Id == id && entry.Retention == StoryRetention.Temporary && entry.State == StoryOccurrenceState.Retired)
            .OrderByDescending(entry => entry.Sequence)
            .Skip(TemporaryTombstoneHorizon)
            .ToArray();
        foreach (var entry in terminal) _byOccurrence.Remove(entry.OccurrenceId);
    }

    /// <summary>Exact encoded size of the current ledger, so bounds are checked in the units that actually matter.</summary>
    private int EncodedSize() => StoryStateCodec.HeaderBytes + _byOccurrence.Values.Sum(StoryStateCodec.EncodedSize);

    private int RetainedFor(StoryContentId id)
        => _byOccurrence.Values.Count(entry => entry.Id == id && entry.State == StoryOccurrenceState.Retired);

    private StoryLedgerStatus Resolve(StoryContentId caller, Guid occurrenceId, out StoryOccurrenceEntry? entry, out string diagnostic)
    {
        diagnostic = "";
        if (!_byOccurrence.TryGetValue(occurrenceId, out entry))
        {
            diagnostic = "Unknown occurrence.";
            return StoryLedgerStatus.UnknownOccurrence;
        }
        if (entry.Id.Provider != caller.Provider)
        {
            // Deliberately reports foreign ownership without revealing the other provider's local ID.
            diagnostic = "Occurrence belongs to another provider.";
            entry = null;
            return StoryLedgerStatus.ForeignOwner;
        }
        if (entry.Id != caller)
        {
            diagnostic = "Occurrence belongs to a different definition of this provider.";
            entry = null;
            return StoryLedgerStatus.ForeignOwner;
        }
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>Authoritative retained outcomes for one definition, oldest first.</summary>
    internal IReadOnlyList<StoryOccurrenceRecord> Retained(StoryContentId id)
        => _byOccurrence.Values
            .Where(entry => entry.Id == id && entry.State == StoryOccurrenceState.Retired)
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new StoryOccurrenceRecord(entry.Id, entry.OccurrenceId, entry.Outcome,
                entry.Retention == StoryRetention.Campaign ? entry.Choices.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : null))
            .ToArray();

    /// <summary>
    /// Authoritative CAMPAIGN completion. A temporary definition keeps a bounded idempotency
    /// tombstone, not an authoritative outcome, so it never answers true here.
    /// </summary>
    internal bool IsCompleted(StoryContentId id)
        => _byOccurrence.Values.Any(entry => entry.Id == id && entry.Retention == StoryRetention.Campaign
            && entry.Outcome == StoryOutcome.Completed);

    /// <summary>
    /// The bounds a persisted record set must satisfy, shared by the encoder and the decoder so the
    /// codec is symmetric: a payload can never restore a ledger that this type's own operations would
    /// have refused, and a capture can never produce one either. A violating payload is refused,
    /// which protects the owner's retained bytes rather than silently pruning them.
    /// </summary>
    internal static string? RefuseBounds(IReadOnlyList<StoryOccurrenceEntry> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        if (rows.Count > MaxOccurrences) return "Too many story occurrences: " + rows.Count + ".";
        if (StoryStateCodec.HeaderBytes + rows.Sum(StoryStateCodec.EncodedSize) > StoryStateCodec.MaxBytes)
            return "Story state exceeds its bounded payload size.";
        if (rows.Any(row => row.Sequence < 1 || row.Sequence > MaxSequence)) return "Story occurrence sequence out of range.";
        if (rows.Select(row => row.Sequence).Distinct().Count() != rows.Count) return "Story occurrence sequences must be unique.";
        if (rows.Select(row => row.OccurrenceId).Distinct().Count() != rows.Count) return "Duplicate story occurrence identity.";
        foreach (var group in rows.GroupBy(row => row.Id.Provider, StringComparer.Ordinal))
            if (group.Count() > MaxOccurrencesPerProvider) return "Provider '" + group.Key + "' exceeds its occurrence quota.";
        foreach (var group in rows.GroupBy(row => row.Id))
        {
            var retired = group.Count(row => row.State == StoryOccurrenceState.Retired);
            if (retired > MaxRetainedPerDefinition) return "Definition '" + group.Key + "' exceeds its retained outcome cap.";
            if (group.Any(row => row.Retention == StoryRetention.Temporary)
                && group.Count(row => row.Retention == StoryRetention.Temporary && row.State == StoryOccurrenceState.Retired) > TemporaryTombstoneHorizon)
                return "Definition '" + group.Key + "' exceeds its temporary tombstone horizon.";
        }
        return null;
    }

    /// <summary>Replaces the whole ledger with restored state; a load never merges a newer snapshot into an older save.</summary>
    internal void Restore(IEnumerable<StoryOccurrenceEntry> entries)
    {
        _byOccurrence.Clear();
        _sequence = 0;
        foreach (var entry in entries ?? throw new ArgumentNullException(nameof(entries)))
        {
            _byOccurrence[entry.OccurrenceId] = entry;
            if (entry.Sequence > _sequence) _sequence = entry.Sequence;
        }
    }

    internal void Reset() => Restore(Array.Empty<StoryOccurrenceEntry>());
}
