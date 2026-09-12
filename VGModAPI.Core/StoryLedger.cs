using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// One state axis shared by the persisted ledger and the surfaced mission object. The game tracks a
/// mission as on the board (offer), in its <c>missions</c> list (with a failed bool), or archived —
/// it has no separate 'resolved' terminal and never persists session conditions like 'game ended'.
/// The three terminal members are therefore named exactly like <see cref="StoryOutcome"/>, and the
/// session/provider conditions (<c>PendingOffering</c>, <c>Withdrawn</c>, <c>Unavailable</c>,
/// <c>GameEnded</c>) live on the orthogonal, derived <see cref="StoryAvailability"/> axis instead.
/// </summary>
internal static class StoryMissionStates
{
    internal static bool IsTerminal(this StoryMissionState state)
        => state is StoryMissionState.Failed or StoryMissionState.Completed or StoryMissionState.Abandoned;

    internal static StoryMissionState FromOutcome(this StoryOutcome outcome) => outcome switch
    {
        StoryOutcome.Completed => StoryMissionState.Completed,
        StoryOutcome.Failed => StoryMissionState.Failed,
        StoryOutcome.Abandoned => StoryMissionState.Abandoned,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    internal static StoryOutcome? AsOutcome(this StoryMissionState state) => state switch
    {
        StoryMissionState.Failed => StoryOutcome.Failed,
        StoryMissionState.Completed => StoryOutcome.Completed,
        StoryMissionState.Abandoned => StoryOutcome.Abandoned,
        _ => null
    };
}

/// <summary>Why a ledger operation was refused. A refusal changes nothing.</summary>
internal enum StoryLedgerStatus
{
    Accepted,
    UnknownMission,
    /// <summary>Another provider's mission: content is owner-scoped, so this never mutates it.</summary>
    ForeignOwner,
    /// <summary>The transition does not follow the recorded state (for example a second terminal outcome).</summary>
    InvalidTransition,
    /// <summary>A bounded limit would be exceeded. Nothing is truncated and no progression is dropped.</summary>
    LimitExceeded
}

internal sealed class StoryMissionEntry
{
    internal StoryMissionDefinitionId Id { get; }
    internal Guid MissionId { get; }
    internal StoryMissionState State { get; private set; }
    internal StoryOutcome? Outcome => State.AsOutcome();
    internal StoryRetention Retention { get; }
    internal long Sequence { get; }
    /// <summary>
    /// Encoded bytes reserved for this mission's declared choices while it is unresolved. It is
    /// taken from the definition when the mission is offered and PERSISTED, so a reload recomputes
    /// exactly the same reservation without the definition having been registered yet.
    /// </summary>
    internal int ChoiceReservation { get; }
    internal StoryObjectiveLayout ObjectiveLayout { get; private set; }
    internal StoryMissionDefinition? RetainedDefinition { get; private set; }
    internal void ReplaceDefinition(StoryMissionDefinition definition) => RetainedDefinition = definition;
    internal void SetObjectiveProgress(string key, int progress) => ObjectiveLayout = ObjectiveLayout.WithProgress(key, progress);
    internal void ReplaceObjectiveLayout(StoryObjectiveLayout layout) => ObjectiveLayout = layout;
    internal StoryMissionEntry WithObjectiveLayout(StoryObjectiveLayout layout, StoryMissionDefinition? definition = null) => new(Id, MissionId, Retention, Sequence,
        State, Choices, ChoiceReservation, PendingChoices, FailureObserved, layout, definition ?? RetainedDefinition);
    internal void ResetObjectiveProgress() => ObjectiveLayout = new StoryObjectiveLayout(ObjectiveLayout.Slots.Select(slot =>
        new StoryObjectiveLayout.Slot(slot.Key, slot.Step, slot.Objective, slot.Kind, slot.Required)), ObjectiveLayout.Revision, ObjectiveLayout.FullyScripted);
    private readonly Dictionary<string, string> _choices = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, string> Choices => _choices;
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    /// <summary>
    /// Choices declared for an outcome the GAME will produce. They are part of this mission's
    /// persisted state, not process memory: a completion can arrive in a later session, and a
    /// declaration made for one save must not travel to another.
    /// </summary>
    internal IReadOnlyDictionary<string, string> PendingChoices => _pending;
    /// <summary>
    /// The game reported this mission failed while it still holds it. That is a fact about the
    /// mission, not a terminal outcome: the game leaves a failed story mission in the player's list
    /// and offers to retry it, so the mission stays live and owned until it is actually resolved.
    /// </summary>
    internal bool FailureObserved { get; private set; }

    internal void DeclarePending(IReadOnlyDictionary<string, string>? choices)
    {
        _pending.Clear();
        if (choices != null) foreach (var pair in choices) _pending[pair.Key] = pair.Value;
    }

    internal void MarkFailureObserved(bool observed) => FailureObserved = observed;

    internal StoryMissionEntry(StoryMissionDefinitionId id, Guid missionId, StoryRetention retention, long sequence,
        StoryMissionState state = StoryMissionState.Offered,
        IEnumerable<KeyValuePair<string, string>>? choices = null, int choiceReservation = 0,
        IEnumerable<KeyValuePair<string, string>>? pendingChoices = null, bool failureObserved = false,
        StoryObjectiveLayout? objectiveLayout = null, StoryMissionDefinition? retainedDefinition = null)
    {
        if (missionId == Guid.Empty) throw new ArgumentException("An mission requires its own identity.", nameof(missionId));
        if (choiceReservation is < 0 or > StoryMissionDefinition.MaxChoiceBytesPerMission)
            throw new ArgumentOutOfRangeException(nameof(choiceReservation));
        Id = id; MissionId = missionId; Retention = retention; Sequence = sequence; State = state;
        ChoiceReservation = choiceReservation;
        ObjectiveLayout = objectiveLayout ?? new StoryObjectiveLayout(Array.Empty<StoryObjectiveLayout.Slot>());
        RetainedDefinition = retainedDefinition;
        if (choices != null) foreach (var pair in choices) _choices[pair.Key] = pair.Value;
        if (pendingChoices != null) foreach (var pair in pendingChoices) _pending[pair.Key] = pair.Value;
        FailureObserved = failureObserved;
    }

    internal void Activate() => State = StoryMissionState.Active;
    internal void Retire(StoryOutcome outcome, IReadOnlyDictionary<string, string>? choices)
    {
        State = outcome.FromOutcome();
        RetainedDefinition = null;
        // The recorded choices REPLACE whatever the entry carried; they are never merged into it, so
        // the terminal record is exactly what this outcome declared and can never grow past the
        // bound that was checked for it.
        _choices.Clear();
        // A temporary definition keeps only the bounded idempotency record, never declared choices.
        if (Retention == StoryRetention.Campaign && choices != null)
            foreach (var pair in choices) _choices[pair.Key] = pair.Value;
        // The declaration is TRANSFERRED into the record, never kept alongside it, so a terminal
        // mission costs no more than the space its outcome was already reserved.
        _pending.Clear();
        FailureObserved = false;
    }
}

/// <summary>
/// Pure mission ledger: the API-owned state that a reload must reconstruct. It holds offered and
/// active missions, authoritative outcomes for campaign-retained definitions and bounded
/// idempotency tombstones for temporary ones. It stores no narrative history, no provider payloads
/// and no vanilla objects.
///
/// Repeated missions of one definition are separate entries with separate identity, because the
/// inspected <c>AddMissionWithLog</c> refuses a duplicate story ID while it is active or archived:
/// a second run is a later mission, never the same one resurrected.
/// </summary>
internal sealed class StoryLedger
{
    /// <summary>Global safety cap. It is a backstop: the per-provider quota below is what a provider may actually use.</summary>
    internal const int MaxMissions = 2048;
    /// <summary>
    /// Every bound provider owns this share of the ledger outright, so one provider's missions can
    /// never make another provider's <see cref="Offer"/> fail. It is exactly
    /// <see cref="MaxMissions"/> / <see cref="StoryProviderBindings.MaxProviders"/>, so the global
    /// cap can never be reached before every provider has spent its own quota, and the provider count
    /// is bounded for the same reason: a new provider is refused rather than handed a share that
    /// would have to come out of another provider's retained history.
    /// </summary>
    internal const int MaxMissionsPerProvider = MaxMissions / StoryProviderBindings.MaxProviders;
    /// <summary>
    /// Highest sequence the codec and the ledger accept. The reserved headroom means a restored
    /// ledger can never wrap: <see cref="Offer"/> refuses at the bound BEFORE mutating anything, so a
    /// capture can never fail on a sequence its own decode accepted and block every owner's saves.
    /// </summary>
    internal const long MaxSequence = long.MaxValue - MaxMissions;
    /// <summary>
    /// Campaign outcomes are never pruned; reaching this bound refuses instead of dropping
    /// progression. It stays BELOW <see cref="MaxMissionsPerProvider"/> so one definition's
    /// history cannot consume its owner's whole share before this bound is reached.
    /// </summary>
    internal const int MaxRetainedPerDefinition = 48;
    /// <summary>
    /// The idempotency horizon for TEMPORARY definitions: the newest terminal tombstones per
    /// definition are retained and older ones are pruned. A pruned mission reports
    /// <see cref="StoryLedgerStatus.UnknownMission"/>; it is never re-offered or re-accepted,
    /// because mission identities are API-generated and never reused.
    /// </summary>
    internal const int TemporaryTombstoneHorizon = 32;
    /// <summary>
    /// Bytes of persisted payload every bound provider owns outright, including the reservations that
    /// guarantee its offered missions can still record an outcome. The shares of all
    /// <see cref="StoryProviderBindings.MaxProviders"/> providers plus the header fit inside
    /// <see cref="StoryStateCodec.MaxBytes"/>, so no provider's content can ever make another
    /// provider's offer or outcome fail for space.
    /// </summary>
    internal const int ProviderPayloadBudget = (StoryStateCodec.MaxBytes - StoryStateCodec.HeaderBytes) / StoryProviderBindings.MaxProviders;
    /// <summary>
    /// The reserved footprint of the WHOLE ledger, header included, that a capture must still fit.
    /// The per-provider shares only add up while the ledger holds at most
    /// <see cref="StoryProviderBindings.MaxProviders"/> provider namespaces, and rows of providers
    /// that are no longer loaded are neither pruned nor bound to a lease, so a restored save can hold
    /// more namespaces than that. This bound is what keeps the guarantee true in that case.
    /// </summary>
    internal const int LedgerPayloadBudget = StoryStateCodec.MaxBytes;

    private readonly Dictionary<Guid, StoryMissionEntry> _byMission = new();
    private long _sequence;

    internal int Count => _byMission.Count;

    internal IReadOnlyList<StoryMissionEntry> Entries =>
        _byMission.Values.OrderBy(entry => entry.Sequence).ToArray();

    internal bool TryGet(Guid missionId, out StoryMissionEntry entry) => _byMission.TryGetValue(missionId, out entry!);

    /// <summary>
    /// Records a new offered mission and mints its own identity. Never reuses a retired identity.
    /// The mission's row AND the worst-case declared-choice payload of its eventual outcome are
    /// reserved from the provider's own budget here, so an accepted offer can always be retired: an
    /// mission is never admitted that the API could not finish.
    /// </summary>
    internal StoryLedgerStatus Offer(StoryMissionDefinitionId id, StoryRetention retention, Guid missionId, int choiceReservation, out string diagnostic, StoryObjectiveLayout? objectiveLayout = null, StoryMissionDefinition? retainedDefinition = null)
    {
        diagnostic = "";
        if (missionId == Guid.Empty) { diagnostic = "An mission requires its own identity."; return StoryLedgerStatus.InvalidTransition; }
        if (_byMission.ContainsKey(missionId)) { diagnostic = "That mission identity already exists."; return StoryLedgerStatus.InvalidTransition; }
        if (_byMission.Values.Count(entry => entry.Id.Provider == id.Provider) >= MaxMissionsPerProvider)
        {
            diagnostic = "Provider '" + id.Provider + "' holds its quota of " + MaxMissionsPerProvider
                + " missions; nothing was truncated and no other provider is affected.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (_byMission.Count >= MaxMissions)
        {
            diagnostic = "The ledger holds its maximum of " + MaxMissions + " missions; nothing was truncated.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (_sequence >= MaxSequence)
        {
            // Refused BEFORE any mutation, so the ledger stays capturable instead of overflowing.
            diagnostic = "The mission sequence reached its bound; refusing rather than wrapping the timeline.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (retention == StoryRetention.Campaign && CampaignSlotsUsed(id) >= MaxRetainedPerDefinition)
        {
            // Campaign outcomes are never pruned, so an mission admitted beyond this bound could
            // never record its outcome. Refused HERE, before anything exists to strand.
            diagnostic = "Definition '" + id + "' already holds " + MaxRetainedPerDefinition
                + " campaign missions including unresolved ones; refusing the offer rather than admitting one that could never retire.";
            return StoryLedgerStatus.LimitExceeded;
        }
        if (retention != StoryRetention.Campaign && choiceReservation > 0)
        { diagnostic = "Only a campaign definition reserves declared-choice space."; return StoryLedgerStatus.InvalidTransition; }
        var candidate = new StoryMissionEntry(id, missionId, retention, _sequence + 1, choiceReservation: choiceReservation, objectiveLayout: objectiveLayout, retainedDefinition: retainedDefinition);
        if (ProviderFootprint(id.Provider!) + Footprint(candidate) > ProviderPayloadBudget)
        {
            diagnostic = "Provider '" + id.Provider + "' would exceed its " + ProviderPayloadBudget
                + "-byte payload budget, including the space reserved to record this outcome; refusing before the offer rather than stranding it later.";
            return StoryLedgerStatus.LimitExceeded;
        }
        // Backstop for a save whose ledger holds MORE provider namespaces than can be bound at once:
        // rows of providers that are no longer loaded still occupy the payload and are never pruned,
        // so the per-provider shares alone would not add up. Refusing here keeps every mission that
        // WAS admitted able to record its outcome, instead of failing a later capture and blocking
        // coordinated saves for every registered mod.
        if (ReservedFootprint() + Footprint(candidate) > LedgerPayloadBudget)
        {
            diagnostic = "The story state, including the space reserved to record outcomes for missions already admitted, "
                + "would exceed its " + LedgerPayloadBudget + "-byte payload; refusing the offer rather than failing a later capture.";
            return StoryLedgerStatus.LimitExceeded;
        }
        _sequence++;
        _byMission.Add(missionId, candidate);
        return StoryLedgerStatus.Accepted;
    }

    internal StoryLedgerStatus Activate(StoryMissionDefinitionId caller, Guid missionId, out string diagnostic)
    {
        var status = Resolve(caller, missionId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State != StoryMissionState.Offered)
        {
            diagnostic = "Only an offered mission becomes active; this one is " + entry.State + ".";
            return StoryLedgerStatus.InvalidTransition;
        }
        entry.Activate();
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Removes an OFFERED mission that was never accepted. Nothing was ever reconstructed for it,
    /// so it leaves no tombstone; an active or retired mission is refused.
    /// </summary>
    internal StoryLedgerStatus Withdraw(StoryMissionDefinitionId caller, Guid missionId, out string diagnostic)
    {
        var status = Resolve(caller, missionId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State != StoryMissionState.Offered)
        {
            diagnostic = "Only an offered mission can be withdrawn; this one is " + entry.State + ".";
            return StoryLedgerStatus.InvalidTransition;
        }
        _byMission.Remove(missionId);
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Records the terminal outcome once. A second terminal call is refused rather than rewriting an
    /// authoritative result, and a temporary definition retains only the tombstone.
    /// </summary>
    /// <summary>
    /// Every check <see cref="Retire"/> makes, with NO mutation. It exists because the world is
    /// changed before the record is written: a refusal discovered after the mission was already
    /// abandoned in the game would leave the two disagreeing, so the whole retirement is validated
    /// first and the world is only touched once it is known to be recordable.
    /// </summary>
    internal StoryLedgerStatus CanRetire(StoryMissionDefinitionId caller, Guid missionId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices, out string diagnostic)
    {
        var status = Resolve(caller, missionId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (!Enum.IsDefined(typeof(StoryOutcome), outcome)) { diagnostic = "Unknown outcome."; return StoryLedgerStatus.InvalidTransition; }
        if (entry!.State.IsTerminal())
        {
            diagnostic = "This mission already reported " + entry.Outcome + "; an outcome is recorded once.";
            return StoryLedgerStatus.InvalidTransition;
        }
        var precondition = CheckChoices(entry, choices);
        if (precondition != null) { diagnostic = precondition; return StoryLedgerStatus.LimitExceeded; }
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Stages the choices a future outcome will carry, validated exactly as a retirement validates
    /// them so the outcome can always be recorded with them. Declaring again REPLACES the previous
    /// declaration; nothing accumulates.
    /// </summary>
    internal StoryLedgerStatus DeclareChoices(StoryMissionDefinitionId caller, Guid missionId,
        IReadOnlyDictionary<string, string>? choices, out string diagnostic)
    {
        var status = CanRetire(caller, missionId, StoryOutcome.Completed, choices, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        Resolve(caller, missionId, out var entry, out _);
        if (entry == null) { diagnostic = "Unknown mission."; return StoryLedgerStatus.UnknownMission; }
        entry.DeclarePending(choices);
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Records that the game reported this mission failed while still holding it. It is not an
    /// outcome: it is remembered so the removal that eventually follows can be attributed.
    /// </summary>
    internal StoryLedgerStatus ObserveFailure(StoryMissionDefinitionId caller, Guid missionId, out string diagnostic)
    {
        var status = Resolve(caller, missionId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State.IsTerminal())
        { diagnostic = "This mission already reported " + entry.Outcome + "."; return StoryLedgerStatus.InvalidTransition; }
        entry.MarkFailureObserved(true);
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>
    /// Clears a reported failure, because the game accepted this mission again. Only a verified
    /// re-acceptance clears it; nothing else forgets that the game once failed this mission.
    /// </summary>
    internal bool CanReplaceObjectiveLayout(StoryMissionEntry entry, StoryObjectiveLayout layout, StoryMissionDefinition? definition = null)
    {
        if (!_byMission.TryGetValue(entry.MissionId, out var current) || !ReferenceEquals(current, entry)) return false;
        if (definition != null && (definition.Retention != entry.Retention || definition.ReservedChoiceBytes != entry.ChoiceReservation
            || (entry.RetainedDefinition != null && !entry.RetainedDefinition.ChoiceKeys.SequenceEqual(definition.ChoiceKeys)))) return false;
        var candidate = entry.WithObjectiveLayout(layout, definition);
        int growth;
        try { growth = Footprint(candidate) - Footprint(entry); }
        catch (Exception error) when (error is System.IO.InvalidDataException || error is System.Text.EncoderFallbackException) { return false; }
        return ProviderFootprint(entry.Id.Provider!) + growth <= ProviderPayloadBudget
            && ReservedFootprint() + growth <= LedgerPayloadBudget;
    }

    internal StoryLedgerStatus ClearFailure(StoryMissionDefinitionId caller, Guid missionId, out string diagnostic)
    {
        var status = Resolve(caller, missionId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State.IsTerminal())
        { diagnostic = "This mission already reported " + entry.Outcome + "."; return StoryLedgerStatus.InvalidTransition; }
        entry.MarkFailureObserved(false);
        entry.ResetObjectiveProgress();
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>Every check <see cref="Activate"/> makes, with no mutation, for the same reason.</summary>
    internal StoryLedgerStatus CanActivate(StoryMissionDefinitionId caller, Guid missionId, out string diagnostic)
    {
        var status = Resolve(caller, missionId, out var entry, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        if (entry!.State != StoryMissionState.Offered)
        {
            diagnostic = "Only an offered mission becomes active; this one is " + entry.State + ".";
            return StoryLedgerStatus.InvalidTransition;
        }
        return StoryLedgerStatus.Accepted;
    }

    internal StoryLedgerStatus Retire(StoryMissionDefinitionId caller, Guid missionId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices, out string diagnostic)
    {
        var status = CanRetire(caller, missionId, outcome, choices, out diagnostic);
        if (status != StoryLedgerStatus.Accepted) return status;
        Resolve(caller, missionId, out var entry, out _);
        if (entry == null) { diagnostic = "Unknown mission."; return StoryLedgerStatus.UnknownMission; }
        // No per-definition bound is applied here: the slot was reserved when the mission was
        // offered, so recording ITS outcome is always possible. Checking again would strand it.
        entry.Retire(outcome, choices);
        if (entry.Retention == StoryRetention.Temporary) PruneTemporary(entry.Id);
        return StoryLedgerStatus.Accepted;
    }

    private string? CheckChoices(StoryMissionEntry entry, IReadOnlyDictionary<string, string>? choices)
    {
        if (choices == null || choices.Count == 0) return null;
        if (entry.Retention != StoryRetention.Campaign)
            return "Declared choices are retained for campaign definitions only; '" + entry.Id + "' is temporary.";
        if (choices.Count > StoryMissionDefinition.MaxChoiceKeys)
            return "At most " + StoryMissionDefinition.MaxChoiceKeys + " declared choices per mission.";
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
                + " bytes reserved for this mission.";
        return null;
    }

    /// <summary>
    /// Keeps the newest <see cref="TemporaryTombstoneHorizon"/> terminal tombstones of a temporary
    /// definition. Only TERMINAL TEMPORARY entries are pruned: offered and active missions are
    /// still needed to reconstruct live content, and campaign outcomes/choices are never removed.
    /// This is a bounded horizon, not a time-based purge.
    /// </summary>
    private void PruneTemporary(StoryMissionDefinitionId id)
    {
        var terminal = _byMission.Values
            .Where(entry => entry.Id == id && entry.Retention == StoryRetention.Temporary && entry.State.IsTerminal())
            .OrderByDescending(entry => entry.Sequence)
            .Skip(TemporaryTombstoneHorizon)
            .ToArray();
        foreach (var entry in terminal) _byMission.Remove(entry.MissionId);
    }

    /// <summary>
    /// What one mission costs its provider's budget: the row it writes today plus, while it is
    /// still unresolved, the space held back for the outcome it is still allowed to record. A
    /// recorded outcome releases the reservation and pays only for what it actually wrote.
    /// </summary>
    internal static int Footprint(StoryMissionEntry entry)
        // Pending choices are already written into the row, so only the UNUSED part of the
        // reservation is still held back; counting the whole reservation as well would double-count.
        => StoryStateCodec.EncodedSize(entry)
           + (entry.State.IsTerminal() ? 0 : entry.ChoiceReservation - StoryStateCodec.PendingSize(entry));

    private int ProviderFootprint(string provider)
        => _byMission.Values.Where(entry => entry.Id.Provider == provider).Sum(Footprint);

    /// <summary>What a capture of this ledger must fit today, plus what every unresolved mission still holds back.</summary>
    private int ReservedFootprint() => StoryStateCodec.HeaderBytes + _byMission.Values.Sum(Footprint);

    /// <summary>
    /// Campaign slots a definition already holds: retired outcomes AND unresolved missions that
    /// still have their outcome to record. The bound covers both, because an unresolved mission
    /// is a retirement that must still fit.
    /// </summary>
    private int CampaignSlotsUsed(StoryMissionDefinitionId id)
        => _byMission.Values.Count(entry => entry.Id == id && entry.Retention == StoryRetention.Campaign);

    /// <summary>
    /// Ownership resolution for callers that need the entry before deciding anything else. It uses
    /// exactly the transition rules, including hiding another provider's local ID.
    /// </summary>
    internal StoryLedgerStatus ResolveOwned(StoryMissionDefinitionId caller, Guid missionId, out StoryMissionEntry? entry, out string diagnostic)
        => Resolve(caller, missionId, out entry, out diagnostic);

    private StoryLedgerStatus Resolve(StoryMissionDefinitionId caller, Guid missionId, out StoryMissionEntry? entry, out string diagnostic)
    {
        diagnostic = "";
        if (!_byMission.TryGetValue(missionId, out entry))
        {
            diagnostic = "Unknown mission.";
            return StoryLedgerStatus.UnknownMission;
        }
        if (entry.Id.Provider != caller.Provider)
        {
            // Deliberately reports foreign ownership without revealing the other provider's local ID.
            diagnostic = "Mission belongs to another provider.";
            entry = null;
            return StoryLedgerStatus.ForeignOwner;
        }
        if (entry.Id != caller)
        {
            diagnostic = "Mission belongs to a different definition of this provider.";
            entry = null;
            return StoryLedgerStatus.ForeignOwner;
        }
        return StoryLedgerStatus.Accepted;
    }

    /// <summary>Offered and active missions of one definition, oldest first.</summary>
    internal IReadOnlyList<StoryMissionSnapshot> Unresolved(StoryMissionDefinitionId id)
        => _byMission.Values
            .Where(entry => entry.Id == id && !entry.State.IsTerminal())
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new StoryMissionSnapshot(entry.Id, entry.MissionId,
                entry.State == StoryMissionState.Active ? StoryMissionStage.Active : StoryMissionStage.Offered,
                entry.Retention))
            .ToArray();

    /// <summary>Authoritative retained outcomes for one definition, oldest first.</summary>
    internal IReadOnlyList<StoryMissionRecord> Retained(StoryMissionDefinitionId id)
        => _byMission.Values
            .Where(entry => entry.Id == id && entry.State.IsTerminal())
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new StoryMissionRecord(entry.Id, entry.MissionId, entry.Outcome,
                entry.Retention == StoryRetention.Campaign ? entry.Choices.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : null))
            .ToArray();

    /// <summary>
    /// Authoritative CAMPAIGN completion. A temporary definition keeps a bounded idempotency
    /// tombstone, not an authoritative outcome, so it never answers true here.
    /// </summary>
    internal bool IsCompleted(StoryMissionDefinitionId id)
        => _byMission.Values.Any(entry => entry.Id == id && entry.Retention == StoryRetention.Campaign
            && entry.Outcome == StoryOutcome.Completed);

    /// <summary>
    /// The bounds a persisted record set must satisfy, shared by the encoder and the decoder so the
    /// codec is symmetric: a payload can never restore a ledger that this type's own operations would
    /// have refused, and a capture can never produce one either. A violating payload is refused,
    /// which protects the owner's retained bytes rather than silently pruning them.
    /// </summary>
    internal static string? RefuseBounds(IReadOnlyList<StoryMissionEntry> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        if (rows.Count > MaxMissions) return "Too many story missions: " + rows.Count + ".";
        foreach (var row in rows)
        {
            var definition = row.RetainedDefinition;
            if (definition == null) continue;
            if (row.State.IsTerminal() || definition.LocalId != row.Id.LocalId || definition.Retention != row.Retention
                || definition.ReservedChoiceBytes != row.ChoiceReservation
                || !row.ObjectiveLayout.SamePositions(new StoryObjectiveLayout(definition))
                || definition.Steps.SelectMany(step => step.Objectives).Any(objective => StoryMissionPolicy.RefuseObjective(objective.Kind) != null))
                return "Retained definition does not match its mission.";
        }
        if (StoryStateCodec.HeaderBytes + rows.Sum(StoryStateCodec.EncodedSize) > StoryStateCodec.MaxBytes)
            return "Story state exceeds its bounded payload size.";
        // The RESERVED footprint, not just today's bytes: a restored ledger must still be able to
        // record the outcome of every mission it restores, however many provider namespaces it
        // holds. Encoded size alone would admit a save that can never finish its own content.
        if (StoryStateCodec.HeaderBytes + rows.Sum(Footprint) > LedgerPayloadBudget)
            return "Story state reserves more than its bounded payload for outcomes still to be recorded.";
        if (rows.Any(row => row.Sequence < 1 || row.Sequence > MaxSequence)) return "Story mission sequence out of range.";
        if (rows.Select(row => row.Sequence).Distinct().Count() != rows.Count) return "Story mission sequences must be unique.";
        if (rows.Select(row => row.MissionId).Distinct().Count() != rows.Count) return "Duplicate story mission identity.";
        if (rows.Any(row => (row.State.IsTerminal()) != row.Outcome.HasValue))
            return "A retired mission requires exactly one outcome.";
        if (rows.Any(row => !row.State.IsTerminal() && row.Retention != StoryRetention.Campaign && row.ChoiceReservation > 0))
            return "Only a campaign mission reserves declared-choice space.";
        // Only a terminal record carries choices. An unresolved row with choices is not a state this
        // ledger can produce, and restoring one would let a later outcome grow past its bound.
        if (rows.Any(row => !row.State.IsTerminal() && row.Choices.Count > 0))
            return "An unresolved story mission records no declared choices.";
        // Pending declarations are the mirror image: only an unresolved campaign mission can hold
        // them, and never more than the space its own outcome already reserved.
        if (rows.Any(row => row.PendingChoices.Count > 0
            && (row.State.IsTerminal() || row.Retention != StoryRetention.Campaign)))
            return "Only an unresolved campaign mission holds declared choices for a future outcome.";
        if (rows.Any(row => StoryStateCodec.PendingSize(row) > row.ChoiceReservation))
            return "Declared choices exceed the space reserved for this mission's outcome.";
        if (rows.Any(row => row.FailureObserved && row.State.IsTerminal()))
            return "A retired mission carries no unresolved failure.";
        foreach (var group in rows.GroupBy(row => row.Id.Provider, StringComparer.Ordinal))
        {
            if (group.Count() > MaxMissionsPerProvider) return "Provider '" + group.Key + "' exceeds its mission quota.";
            // The reservation is part of the persisted contract: a restored ledger must leave every
            // unresolved mission able to record its outcome, exactly as when it was offered.
            if (group.Sum(Footprint) > ProviderPayloadBudget) return "Provider '" + group.Key + "' exceeds its payload budget.";
        }
        foreach (var group in rows.GroupBy(row => row.Id))
        {
            // The same sum the ledger reserves at offer time: retired outcomes plus unresolved
            // missions that still have an outcome to record.
            if (group.Count(row => row.Retention == StoryRetention.Campaign) > MaxRetainedPerDefinition)
                return "Definition '" + group.Key + "' exceeds its campaign mission cap.";
            if (group.Any(row => row.Retention == StoryRetention.Temporary)
                && group.Count(row => row.Retention == StoryRetention.Temporary && row.State.IsTerminal()) > TemporaryTombstoneHorizon)
                return "Definition '" + group.Key + "' exceeds its temporary tombstone horizon.";
        }
        return null;
    }

    /// <summary>Replaces the whole ledger with restored state; a load never merges a newer snapshot into an older save.</summary>
    internal void Restore(IEnumerable<StoryMissionEntry> entries)
    {
        _byMission.Clear();
        _sequence = 0;
        foreach (var entry in entries ?? throw new ArgumentNullException(nameof(entries)))
        {
            _byMission[entry.MissionId] = entry;
            if (entry.Sequence > _sequence) _sequence = entry.Sequence;
        }
    }

    internal void Reset() => Restore(Array.Empty<StoryMissionEntry>());
}
