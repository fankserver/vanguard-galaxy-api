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
    /// <summary>
    /// Encoded bytes reserved for this occurrence's declared choices while it is unresolved. It is
    /// taken from the definition when the occurrence is offered and PERSISTED, so a reload recomputes
    /// exactly the same reservation without the definition having been registered yet.
    /// </summary>
    internal int ChoiceReservation { get; }
    private readonly Dictionary<string, string> _choices = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, string> Choices => _choices;

    internal StoryOccurrenceEntry(StoryContentId id, Guid occurrenceId, StoryRetention retention, long sequence,
        StoryOccurrenceState state = StoryOccurrenceState.Offered, StoryOutcome? outcome = null,
        IEnumerable<KeyValuePair<string, string>>? choices = null, int choiceReservation = 0)
    {
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence requires its own identity.", nameof(occurrenceId));
        if (choiceReservation is < 0 or > StoryMissionDefinition.MaxChoiceBytesPerOccurrence)
            throw new ArgumentOutOfRangeException(nameof(choiceReservation));
        Id = id; OccurrenceId = occurrenceId; Retention = retention; Sequence = sequence; State = state; Outcome = outcome;
        ChoiceReservation = choiceReservation;
        if (choices != null) foreach (var pair in choices) _choices[pair.Key] = pair.Value;
    }

    internal void Activate() => State = StoryOccurrenceState.Active;
    internal void Retire(StoryOutcome outcome, IReadOnlyDictionary<string, string>? choices)
    {
        State = StoryOccurrenceState.Retired;
        Outcome = outcome;
        // The recorded choices REPLACE whatever the entry carried; they are never merged into it, so
        // the terminal record is exactly what this outcome declared and can never grow past the
        // bound that was checked for it.
        _choices.Clear();
        // A temporary definition keeps only the bounded idempotency record, never declared choices.
        if (Retention == StoryRetention.Campaign && choices != null)
            foreach (var pair in choices) _choices[pair.Key] = pair.Value;
    }
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
    /// <summary>
    /// Bytes of persisted payload every bound provider owns outright, including the reservations that
    /// guarantee its offered occurrences can still record an outcome. The shares of all
    /// <see cref="StoryProviderBindings.MaxProviders"/> providers plus the header fit inside
    /// <see cref="StoryStateCodec.MaxBytes"/>, so no provider's content can ever make another
    /// provider's offer or outcome fail for space.
    /// </summary>
    internal const int ProviderPayloadBudget = (StoryStateCodec.MaxBytes - StoryStateCodec.HeaderBytes) / StoryProviderBindings.MaxProviders;

    private readonly Dictionary<Guid, StoryOccurrenceEntry> _byOccurrence = new();
    private long _sequence;

    internal int Count => _byOccurrence.Count;

    internal IReadOnlyList<StoryOccurrenceEntry> Entries =>
        _byOccurrence.Values.OrderBy(entry => entry.Sequence).ToArray();

    internal bool TryGet(Guid occurrenceId, out StoryOccurrenceEntry entry) => _byOccurrence.TryGetValue(occurrenceId, out entry!);

    /// <summary>
    /// Records a new offered occurrence and mints its own identity. Never reuses a retired identity.
    /// The occurrence's row AND the worst-case declared-choice payload of its eventual outcome are
    /// reserved from the provider's own budget here, so an accepted offer can always be retired: an
    /// occurrence is never admitted that the API could not finish.
    /// </summary>
    internal StoryLedgerStatus Offer(StoryContentId id, StoryRetention retention, Guid occurrenceId, int choiceReservation, out string diagnostic)
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
        if (retention == StoryRetention.Campaign && CampaignSlotsUsed(id) >= MaxRetainedPerDefinition)
        {
            // Campaign outcomes are never pruned, so an occurrence admitted beyond this bound could
            // never record its outcome. Refused HERE, before anything exists to strand.
            diagnostic = "Definition '" + id + "' already holds " + MaxRetainedPerDefinition
                + " campaign occurrences including unresolved ones; refusing the offer rather than admitting one that could never retire.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (retention != StoryRetention.Campaign && choiceReservation > 0)
        { diagnostic = "Only a campaign definition reserves declared-choice space."; return StoryLedgerStatus.InvalidTransition; }
        var candidate = new StoryOccurrenceEntry(id, occurrenceId, retention, _sequence + 1, choiceReservation: choiceReservation);
        if (ProviderFootprint(id.Provider!) + Footprint(candidate) > ProviderPayloadBudget)
        {
            diagnostic = "Provider '" + id.Provider + "' would exceed its " + ProviderPayloadBudget
                + "-byte payload budget, including the space reserved to record this outcome; refusing before the offer rather than stranding it later.";
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
        // No per-definition bound is applied here: the slot was reserved when the occurrence was
        // offered, so recording ITS outcome is always possible. Checking again would strand it.
        entry.Retire(outcome, choices);
        if (entry.Retention == StoryRetention.Temporary) PruneTemporary(entry.Id);
        return StoryLedgerStatus.Accepted;
    }

    private string? CheckChoices(StoryOccurrenceEntry entry, IReadOnlyDictionary<string, string>? choices)
    {
        if (choices == null || choices.Count == 0) return null;
        if (entry.Retention != StoryRetention.Campaign)
            return "Declared choices are retained for campaign definitions only; '" + entry.Id + "' is temporary.";
        if (choices.Count > StoryMissionDefinition.MaxChoiceKeys)
            return "At most " + StoryMissionDefinition.MaxChoiceKeys + " declared choices per occurrence.";
        int used = 0;
        foreach (var pair in choices)
        {
            // Refused here so the ledger never accepts text its own capture could not write.
            if (string.IsNullOrEmpty(pair.Key) || pair.Value == null
                || !StoryStateCodec.IsEncodable(pair.Key) || !StoryStateCodec.IsEncodable(pair.Value))
                return "A declared choice must be valid non-empty text.";
            int keyBytes = StoryStateCodec.Utf8Bytes(pair.Key), valueBytes = StoryStateCodec.Utf8Bytes(pair.Value);
            if (keyBytes > StoryMissionDefinition.MaxChoiceKeyBytes)
                return "A choice key is at most " + StoryMissionDefinition.MaxChoiceKeyBytes + " encoded bytes.";
            if (valueBytes > StoryMissionDefinition.MaxChoiceValueBytes)
                return "A choice value is at most " + StoryMissionDefinition.MaxChoiceValueBytes + " encoded bytes.";
            used += 2 + keyBytes + 2 + valueBytes;
        }
        // The reservation was taken at OFFER time from the definition as it was declared then. If the
        // definition has since been revised with more or longer keys, a legitimately declared key can
        // still exceed the older reservation: that is refused here without mutating anything, and the
        // outcome remains recordable with fewer or no choices.
        if (used > entry.ChoiceReservation)
            return "These declared choices need " + used + " bytes, above the " + entry.ChoiceReservation
                + " bytes reserved for this occurrence.";
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

    /// <summary>
    /// What one occurrence costs its provider's budget: the row it writes today plus, while it is
    /// still unresolved, the space held back for the outcome it is still allowed to record. A
    /// recorded outcome releases the reservation and pays only for what it actually wrote.
    /// </summary>
    internal static int Footprint(StoryOccurrenceEntry entry)
        => StoryStateCodec.EncodedSize(entry) + (entry.State == StoryOccurrenceState.Retired ? 0 : entry.ChoiceReservation);

    private int ProviderFootprint(string provider)
        => _byOccurrence.Values.Where(entry => entry.Id.Provider == provider).Sum(Footprint);

    private int RetainedFor(StoryContentId id)
        => _byOccurrence.Values.Count(entry => entry.Id == id && entry.State == StoryOccurrenceState.Retired);

    /// <summary>
    /// Campaign slots a definition already holds: retired outcomes AND unresolved occurrences that
    /// still have their outcome to record. The bound covers both, because an unresolved occurrence
    /// is a retirement that must still fit.
    /// </summary>
    private int CampaignSlotsUsed(StoryContentId id)
        => _byOccurrence.Values.Count(entry => entry.Id == id && entry.Retention == StoryRetention.Campaign);

    /// <summary>
    /// Ownership resolution for callers that need the entry before deciding anything else. It uses
    /// exactly the transition rules, including hiding another provider's local ID.
    /// </summary>
    internal StoryLedgerStatus ResolveOwned(StoryContentId caller, Guid occurrenceId, out StoryOccurrenceEntry? entry, out string diagnostic)
        => Resolve(caller, occurrenceId, out entry, out diagnostic);

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

    /// <summary>Offered and active occurrences of one definition, oldest first.</summary>
    internal IReadOnlyList<StoryOccurrenceSnapshot> Unresolved(StoryContentId id)
        => _byOccurrence.Values
            .Where(entry => entry.Id == id && entry.State != StoryOccurrenceState.Retired)
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new StoryOccurrenceSnapshot(entry.Id, entry.OccurrenceId,
                entry.State == StoryOccurrenceState.Active ? StoryOccurrenceStage.Active : StoryOccurrenceStage.Offered,
                entry.Retention))
            .ToArray();

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
        if (rows.Any(row => (row.State == StoryOccurrenceState.Retired) != row.Outcome.HasValue))
            return "A retired occurrence requires exactly one outcome.";
        if (rows.Any(row => row.State != StoryOccurrenceState.Retired && row.Retention != StoryRetention.Campaign && row.ChoiceReservation > 0))
            return "Only a campaign occurrence reserves declared-choice space.";
        // Only a terminal record carries choices. An unresolved row with choices is not a state this
        // ledger can produce, and restoring one would let a later outcome grow past its bound.
        if (rows.Any(row => row.State != StoryOccurrenceState.Retired && row.Choices.Count > 0))
            return "An unresolved story occurrence records no declared choices.";
        foreach (var group in rows.GroupBy(row => row.Id.Provider, StringComparer.Ordinal))
        {
            if (group.Count() > MaxOccurrencesPerProvider) return "Provider '" + group.Key + "' exceeds its occurrence quota.";
            // The reservation is part of the persisted contract: a restored ledger must leave every
            // unresolved occurrence able to record its outcome, exactly as when it was offered.
            if (group.Sum(Footprint) > ProviderPayloadBudget) return "Provider '" + group.Key + "' exceeds its payload budget.";
        }
        foreach (var group in rows.GroupBy(row => row.Id))
        {
            // The same sum the ledger reserves at offer time: retired outcomes plus unresolved
            // occurrences that still have an outcome to record.
            if (group.Count(row => row.Retention == StoryRetention.Campaign) > MaxRetainedPerDefinition)
                return "Definition '" + group.Key + "' exceeds its campaign occurrence cap.";
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
