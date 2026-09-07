using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// The API-owned story module: definitions in, automatic persistence out. It registers its OWN
/// persistence provider, so a consumer supplies no codec, no save/load callback and no restoration
/// scheduling for supported story state. Providers keep using the separate save-data API for their
/// additional information; that boundary is documented, not blurred.
///
/// This type is pure with respect to the game: it decides what must be installed and what must be
/// remembered. Driving vanilla registration/reconstruction is the adapter's separate work, so this
/// module alone does not qualify the runtime path.
/// </summary>
internal sealed class StoryContentService : IStoryApi, IDisposable
{
    private sealed class Handle : IStoryRegistration
    {
        private readonly StoryContentService _service;
        private bool _disposed;
        public StoryContentId Id { get; }
        public string NativeIdentifier { get; }
        public bool Active => !_disposed && !_service._disposed && _service._registry.Contains(Id);
        internal Handle(StoryContentService service, StoryContentId id, string identifier)
        { _service = service; Id = id; NativeIdentifier = identifier; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _service.Remove(Id);
        }
    }

    private readonly StoryDefinitionRegistry _registry = new();
    private readonly StoryLedger _ledger = new();
    private readonly Func<Guid> _newOccurrence;
    private readonly IPersistenceRegistration? _persistence;
    private readonly Action<StoryContentId>? _onRemoved;
    private bool _disposed;

    /// <summary>
    /// Registers the module's own persistence provider immediately: the API captures and restores
    /// this state itself. A null persistence API means the module runs without persistence, and
    /// every persistent operation is refused rather than silently accepted.
    /// </summary>
    internal StoryContentService(IPersistenceApi? persistence, Func<Guid>? newOccurrence = null, Action<StoryContentId>? onRemoved = null)
    {
        _newOccurrence = newOccurrence ?? Guid.NewGuid;
        _onRemoved = onRemoved;
        _persistence = persistence?.Register(new PersistenceProvider(
            StoryStateCodec.Owner, StoryStateCodec.SchemaVersion,
            capture: () => StoryStateCodec.Encode(_ledger.Entries),
            restore: (_, bytes) => _ledger.Restore(bytes == null ? Array.Empty<StoryOccurrenceEntry>() : StoryStateCodec.Decode(bytes)),
            validate: StoryStateCodec.Validate));
    }

    /// <summary>True only when this module's own state may currently be mutated and persisted.</summary>
    internal bool PersistenceAvailable => !_disposed && _persistence is { MutationAllowed: true };
    internal string PersistenceStatus => _disposed ? "inactive" : _persistence?.Status ?? "unavailable";
    internal StoryLedger Ledger => _ledger;

    /// <summary>Identifiers that already exist in the world; reserving them keeps registration fail-closed.</summary>
    internal void ReserveExistingIdentifiers(IEnumerable<string> identifiers) => _registry.Reserve(identifiers);

    public StoryRegistrationResult Register(StoryMissionDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (_disposed) return new StoryRegistrationResult(StoryRegistrationStatus.Unavailable, null, "The story module is disposed.");
        var status = _registry.TryRegister(definition, out var diagnostic, out var identifier);
        if (status != StoryRegistrationStatus.Registered) return new StoryRegistrationResult(status, null, diagnostic);
        return new StoryRegistrationResult(status, new Handle(this, definition.Id, identifier), "");
    }

    public IReadOnlyList<StoryOccurrenceRecord> Occurrences(StoryContentId id)
        => _disposed ? Array.Empty<StoryOccurrenceRecord>() : _ledger.Retained(id);

    public bool IsCompleted(StoryContentId id) => !_disposed && _ledger.IsCompleted(id);

    /// <summary>
    /// Records a new offered occurrence and returns its identity. Refused when the module cannot
    /// persist it: an unsaved persistent mission is never silently accepted.
    /// </summary>
    internal StoryLedgerStatus Offer(StoryContentId id, out Guid occurrenceId, out string diagnostic)
    {
        occurrenceId = Guid.Empty;
        if (!Guard(id, out diagnostic, out var definition)) return StoryLedgerStatus.InvalidTransition;
        var minted = _newOccurrence();
        var status = _ledger.Offer(id, definition!.Retention, minted, out diagnostic);
        if (status == StoryLedgerStatus.Accepted) occurrenceId = minted;
        return status;
    }

    internal StoryLedgerStatus Activate(StoryContentId caller, Guid occurrenceId, out string diagnostic)
        => Guard(caller, out diagnostic, out _) ? _ledger.Activate(caller, occurrenceId, out diagnostic) : StoryLedgerStatus.InvalidTransition;

    internal StoryLedgerStatus Retire(StoryContentId caller, Guid occurrenceId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices, out string diagnostic)
        => Guard(caller, out diagnostic, out _) ? _ledger.Retire(caller, occurrenceId, outcome, choices, out diagnostic) : StoryLedgerStatus.InvalidTransition;

    private bool Guard(StoryContentId id, out string diagnostic, out StoryMissionDefinition? definition)
    {
        definition = null;
        if (_disposed) { diagnostic = "The story module is disposed."; return false; }
        if (!_registry.TryGet(id, out var found))
        {
            diagnostic = "Definition '" + id + "' is not registered in this process.";
            return false;
        }
        if (!PersistenceAvailable)
        {
            diagnostic = "Story persistence is unavailable (" + PersistenceStatus + "); refusing to accept unsaved persistent content.";
            return false;
        }
        definition = found;
        diagnostic = "";
        return true;
    }

    private void Remove(StoryContentId id)
    {
        if (_disposed) return;
        // Unregistering stops offering new content. It never rewrites or deletes saved occurrences:
        // removal policy for persisted references belongs to the content-safety contract.
        if (_registry.Unregister(id)) _onRemoved?.Invoke(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _persistence?.Dispose();
        _registry.Clear();
        _ledger.Reset();
    }
}
