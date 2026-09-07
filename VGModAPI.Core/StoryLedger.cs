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
    internal const int MaxOccurrences = 2048;
    internal const int MaxRetainedPerDefinition = 64;
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
        if (_byOccurrence.Count >= MaxOccurrences)
        {
            diagnostic = "The ledger holds its maximum of " + MaxOccurrences + " occurrences; nothing was truncated.";
            return StoryLedgerStatus.LimitExceeded;
        }
        _byOccurrence.Add(occurrenceId, new StoryOccurrenceEntry(id, occurrenceId, retention, ++_sequence));
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
        entry.Retire(outcome, choices);
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
        }
        return null;
    }

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

    internal bool IsCompleted(StoryContentId id)
        => _byOccurrence.Values.Any(entry => entry.Id == id && entry.Outcome == StoryOutcome.Completed);

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
